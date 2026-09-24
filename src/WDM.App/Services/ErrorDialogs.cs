using System;
using System.Windows;

namespace WDM.Services;

/// <summary>WPF message-box half of <see cref="UserFriendlyError"/>: the text
/// mapping lives UI-free in WDM.Engine, the dialogs stay here in the App.</summary>
public static class ErrorDialogs
{
    /// <summary>Logs the technical details and shows a small friendly error box.</summary>
    public static MessageBoxResult ShowError(Window? owner, string title, Exception ex)
    {
        try { App.LogException(ex); } catch { }
        return ShowBox(owner, title, UserFriendlyError.For(ex), MessageBoxImage.Error);
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
