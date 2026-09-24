using System.Windows;
using System.Windows.Input;
using System.Windows.Shell;

namespace WDM.Controls;

public class WdmWindow : Window
{
    public WdmWindow()
    {
        WindowStyle = WindowStyle.None;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 38,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        CommandBindings.Add(new CommandBinding(SystemCommands.CloseWindowCommand, CloseWindow));
        CommandBindings.Add(new CommandBinding(SystemCommands.MaximizeWindowCommand, MaximizeWindow, CanResizeWindow));
        CommandBindings.Add(new CommandBinding(SystemCommands.MinimizeWindowCommand, MinimizeWindow, CanMinimizeWindow));
        CommandBindings.Add(new CommandBinding(SystemCommands.RestoreWindowCommand, RestoreWindow, CanResizeWindow));
    }

    private void CanResizeWindow(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = ResizeMode == ResizeMode.CanResize || ResizeMode == ResizeMode.CanResizeWithGrip;
    }

    private void CanMinimizeWindow(object sender, CanExecuteRoutedEventArgs e)
    {
        e.CanExecute = ResizeMode != ResizeMode.NoResize;
    }

    private void CloseWindow(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.CloseWindow(this);
    }

    private void MaximizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MaximizeWindow(this);
    }

    private void MinimizeWindow(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.MinimizeWindow(this);
    }

    private void RestoreWindow(object sender, ExecutedRoutedEventArgs e)
    {
        SystemCommands.RestoreWindow(this);
    }
}