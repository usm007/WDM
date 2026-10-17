namespace Redm.Core;

/// <summary>UI string tables with English fallback. Only the main chrome is
/// localized today; deep dialogs remain English (see limitations).</summary>
public sealed class Localizer
{
    private readonly Dictionary<string, string> _en;
    private Dictionary<string, string> _active;

    public string Language { get; private set; } = "en";

    public Localizer()
    {
        _en = LoadEmbedded("en");
        _active = _en;
    }

    public void SetLanguage(string lang)
    {
        Language = lang;
        try { _active = lang == "en" ? _en : LoadEmbedded(lang); }
        catch { _active = _en; Language = "en"; }
    }

    public string Get(string key)
    {
        if (_active.TryGetValue(key, out string? v)) return v;
        return _en.TryGetValue(key, out string? e) ? e : key;
    }

    public IReadOnlyCollection<string> Keys => _en.Keys;

    public static Dictionary<string, string> Parse(string json)
    {
        var d = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(json);
        return d ?? new();
    }

    private static Dictionary<string, string> LoadEmbedded(string lang)
    {
        var asm = typeof(Localizer).Assembly;
        string name = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"strings-{lang}.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No embedded locale '{lang}'.");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd());
    }
}
