using System.Windows;

namespace WDM;

public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();
    }

    private void CloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
