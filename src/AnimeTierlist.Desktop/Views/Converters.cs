using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AnimeTierlist.Core;

namespace AnimeTierlist.Desktop.Views;

/// <summary>Attached placeholder text for styled <see cref="System.Windows.Controls.TextBox"/> controls.</summary>
public static class TextBoxAssist
{
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(TextBoxAssist), new PropertyMetadata(string.Empty));

    /// <summary>Gets the placeholder string shown when the TextBox is empty.</summary>
    public static string GetPlaceholder(DependencyObject obj) => (string)obj.GetValue(PlaceholderProperty);

    /// <summary>Sets the placeholder string shown when the TextBox is empty.</summary>
    public static void SetPlaceholder(DependencyObject obj, string value) => obj.SetValue(PlaceholderProperty, value);
}

/// <summary>True collapses, false shows.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Shows non-empty text, non-zero counts, and non-null values; collapses null, empty, or zero.</summary>
public sealed class NotEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is null || (value is string text && string.IsNullOrWhiteSpace(text)) || (value is int count && count == 0) ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when the bound value equals the converter parameter; supports two-way radio binding.</summary>
public sealed class EqualsToBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true && parameter is not null)
        {
            if (targetType.IsEnum)
            {
                return Enum.Parse(targetType, parameter.ToString()!, ignoreCase: true);
            }

            return parameter;
        }

        return Binding.DoNothing;
    }
}

/// <summary>Visible when the bound value equals the parameter, collapsed otherwise.</summary>
public sealed class EqualsToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is not null && parameter is not null && string.Equals(value.ToString(), parameter.ToString(), StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Converts a <see cref="TierColor"/> into a frozen <see cref="SolidColorBrush"/>.</summary>
public sealed class TierColorToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is TierColor c)
        {
            var brush = new SolidColorBrush(Color.FromRgb(c.Red, c.Green, c.Blue));
            brush.Freeze();
            return brush;
        }

        return Brushes.Transparent;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Freezable proxy allowing detached visual trees (such as ContextMenus) to bind to a parent DataContext.</summary>
public sealed class BindingProxy : Freezable
{
    public static readonly DependencyProperty DataProperty = DependencyProperty.Register(
        nameof(Data), typeof(object), typeof(BindingProxy), new UIPropertyMetadata(null));

    /// <summary>Bound data context forwarded to detached popups and context menus.</summary>
    public object? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    protected override Freezable CreateInstanceCore() => new BindingProxy();
}

/// <summary>Converts a <see cref="TierColor"/> into a high-contrast foreground brush using <see cref="TierColor.PreferredInk"/>.</summary>
public sealed class TierInkToBrushConverter : IValueConverter
{
    private static readonly SolidColorBrush DarkInk = Freeze(Color.FromRgb(0x00, 0x00, 0x00));
    private static readonly SolidColorBrush LightInk = Freeze(Color.FromRgb(0xFF, 0xFF, 0xFF));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is TierColor c && c.PreferredInk() == TierInk.White ? LightInk : DarkInk;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();

    private static SolidColorBrush Freeze(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}

/// <summary>Converts a TextBox's Padding into the exact Margin for its overlaid placeholder TextBlock.</summary>
public sealed class PaddingToPlaceholderMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Thickness padding)
        {
            return new Thickness(padding.Left + 2, padding.Top, padding.Right + 2, padding.Bottom);
        }

        return new Thickness(10, 4, 10, 4);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Dims artwork on non-matching items when a visual search highlight is active.</summary>
public sealed class DimmedToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? 0.35 : 1.0;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>True when two bound objects are equal (used to highlight the active color swatch).</summary>
public sealed class SameObjectConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture) =>
        values.Length >= 2 && Equals(values[0], values[1]);

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
