using System.Globalization;
using System.Windows.Data;
using WDM.Models;
using Wpf.Ui.Controls;

namespace WDM.ViewModels;

/// <summary>Maps a task's <see cref="DownloadCategory"/> to the file-type glyph
/// shown in the task row and progress dialog. Replaces the WPF-coupled
/// DownloadTask.TypeSymbol so the domain model stays UI-free.</summary>
public sealed class CategoryToSymbolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is DownloadCategory category)
        {
            return category switch
            {
                DownloadCategory.Video => SymbolRegular.Video24,
                DownloadCategory.Music => SymbolRegular.MusicNote224,
                DownloadCategory.Document => SymbolRegular.Document24,
                DownloadCategory.Compressed => SymbolRegular.FolderZip24,
                DownloadCategory.Program => SymbolRegular.AppGeneric24,
                _ => SymbolRegular.DocumentBulletList24,
            };
        }
        return SymbolRegular.DocumentBulletList24;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
