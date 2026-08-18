using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace WDM.Services;

/// <summary>
/// Loads the app icon (the one Windows shows in the taskbar / on the exe) once and
/// exposes it for every surface: taskbar tile, window title bars, tray, and About box.
/// </summary>
internal static class AppIcon
{
    private static readonly System.Drawing.Icon? _trayIcon = LoadTrayIcon();
    private static readonly BitmapSource? _logo = LoadLogo();

    /// <summary>Best-effort 32x32 System.Drawing.Icon for the tray NotifyIcon.</summary>
    public static System.Drawing.Icon? Tray => _trayIcon;

    /// <summary>High-res BitmapSource for in-app display (About dialog logo).</summary>
    public static BitmapSource? Logo => _logo;

    private static byte[]? ReadIcoBytes()
    {
        // 1) Embedded resource (pack URI).
        try
        {
            var uri = new Uri("pack://application:,,,/Assets/WDM.ico", UriKind.Absolute);
            Stream? s = Application.GetResourceStream(uri)?.Stream;
            if (s is not null)
            {
                using (s)
                {
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    if (ms.Length > 0)
                        return ms.ToArray();
                }
            }
        }
        catch
        {
            // Fall through.
        }

        // 2) Loose file next to the exe.
        try
        {
            string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "WDM.ico");
            if (File.Exists(path))
                return File.ReadAllBytes(path);
        }
        catch
        {
            // Fall through.
        }

        return null;
    }

    private static System.Drawing.Icon? LoadTrayIcon()
    {
        byte[]? ico = ReadIcoBytes();
        if (ico is not null)
        {
            try
            {
                using var ms = new MemoryStream(ico);
                return new System.Drawing.Icon(ms);
            }
            catch
            {
                // Fall through to exe extraction.
            }
        }

        // 3) Extract the exact icon Windows shows for our exe.
        try
        {
            string exe = Environment.ProcessPath ?? throw new InvalidOperationException();
            return System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? LoadLogo()
    {
        byte[]? ico = ReadIcoBytes();
        if (ico is null)
            return null;
        try
        {
            // Pick the largest embedded image for a crisp in-app logo.
            var entry = IcoReader.Largest(ico);
            if (entry is null)
                return null;
            var decoder = BitmapDecoder.Create(new MemoryStream(entry), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            return decoder.Frames.Count > 0 ? decoder.Frames[0] : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Minimal ICO container parser to pull the largest frame out.</summary>
    private static class IcoReader
    {
        public static byte[]? Largest(byte[] ico)
        {
            if (ico.Length < 6)
                return null;
            int count = ico[4] | (ico[5] << 8);
            if (count <= 0)
                return null;

            int bestSize = 0;
            byte[]? best = null;
            int cursor = 6;
            for (int i = 0; i < count; i++)
            {
                if (cursor + 16 > ico.Length)
                    break;
                int w = ico[cursor] == 0 ? 256 : ico[cursor];
                int h = ico[cursor + 1] == 0 ? 256 : ico[cursor + 1];
                int length = ico[cursor + 8] | (ico[cursor + 9] << 8) | (ico[cursor + 10] << 16) | (ico[cursor + 11] << 24);
                int offset = ico[cursor + 12] | (ico[cursor + 13] << 8) | (ico[cursor + 14] << 16) | (ico[cursor + 15] << 24);
                int area = w * h;
                if (area > bestSize && length > 0 && offset >= 0 && offset + length <= ico.Length)
                {
                    bestSize = area;
                    best = new byte[length];
                    Array.Copy(ico, offset, best, 0, length);
                }
                cursor += 16;
            }
            return best;
        }
    }
}