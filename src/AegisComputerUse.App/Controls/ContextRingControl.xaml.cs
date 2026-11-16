using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace AegisComputerUse.App.Controls;

public partial class ContextRingControl : UserControl
{
    private const int DefaultContextLimit = 1_048_576;

    public ContextRingControl()
    {
        InitializeComponent();
        UpdateUsage(0, DefaultContextLimit);
    }

    public void UpdateUsage(int totalTokens, int contextLimit = DefaultContextLimit)
    {
        int safeTotal = Math.Max(0, totalTokens);
        int safeLimit = Math.Max(1, contextLimit);
        double ratio = Math.Clamp((double)safeTotal / safeLimit, 0.0, 1.0);

        TokensLabel.Text = FormatCompactTokens(safeTotal);

        Brush arcBrush = ratio >= 0.90
            ? new SolidColorBrush(Color.FromRgb(239, 68, 68))
            : ratio >= 0.72
                ? new SolidColorBrush(Color.FromRgb(245, 158, 11))
                : new SolidColorBrush(Color.FromRgb(215, 220, 228));
        ProgressArc.Stroke = arcBrush;

        if (ratio <= 0.001)
        {
            // Show subtle minimum tick when 0 tokens so ring feels alive like reference
            ProgressArc.Data = Geometry.Empty;
            return;
        }

        double visualRatio = Math.Clamp(ratio, 0.04, 0.9999);
        const double cx = 8.0;
        const double cy = 8.0;
        const double r = 6.9;

        double angleRad = (visualRatio * 360.0 - 90.0) * Math.PI / 180.0;
        double startX = cx;
        double startY = cy - r;
        double endX = cx + r * Math.Cos(angleRad);
        double endY = cy + r * Math.Sin(angleRad);

        bool isLargeArc = visualRatio > 0.5;
        var figure = new PathFigure { StartPoint = new Point(startX, startY), IsClosed = false };
        figure.Segments.Add(new ArcSegment(
            new Point(endX, endY),
            new Size(r, r),
            0,
            isLargeArc,
            SweepDirection.Clockwise,
            true));

        var geom = new PathGeometry();
        geom.Figures.Add(figure);
        ProgressArc.Data = geom;
    }

    public static string FormatCompactTokens(int tokens)
    {
        if (tokens < 1000) return tokens.ToString(CultureInfo.InvariantCulture);
        if (tokens < 1_000_000) return $"{Math.Round(tokens / 1000.0).ToString("0", CultureInfo.InvariantCulture)} тыс.";
        return $"{(tokens / 1_000_000.0).ToString("0.#", CultureInfo.InvariantCulture)} млн";
    }
}
