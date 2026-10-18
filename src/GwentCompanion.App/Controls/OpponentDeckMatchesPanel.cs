using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App.Controls;

public sealed class OpponentDeckMatchesPanel : StackPanel
{
    public static readonly DependencyProperty ReportProperty = DependencyProperty.Register(nameof(Report), typeof(OpponentDeckMatchReport),
        typeof(OpponentDeckMatchesPanel), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DataRootProperty = DependencyProperty.Register(nameof(DataRoot), typeof(string),
        typeof(OpponentDeckMatchesPanel), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty FilterLabelProperty = DependencyProperty.Register(nameof(FilterLabel), typeof(string),
        typeof(OpponentDeckMatchesPanel), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty EditorProperty = DependencyProperty.Register(nameof(Editor), typeof(OpponentEvidenceEditor),
        typeof(OpponentDeckMatchesPanel), new PropertyMetadata(null));
    public OpponentEvidenceEditor? Editor { get => (OpponentEvidenceEditor?)GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    public OpponentDeckMatchReport? Report { get => (OpponentDeckMatchReport?)GetValue(ReportProperty); set => SetValue(ReportProperty, value); }
    public string? DataRoot { get => (string?)GetValue(DataRootProperty); set => SetValue(DataRootProperty, value); }
    public string? FilterLabel { get => (string?)GetValue(FilterLabelProperty); set => SetValue(FilterLabelProperty, value); }
    public OpponentDeckMatchesPanel() => Visibility = Visibility.Collapsed;
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs _) => ((OpponentDeckMatchesPanel)target).Refresh();
    public void Refresh()
    {
        Children.Clear();
        Visibility = Report?.HasEvidence == true ? Visibility.Visible : Visibility.Collapsed;
        if (Report is not { } report) return;
        if (!report.HasMatches)
        {
            AddText("暂不推荐相似卡组",14,"#E5C77E");
            AddText(report.TotalObservedCopies<OpponentDeckMatcher.MinimumObservedCopies
                ? $"有效已识别／补录卡牌 {report.TotalObservedCopies}/{OpponentDeckMatcher.MinimumObservedCopies} 张；数量不足。可直接使用“补录／校对卡牌”。"
                : "没有候选同时满足阵营、初始领袖和至少 80% 的已识别卡牌张数覆盖。实录与补录不受影响。",12,"#AFBCB5");
            return;
        }
        AddText("最接近的两套候选（非实录）", 14, "#E5C77E");
        foreach (var hit in report.Matches.Take(2))
        {
            var content = new DockPanel();
            var open = new Button { Content = "View list", Padding = new Thickness(7,3,7,3), Margin = new Thickness(8,0,0,0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(open, Dock.Right); content.Children.Add(open);
            open.Click += (_,_) => new DeckMatchPreviewWindow(hit, DataRoot ?? AppContext.BaseDirectory, report.Evidence, Editor)
                { Owner = Window.GetWindow(this) }.Show();
            var label = new StackPanel();
            label.Children.Add(new TextBlock { Text = DeckDisplayNames.DisplayCandidate(hit.Deck.Id, hit.Deck.Name), FontSize = 12,
                Foreground = FactionPalette.Brush("#E7D39F"), TextTrimming = TextTrimming.CharacterEllipsis,
                ToolTip = DeckDisplayNames.DisplayCandidate(hit.Deck.Id, hit.Deck.Name) });
            label.Children.Add(new TextBlock { Text = $"张数覆盖 {hit.ObservedCoveragePercent:0.#}% · {hit.MatchedCopies}/{hit.TotalObservedCopies} 张吻合",
                FontSize = 11, Foreground = FactionPalette.Brush("#AFBCB5"), TextWrapping = TextWrapping.Wrap,ToolTip="按卡牌副本张数计算；推荐要求至少 12 张且覆盖至少 80%。费用加权只用于候选排序，不代表确认整套牌表。" });
            content.Children.Add(label);
            Children.Add(new Border { Child = content, Background = FactionPalette.Brush("#182421"), BorderBrush = FactionPalette.Brush("#3E5148"),
                BorderThickness = new Thickness(1), Padding = new Thickness(8,5,8,5), Margin = new Thickness(0,4,0,0) });
        }
        AddText($"{report.TotalObservedCopies} 张已记录 · {report.CompatibleDistinctDeckCount} 张完整候选牌表",11,"#AFBCB5");
        var explanation=new StackPanel();
        void Explain(string text)=>explanation.Children.Add(new TextBlock {Text=UiTranslation.Translate(text),FontSize=11,Foreground=FactionPalette.Brush("#AFBCB5"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,0)});
        if(!string.IsNullOrWhiteSpace(FilterLabel)) Explain(FilterLabel);
        if(report.TopTieCount>2) Explain($"{report.TopTieCount} lists share the best score; showing two.");
        Explain("张数覆盖 = 候选中包含的已记录副本数 ÷ 全部有效已记录副本数。至少 12 张、覆盖至少 80% 才推荐；费用加权用于排序，未见卡仍未确认。");
        Children.Add(new Expander {Header="匹配依据与限制",Content=explanation,Margin=new Thickness(0,5,0,0),FontSize=11});
    }
    private void AddText(string text, double size, string color) => Children.Add(new TextBlock { Text = UiTranslation.Translate(text), FontSize = size,
        Foreground = FactionPalette.Brush(color), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,2,0,0) });
}
