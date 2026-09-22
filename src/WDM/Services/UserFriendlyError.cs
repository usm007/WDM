using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Windows;

namespace WDM.Services;

/// <summary>Single policy for every user-visible error: short, plain-language
/// sentences a non-technical user can act on. Never stack traces, HRESULTs
/// (e.g. 0x80004004), exception type names, or raw server text.
/// Technical details are written to wdm_error.log via <see cref="App.LogException"/>
/// — never shown on screen.</summary>
public static class UserFriendlyError
{
    private const int EAbort = unchecked((int)0x80004004);
    private const int ErrorDiskFull = 0x70;
    private const int ErrorHandleDiskFull = 0x27;

    /// <summary>Plain-language one-liner for any exception.</summary>
    public static string For(Exception? ex)
    {
        if (ex is null)
            return "Something went wrong. Please try again.";

        // Curated engine exceptions already carry display-ready text.
        if (ex is DownloadEngine.CloudflareBlockedException
            or DownloadEngine.FileChangedException
            or DownloadEngine.HtmlPageException)
            return ex.Message;

        if (ex is OperationCanceledException)
            return "The action was cancelled.";

        if (ex is TimeoutException)
            return "The connection timed out. Check your internet connection and try again.";

        if (ex is UriFormatException)
            return "That web address doesn't look valid. Check it and try again.";

        if (ex is PathTooLongException)
            return "The file path is too long. Try saving to a shorter location.";

        if (ex is FileNotFoundException or DirectoryNotFoundException)
            return "The file or folder couldn't be found. It may have been moved or deleted.";

        if (ex is UnauthorizedAccessException)
            return "WDM doesn't have permission to use that file or folder. Try a different location.";

        if (ex is IOException ioEx)
            return ForIoError(ioEx);

        if (ex is SocketException)
            return "Couldn't reach the server. Check your internet connection and try again.";

        if (ex is HttpRequestException httpEx)
            return ForHttpError(httpEx.StatusCode, httpEx.InnerException);

        if (ex is COMException comEx && comEx.ErrorCode == EAbort)
            return "The browser part closed unexpectedly. Please try again.";

        if (ex.InnerException is not null)
        {
            string inner = For(ex.InnerException);
            if (!string.Equals(inner, Fallback, StringComparison.Ordinal))
                return inner;
        }

        return Fallback;
    }

    private const string Fallback = "Something went wrong. Please try again.";

    private static string ForIoError(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        if (code == ErrorDiskFull || code == ErrorHandleDiskFull)
            return "There isn't enough free space on the disk. Free some space and try again.";
        return "A file error occurred. The file may be in use by another program.";
    }

    private static string ForHttpError(HttpStatusCode? status, Exception? inner)
    {
        if (status.HasValue)
            return ForStatusCode((int)status.Value);
        if (inner is SocketException)
            return "Couldn't reach the server. Check your internet connection and try again.";
        if (inner is TimeoutException)
            return "The connection timed out. Check your internet connection and try again.";
        return "Couldn't reach the server. Check your internet connection and try again.";
    }

    /// <summary>Plain-language one-liner for an HTTP status code.</summary>
    public static string ForStatusCode(int statusCode) => statusCode switch
    {
        400 => "The server rejected the request. The link may be broken.",
        401 or 403 => "The server refused access. The link may need sign-in or has expired.",
        404 => "The file couldn't be found on the server. The link may have expired — try refreshing it.",
        408 or 504 => "The connection timed out. Check your internet connection and try again.",
        410 => "The file is no longer on the server. The link has expired.",
        416 => "The server can't resume this download. Try restarting it from the beginning.",
        429 => "The server is busy. Please wait a bit and try again.",
        >= 500 => "The server had a problem. Try again later.",
        _ => "The download failed. Please try again.",
    };

    /// <summary>Compact list-row text — "Error: 403 Forbidden", never a
    /// sentence. The row has no room; the full guidance stays available via
    /// <see cref="For"/> (tooltip / dialogs / log).</summary>
    public static string ForDownload(Exception? ex, int? statusCode = null)
    {
        int? code = statusCode ?? FindStatusCode(ex);
        if (code.HasValue)
        {
            string phrase = ReasonPhrase(code.Value);
            return string.IsNullOrEmpty(phrase) ? $"Error: {code.Value}" : $"Error: {code.Value} {phrase}";
        }
        if (ex is DownloadEngine.HtmlPageException)
            return "Error: web page, not a file";
        if (ex is DownloadEngine.TorrentNotSupportedException)
            return "Error: torrents not supported";
        if (ex is DownloadEngine.CloudflareBlockedException)
            return "Error: Cloudflare blocked";
        if (ex is DownloadEngine.FileChangedException)
            return "Error: file changed on server";
        if (ex is HlsDownloader.HlsPackagedStreamException packEx)
            return $"Error: {packEx.Method} needs ffmpeg";
        if (ex is TimeoutException)
            return "Error: timed out";
        if (ex is SocketException || ex is HttpRequestException || ex is UriFormatException)
            return "Error: couldn't reach server";
        if (ex is UnauthorizedAccessException)
            return "Error: access denied";
        if (ex is IOException ioEx && (ioEx.HResult & 0xFFFF) is ErrorDiskFull or ErrorHandleDiskFull)
            return "Error: disk full";
        if (ex is IOException)
            return "Error: file error";
        if (ex is OperationCanceledException)
            return "Cancelled";
        if (ex?.InnerException is not null)
            return ForDownload(ex.InnerException);
        return "Error: download failed";
    }

    private static int? FindStatusCode(Exception? ex)
    {
        while (ex is not null)
        {
            if (ex is HttpRequestException httpEx && httpEx.StatusCode.HasValue)
                return (int)httpEx.StatusCode.Value;
            ex = ex.InnerException;
        }
        return null;
    }

    private static string ReasonPhrase(int code) => code switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        408 => "Request Timeout",
        410 => "Gone",
        416 => "Range Not Satisfiable",
        429 => "Too Many Requests",
        500 => "Internal Server Error",
        502 => "Bad Gateway",
        503 => "Service Unavailable",
        504 => "Gateway Timeout",
        _ => "",
    };

    /// <summary>Logs the technical details and shows a small friendly error box.</summary>
    public static MessageBoxResult ShowError(Window? owner, string title, Exception ex)
    {
        try { App.LogException(ex); } catch { }
        return ShowBox(owner, title, For(ex), MessageBoxImage.Error);
    }

    /// <summary>Shows a small friendly error box (message already plain-language).</summary>
    public static MessageBoxResult ShowError(Window? owner, string title, string message)
        => ShowBox(owner, title, message, MessageBoxImage.Error);

    /// <summary>Shows a small friendly warning box (message already plain-language).</summary>
    public static MessageBoxResult ShowWarning(Window? owner, string title, string message)
        => ShowBox(owner, title, message, MessageBoxImage.Warning);

    private static MessageBoxResult ShowBox(Window? owner, string title, string message, MessageBoxImage icon)
    {
        try
        {
            if (owner is not null)
                return MessageBox.Show(owner, message, title, MessageBoxButton.OK, icon);
            return MessageBox.Show(message, title, MessageBoxButton.OK, icon);
        }
        catch
        {
            return MessageBoxResult.None;
        }
    }
}
