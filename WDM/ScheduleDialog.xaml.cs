using System.Windows;
using WDM.Models;

namespace WDM;

public partial class ScheduleDialog : Window
{
    private readonly DownloadTask _task;

    public ScheduleDialog(DownloadTask task)
    {
        InitializeComponent();
        _task = task;
        FileNameText.Text = $"Schedule: {task.FileName}";

        var start = task.ScheduledStart ?? DateTime.Now.AddMinutes(5);
        for (int h = 0; h < 24; h++)
            HourBox.Items.Add(h.ToString("00"));
        for (int m = 0; m < 60; m += 5)
            MinuteBox.Items.Add(m.ToString("00"));
        HourBox.SelectedIndex = start.Hour;
        MinuteBox.SelectedIndex = start.Minute / 5;
    }

    public DateTime? SelectedTime { get; private set; }

    private void OkClick(object sender, RoutedEventArgs e)
    {
        int hour = HourBox.SelectedIndex >= 0 ? HourBox.SelectedIndex : DateTime.Now.Hour;
        int minute = (MinuteBox.SelectedIndex >= 0 ? MinuteBox.SelectedIndex * 5 : 0) % 60;

        var when = new DateTime(DateTime.Now.Year, DateTime.Now.Month, DateTime.Now.Day, hour, minute, 0);
        if (when <= DateTime.Now)
            when = when.AddDays(1);

        SelectedTime = when;
        DialogResult = true;
        Close();
    }
}
