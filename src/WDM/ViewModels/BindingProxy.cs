using System.Windows;

namespace WDM.ViewModels;

/// <summary>Freezable DataContext proxy: <see cref="ColumnDefinition"/> (and other
/// non-visual objects, e.g. inside <c>DataTemplate</c>s) cannot use
/// RelativeSource/ElementName bindings, but they can bind through a Freezable
/// resource which inherits the surrounding DataContext.
/// Usage: <c>&lt;vm:BindingProxy x:Key="WideProxy" Data="{Binding IsWideLayout}" /&gt;</c>
/// then <c>Width="{Binding Data, Source={StaticResource WideProxy}, ...}"</c>.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty =
        DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));

    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}
