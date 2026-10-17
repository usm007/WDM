namespace Redm.Core;

/// <summary>Tray, clipboard, drag-drop and hotkey behavior contracts.</summary>
public static class ShellBehavior
{
    public const string SpecialKeyDefault = "Insert";
    public const string AltBypass = "ALT bypasses takeover";
    public static bool ShouldCatch(string url, bool altHeld, bool specialHeld, IEnumerable<string> types)
        => altHeld ? false : (specialHeld || FileTypes.ShouldCatch(url, types));
}
