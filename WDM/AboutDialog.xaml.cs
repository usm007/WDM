using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;

namespace WDM;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
        var version = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {(version?.Major ?? 1)}.{(version?.Minor ?? 0)}";
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Hyperlink link && link.NavigateUri is Uri uri)
        {
            try
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch
            {
                // Ignore navigation failures.
            }
        }
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
