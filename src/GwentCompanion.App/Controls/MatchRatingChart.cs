using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App.Controls;

/// <summary>Independent faction scales, actual match indices, and gaps for unavailable observations.</summary>
public sealed class MatchRatingChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(MatchRatingSeries),
        typeof(MatchRatingChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public MatchRatingSeries? Series { get => (MatchRatingSeries?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    private readonly List<(Point Position, MatchRatingPoint Value)> _hits = [];
    private static readonly Brush Muted = FactionPalette.Brush("#B5BFBC");
    public MatchRatingChart() { MouseMove += Hover; MouseLeave += (_, _) => ToolTip = null; }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); _hits.Clear();
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        if (Series is not { } series || ActualWidth < 130) return;
        var accent = FactionPalette.Brush(FactionPalette.For(series.Faction).Accent);
        void Text(string value, double x, double y, Brush? color = null, double size = 11) => dc.DrawText(
            new FormattedText(Localization.UiTranslation.Translate(value), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Microsoft YaHei UI"), size,
                color ?? Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), new(x, y));
        Text(series.Faction, 0, 0, accent, 16);
        var ratings = series.Points.Where(p => p.Rating.HasValue).ToArray();
        Text(series.Caption??(series.Metric==MatchProgressMetric.LadderPieces?Localization.UiTranslation.Translate($"{series.Points.Count(p=>p.Game>0)} games since calibration"):
            series.Metric == MatchProgressMetric.FactionMmr ? $"{series.Points.Length} games · {ratings.Length} plotted ratings"
            : Localization.UiTranslation.Translate("Recorded games") + $": {ratings.Length} / {series.Points.Length}"), 0, 26);
        if (ratings.Length == 0)
        {
            Text(series.Points.Length == 0 ? "No matches" : series.Metric == MatchProgressMetric.FactionMmr
                ? "No ordered faction-MMR readings yet." : "No ordered readings yet.", 0, 100);
            if (series.UnorderedGames > 0) Text($"{series.UnorderedGames} older games have no same-day order.", 0, 123);
            return;
        }
        var min = ratings.Min(p => p.Rating!.Value); var max = ratings.Max(p => p.Rating!.Value);
        var pieces=series.Metric==MatchProgressMetric.LadderPieces;
        var low = pieces?Math.Max(0,min/6*6):series.Metric == MatchProgressMetric.FactionMmr ? (int)Math.Floor((min - 5) / 10d) * 10
            : series.Metric == MatchProgressMetric.Rank ? Math.Max(0,min-1) : Math.Min(0,min-1);
        var high = pieces?Math.Min(42,(max/6+1)*6):series.Metric == MatchProgressMetric.FactionMmr ? (int)Math.Ceiling((max + 5) / 10d) * 10
            : series.Metric == MatchProgressMetric.Rank ? Math.Min(30,max+1) : Math.Max(0,max+1);
        if(high<=low){low=Math.Max(0,low-6);high=low+6;}
        var left=pieces?88:48;const double top=58;
        var width = ActualWidth - left - 16; var height = ActualHeight - top - 48;
        var grid = new Pen(FactionPalette.Brush("#36443F"), 1);
        foreach (var value in new[] { low, (low + high) / 2, high }.Distinct())
        {
            var y = top + height * (high - value) / (high - low);
            dc.DrawLine(grid, new(left, y), new(left + width, y));
            Text(pieces?value>=42?"Pro Rank":Localization.UiTranslation.Translate($"Rank {7-value/6} · {value%6}/5 pieces"):value.ToString(), 0, y - 7);
        }
        var firstGame=series.Points.Min(p=>p.Game);var lastGame=series.Points.Max(p=>p.Game);
        Point Position(MatchRatingPoint p) => new(left + width * (p.Game - firstGame) / Math.Max(1, lastGame-firstGame),
            top + height * (high - p.Rating!.Value) / (high - low));
        MatchRatingPoint? previous = null;
        var stroke = new Pen(accent, 2);
        foreach (var p in series.Points)
        {
            if (p.Rating is null) { previous = null; continue; }
            var point = Position(p);
            if (previous is not null && previous.Patch == p.Patch) dc.DrawLine(stroke, Position(previous), point);
            dc.DrawEllipse(accent, null, point, 3, 3); _hits.Add((point, p)); previous = p;
        }
        Text(pieces?"起点":firstGame.ToString(), left - 3, top + height + 8);
        if (series.Points.Length > 1) Text(lastGame.ToString(), left + width - 12, top + height + 8);
        Text(pieces?series.Caption is null?"Ranked games after calibration":"对局顺序 · 胜利加 1 碎片，失败减 1，零碎片保护；校准不截断曲线":series.Metric == MatchProgressMetric.FactionMmr ? "Games played with this faction"
            : series.Metric == MatchProgressMetric.Rank ? "Lower rank is better; 0 = Pro Rank" : "Win +1 · Loss −1 · Draw 0", left + 20, ActualHeight - 17);
        System.Windows.Automation.AutomationProperties.SetName(this,
            $"{series.Faction}: {series.Points.Length} games, {ratings.Length} readings, range {min} to {max}; {series.Metric}.");
    }
    private void Hover(object sender, MouseEventArgs e)
    {
        if (_hits.Count == 0) { ToolTip = null; return; }
        var position = e.GetPosition(this);
        var nearest = _hits.MinBy(p => (p.Position - position).LengthSquared);
        var unit = Series?.Metric == MatchProgressMetric.Rank ? "Ladder rank" : Series?.Metric == MatchProgressMetric.WinBalance ? "Win / loss balance" : "Faction MMR";
        ToolTip = nearest.Value is not null && (nearest.Position - position).Length < 20
            ? Localization.UiTranslation.Translate("Game") + $" {nearest.Value.Game} · {nearest.Value.Date:yyyy-MM-dd}\n" +
                (nearest.Value.Detail is { } detail?Localization.UiTranslation.Translate(detail):Localization.UiTranslation.Translate(unit) + $": {nearest.Value.Rating}") + " · " + Localization.UiTranslation.Translate("Patch") + $" {nearest.Value.Patch}" : null;
    }
}
