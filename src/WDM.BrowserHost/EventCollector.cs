using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.Threading;
using CefSharp;
using CefSharp.Handler;
using WDM.Browser.Ipc;
using WDM.Browser.Models;

namespace WDM.BrowserHost;

/// <summary>CEF request interception → normalized <see cref="BrowserNetworkEvent"/>s.
/// Headers only, bodies never — a body is fetched solely on FetchBody command.
/// Caps: drops events past the budget instead of growing without bound.</summary>
internal sealed class EventCollector : RequestHandler
{
    private readonly List<BrowserNetworkEvent> _pending = new();
    private readonly object _gate = new();
    private readonly int _maxEvents;
    private long _dropped;
    private long _sequence;

    internal EventCollector(int maxEvents) => _maxEvents = Math.Max(100, maxEvents);

    internal List<BrowserNetworkEvent> Drain()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
                return new List<BrowserNetworkEvent>();
            var batch = new List<BrowserNetworkEvent>(_pending);
            _pending.Clear();
            return batch;
        }
    }

    internal long Dropped => Interlocked.Read(ref _dropped);

    internal event Action<CefTerminationStatus>? RenderTerminated;

    protected override void OnRenderProcessTerminated(IWebBrowser chromiumWebBrowser, IBrowser browser,
        CefTerminationStatus status, int errorCode, string errorMessage)
    {
        try { RenderTerminated?.Invoke(status); } catch { }
    }

    protected override IResourceRequestHandler GetResourceRequestHandler(IWebBrowser chromiumWebBrowser,
        IBrowser browser, IFrame frame, IRequest request, bool isNavigation, bool isDownload,
        string requestInitiator, ref bool disableDefaultHandling)
    {
        var entry = new CollectedEvent
        {
            RequestId = request.Identifier.ToString(CultureInfo.InvariantCulture),
            Timestamp = DateTimeOffset.UtcNow,
            Url = request.Url,
            Method = request.Method,
            RequestHeaders = ToDict(request.Headers),
            ResourceType = request.ResourceType.ToString(),
            FrameId = frame?.Identifier,
            Initiator = string.IsNullOrWhiteSpace(requestInitiator) ? null : requestInitiator,
            Sequence = Interlocked.Increment(ref _sequence),
        };
        return new EventResourceHandler(this, entry);
    }

    private void Publish(CollectedEvent entry, IResponse? response)
    {
        // Exactly once: normal responses reach both OnResourceResponse and
        // OnResourceLoadComplete; cache hits and errors may reach only the
        // latter. Both callbacks run on the CEF IO thread; exchange anyway.
        if (Interlocked.Exchange(ref entry.Published, 1) == 1)
            return;
        var responseHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response?.Headers is NameValueCollection headers)
        {
            foreach (string? key in headers.AllKeys)
            {
                if (key is null)
                    continue;
                responseHeaders[key] = headers[key] ?? "";
            }
        }
        var evt = new BrowserNetworkEvent
        {
            RequestId = entry.RequestId,
            Timestamp = entry.Timestamp,
            Url = entry.Url,
            Method = entry.Method,
            RequestHeaders = BrowserMessage.SanitizeHeaders(entry.RequestHeaders),
            ResponseHeaders = BrowserMessage.SanitizeHeaders(responseHeaders),
            StatusCode = response?.StatusCode ?? 0,
            ContentType = response?.MimeType,
            ContentLength = null,
            ResourceType = entry.ResourceType,
            FrameId = entry.FrameId,
            Initiator = entry.Initiator,
        };
        lock (_gate)
        {
            if (_pending.Count >= _maxEvents)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
            _pending.Add(evt);
        }
    }

    private static Dictionary<string, string> ToDict(NameValueCollection? headers)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (headers is null)
            return dict;
        foreach (string? key in headers.AllKeys)
        {
            if (!string.IsNullOrWhiteSpace(key))
                dict[key] = headers[key] ?? "";
        }
        return dict;
    }

    private sealed class CollectedEvent
    {
        internal string RequestId = "";
        internal DateTimeOffset Timestamp;
        internal string Url = "";
        internal string Method = "GET";
        internal Dictionary<string, string> RequestHeaders = new(StringComparer.OrdinalIgnoreCase);
        internal string ResourceType = "";
        internal string? FrameId;
        internal string? Initiator;
        internal long Sequence;
        internal int Published;
    }

    private sealed class EventResourceHandler : ResourceRequestHandler
    {
        private readonly EventCollector _owner;
        private readonly CollectedEvent _entry;

        internal EventResourceHandler(EventCollector owner, CollectedEvent entry)
        {
            _owner = owner;
            _entry = entry;
        }

        protected override bool OnResourceResponse(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request, IResponse response)
        {
            _owner.Publish(_entry, response);
            return false;
        }

        protected override void OnResourceLoadComplete(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IRequest request, IResponse response, UrlRequestStatus status,
            long receivedContentLength)
        {
            _owner.Publish(_entry, response);
        }
    }
}
