using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using WDM.Tests.TestInfrastructure;
using WDM.ViewModels;

namespace WDM.Tests;

/// <summary>Guards the shipped bug where the thin progress-bar template never
/// sized its fill (42% text, empty bar): the indicator width must be driven
/// by Value, and the star converter must map fractions correctly.</summary>
public sealed class ProgressBarTests
{
    [Trait("Category", Cats.Unit)][Fact]
    public void StarConverter_MapsFraction()
    {
        var c = new ProgressStarConverter();
        var fill = (GridLength)c.Convert(new object[] { 42.0, 0.0, 100.0 }, typeof(GridLength), "fill",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(GridUnitType.Star, fill.GridUnitType);
        Assert.InRange(fill.Value, 0.419, 0.421);

        var rest = (GridLength)c.Convert(new object[] { 42.0, 0.0, 100.0 }, typeof(GridLength), "rest",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(rest.Value, 0.579, 0.581);

        var zero = (GridLength)c.Convert(new object[] { 0.0, 0.0, 100.0 }, typeof(GridLength), "fill",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, zero.Value);

        var full = (GridLength)c.Convert(new object[] { 100.0, 0.0, 100.0 }, typeof(GridLength), "fill",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1, full.Value);

        var clamped = (GridLength)c.Convert(new object[] { 250.0, 0.0, 100.0 }, typeof(GridLength), "fill",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(1, clamped.Value);

        // Degenerate span never divides by zero.
        var flat = (GridLength)c.Convert(new object[] { 5.0, 5.0, 5.0 }, typeof(GridLength), "fill",
            System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(0, flat.Value);
    }

    [Trait("Category", Cats.Unit)][Fact]
    public void ThinProgressBar_IndicatorWidth_IsValueBound()
    {
        Exception? error = null;
        bool bound = false;
        bool isStarConverter = false;
        var thread = new Thread(() =>
        {
            try
            {
                // The real style block, verbatim from Theme.xaml.
                string theme = Path.GetFullPath(Path.Combine(
                    AppContext.BaseDirectory, "..", "..", "..", "..", "WDM.App", "Themes", "Theme.xaml"));
                string text = File.ReadAllText(theme);
                const string marker = "<Style x:Key=\"ProgressBar.Thin\"";
                int start = text.IndexOf(marker, StringComparison.Ordinal);
                if (start < 0)
                    throw new InvalidOperationException("ProgressBar.Thin style not found in Theme.xaml");
                int end = text.IndexOf("</Style>", start, StringComparison.Ordinal) + "</Style>".Length;
                string wrapped =
                    "<ResourceDictionary xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                    "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                    "xmlns:vm=\"clr-namespace:WDM.ViewModels;assembly=WDM\">" +
                    text.Substring(start, end - start) +
                    "</ResourceDictionary>";
                ResourceDictionary dict;
                using (var reader = System.Xml.XmlReader.Create(new StringReader(wrapped)))
                {
                    dict = (ResourceDictionary)System.Windows.Markup.XamlReader.Load(reader);
                }
                var bar = new ProgressBar
                {
                    Style = (Style)dict["ProgressBar.Thin"],
                    Width = 200,
                    Minimum = 0,
                    Maximum = 100,
                    Value = 42,
                };
                var win = new Window { Content = bar };
                bar.ApplyTemplate();
                var indicator = (FrameworkElement)bar.Template.FindName("PART_Indicator", bar)!;
                // The fill is sized by the column it sits in: both columns
                // must carry the 3-way star MultiBinding.
                var grid = (Grid)indicator.Parent;
                var fillExpr = BindingOperations.GetMultiBindingExpression(
                    grid.ColumnDefinitions[0], ColumnDefinition.WidthProperty);
                var restExpr = BindingOperations.GetMultiBindingExpression(
                    grid.ColumnDefinitions[1], ColumnDefinition.WidthProperty);
                bound = fillExpr is not null && restExpr is not null
                    && fillExpr.ParentMultiBinding.Bindings.Count == 3
                    && restExpr.ParentMultiBinding.Bindings.Count == 3;
                isStarConverter = fillExpr?.ParentMultiBinding.Converter is ProgressStarConverter
                    && restExpr?.ParentMultiBinding.Converter is ProgressStarConverter;
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(15)))
            throw new TimeoutException("STA thread did not finish");
        Assert.Null(error);
        Assert.True(bound);
        Assert.True(isStarConverter);
    }
}
