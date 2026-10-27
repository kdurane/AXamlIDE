using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AXamlIDE.Services.Git;

namespace AXamlIDE.Views;

/// <summary>
/// Draws one row of the Git graph (a <see cref="GitGraphRow"/> from GitGraphLayout): the lines coming in at the top,
/// the lines going out at the bottom, and the commit's dot, like VS Code's Source Control Graph. The rows sit with no
/// gap between them, so the lines join up. A lane's colour is GitGraphLane1Brush..5 in turn (Styles/ThemeResources),
/// or the label colours for the current branch's line and its upstream's (GitGraphBranchBrush, GitGraphRemoteBranchBrush).
/// The dot is hollow for a merge and has a ring round it for the checked-out commit (HEAD).
/// </summary>
public class GitGraphCell : Control
{
    /// <summary>The width of one lane.</summary>
    public const double LaneWidth = 12;

    private const double DotRadius = 3.5;
    private const double LineThickness = 1.5;
    private static readonly string[] LaneBrushKeys =
        ["GitGraphLane1Brush", "GitGraphLane2Brush", "GitGraphLane3Brush", "GitGraphLane4Brush", "GitGraphLane5Brush"];

    public static readonly StyledProperty<GitGraphRow?> RowProperty =
        AvaloniaProperty.Register<GitGraphCell, GitGraphRow?>(nameof(Row));

    public static readonly StyledProperty<bool> IsHeadProperty =
        AvaloniaProperty.Register<GitGraphCell, bool>(nameof(IsHead));

    public static readonly StyledProperty<bool> IsMergeProperty =
        AvaloniaProperty.Register<GitGraphCell, bool>(nameof(IsMerge));

    static GitGraphCell()
    {
        AffectsMeasure<GitGraphCell>(RowProperty);
        AffectsRender<GitGraphCell>(RowProperty, IsHeadProperty, IsMergeProperty);
    }

    public GitGraphCell()
    {
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();   // the brushes are looked up while drawing
    }

    public GitGraphRow? Row
    {
        get => GetValue(RowProperty);
        set => SetValue(RowProperty, value);
    }

    public bool IsHead
    {
        get => GetValue(IsHeadProperty);
        set => SetValue(IsHeadProperty, value);
    }

    public bool IsMerge
    {
        get => GetValue(IsMergeProperty);
        set => SetValue(IsMergeProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new((Row?.Width ?? 0) * LaneWidth + 4, 22);

    public override void Render(DrawingContext context)
    {
        if (Row is not { } row)
            return;

        var height = Bounds.Height;
        var middle = height / 2;
        foreach (var line in row.Top)
            DrawLine(context, line, 0, middle);
        foreach (var line in row.Bottom)
            DrawLine(context, line, middle, height);

        if (row.Lane < 0)
            return;
        var brush = LaneBrush(row.Color);
        var centre = new Point(X(row.Lane), middle);
        if (IsHead)
            context.DrawEllipse(null, new Pen(brush, LineThickness), centre, DotRadius + 2.5, DotRadius + 2.5);
        if (IsMerge)
            context.DrawEllipse(Background(), new Pen(brush, LineThickness * 1.5), centre, DotRadius, DotRadius);
        else
            context.DrawEllipse(brush, null, centre, DotRadius, DotRadius);
    }

    /// <summary>A line through half the row: straight down within a lane, or an S-bend from one lane to another.</summary>
    private void DrawLine(DrawingContext context, GitGraphLine line, double top, double bottom)
    {
        var pen = new Pen(LaneBrush(line.Color), LineThickness);
        double fromX = X(line.FromLane), toX = X(line.ToLane);
        if (line.FromLane == line.ToLane)
        {
            context.DrawLine(pen, new Point(fromX, top), new Point(toX, bottom));
            return;
        }

        var bend = new StreamGeometry();
        using (var geometry = bend.Open())
        {
            var halfway = (top + bottom) / 2;
            geometry.BeginFigure(new Point(fromX, top), false);
            geometry.CubicBezierTo(new Point(fromX, halfway), new Point(toX, halfway), new Point(toX, bottom));
            geometry.EndFigure(false);
        }
        context.DrawGeometry(null, pen, bend);
    }

    private static double X(int lane) => lane * LaneWidth + LaneWidth / 2 + 2;

    private IBrush LaneBrush(int color) => FindBrush(color switch
    {
        GitGraphLayout.CurrentBranchColor => "GitGraphBranchBrush",
        GitGraphLayout.UpstreamColor => "GitGraphRemoteBranchBrush",
        _ => LaneBrushKeys[color % LaneBrushKeys.Length]
    }) ?? Brushes.Gray;

    private IBrush Background() => FindBrush("ThemeBackgroundBrush") ?? Brushes.Transparent;

    private IBrush? FindBrush(string key)
        => this.TryFindResource(key, ActualThemeVariant, out var brush) ? brush as IBrush : null;
}
