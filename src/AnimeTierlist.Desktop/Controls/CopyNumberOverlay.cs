using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AnimeTierlist.Desktop.Controls;

/// <summary>
/// Draws a big, bold red copy number centered in front of a duplicated anime cover with a crisp dark outline
/// so the numeral stays legible over light, dark, or red artwork at any card density or DPI scale.
/// Renders nothing when <see cref="CopyNumber"/> is 1 or less.
/// </summary>
public sealed class CopyNumberOverlay : FrameworkElement
{
    public static readonly DependencyProperty CopyNumberProperty = DependencyProperty.Register(
        nameof(CopyNumber),
        typeof(int),
        typeof(CopyNumberOverlay),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Typeface BoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    private static readonly Brush RedFill = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x1E, 0x25)));
    private static readonly Brush DarkOutlineBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0E)));
    private static readonly Brush SoftRimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF)));

    public CopyNumberOverlay()
    {
        IsHitTestVisible = false;
    }

    /// <summary>1-based copy number. Only copies 2 and above render an overlay.</summary>
    public int CopyNumber
    {
        get => (int)GetValue(CopyNumberProperty);
        set => SetValue(CopyNumberProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (CopyNumber <= 1 || ActualWidth <= 4 || ActualHeight <= 4)
        {
            return;
        }

        var label = CopyNumber.ToString(CultureInfo.InvariantCulture);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        // Size proportionally to the cover box, tightening for multi-digit numbers so it never clips.
        var maxFontByHeight = ActualHeight * 0.56;
        var maxFontByWidth = ActualWidth * (label.Length switch
        {
            1 => 0.85,
            2 => 0.58,
            _ => 0.42,
        });
        var fontSize = Math.Max(14, Math.Min(maxFontByHeight, maxFontByWidth));

        var formatted = new FormattedText(
            label,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            BoldTypeface,
            fontSize,
            RedFill,
            dpi);

        var origin = new Point((ActualWidth - formatted.Width) / 2.0, (ActualHeight - formatted.Height) / 2.0);
        var geometry = formatted.BuildGeometry(origin);
        if (geometry.IsEmpty())
        {
            return;
        }

        var strokeThickness = Math.Clamp(fontSize * 0.14, 2.5, 8.0);
        var outerRim = new Pen(SoftRimBrush, strokeThickness + 2.0) { LineJoin = PenLineJoin.Round };
        var darkOutline = new Pen(DarkOutlineBrush, strokeThickness) { LineJoin = PenLineJoin.Round };
        drawingContext.DrawGeometry(null, outerRim, geometry);
        drawingContext.DrawGeometry(RedFill, darkOutline, geometry);
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
