using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

internal sealed class DeckMatchPreviewWindow : Window
{
    public DeckMatchPreviewWindow(OpponentDeckMatch hit, string dataRoot, IReadOnlyList<OpponentDeckEvidence>? evidence = null, OpponentEvidenceEditor? editor = null)
    {
        Title = DeckDisplayNames.DisplayCandidate(hit.Deck.Id, hit.Deck.Name);
        Width = 850; Height = Math.Min(850, SystemParameters.WorkArea.Height - 60); MinWidth = 600; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = FactionPalette.Brush("#0B1018"); Foreground = Brushes.White;
        PreviewKeyDown += (_,e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        void Refresh()
        {
        var offset = (Content as DockPanel)?.Children.OfType<ScrollViewer>().FirstOrDefault()?.VerticalOffset ?? 0;
        var currentEvidence = editor is null ? evidence ?? [] : OpponentDeckMatcher.Match([hit.Deck], editor.Evidence(), hit.Deck.Faction, hit.Deck.Leader).Evidence;
        var currentHit = editor is null ? hit : OpponentDeckMatcher.Match([hit.Deck], editor.Evidence(), hit.Deck.Faction, hit.Deck.Leader).Matches.FirstOrDefault();
        var root = new DockPanel { Margin = new Thickness(18) };
        var heading = new StackPanel { Margin = new Thickness(0,0,0,12) };
        heading.Children.Add(Text(Title, 21, "#D9BC7B"));
        heading.Children.Add(Text(UiTranslation.Translate(hit.Deck.Faction) + " · " + UiTranslation.Translate(hit.Deck.Leader) +
            " · " + UiTranslation.Translate($"{hit.Deck.CardCount} cards"), 12));
        var coveredCount=currentEvidence.Sum(c=>Math.Min(c.Copies,hit.Deck.CountOf(c.Card.Id)));
        var totalCount=currentEvidence.Sum(c=>c.Copies);
        heading.Children.Add(Text($"张数覆盖 {(totalCount==0?0:100d*coveredCount/totalCount):0.#}% · {coveredCount}/{totalCount} 张吻合"+
            (editor is not null && currentHit is null ? " · 当前证据未达到推荐条件，本页仅供对照" : ""),13,"#91D0AD"));
        heading.Children.Add(Text(UiTranslation.Translate("Candidate list only. Covered cards are marked; the remaining cards were not confirmed in this match."), 11));
        if (editor is not null) heading.Children.Add(Text("点击每张副本的按钮切换“已看到 / 尚未看到”；即时保存到本条记录。手动补录标为“记忆”，再次点击可撤回。", 11, "#D9BC7B"));
        var missing = currentHit?.MissingObservedCards ?? (editor is null ? hit.MissingObservedCards : currentEvidence
            .Where(c => c.Copies > hit.Deck.CountOf(c.Card.Id)).Select(c => new OpponentDeckMissingCard(c.Card,c.Copies-hit.Deck.CountOf(c.Card.Id))).ToArray());
        if (missing.Count > 0)
            heading.Children.Add(Text(UiTranslation.Translate("Observed cards absent from this list") + ": " + string.Join("、", missing.Select(c =>
                UiTranslation.CardName(c.Card.Id,c.Card.Name) + " ×" + c.Copies)), 11, "#E3AE8D"));
        DockPanel.SetDock(heading,Dock.Top); root.Children.Add(heading);
        var list = new StackPanel();
        var seen = currentEvidence.ToDictionary(c => c.Card.Id,c => c.Copies,StringComparer.Ordinal);
        foreach (var entry in hit.Deck.Cards.OrderBy(c => c.Card,DeckBuilderOrder.Comparer))
        {
            var row = new DockPanel();
            var info = new Button { Content="ⓘ",Padding=new Thickness(10,4,10,4),Margin=new Thickness(8,0,0,0),ToolTip="View full card and ability" };
            info.Click += (_,_) => new CardPreviewWindow(entry.Card, Path.Combine(dataRoot,"cache","portraits",entry.Card.Id+".jpg")) { Owner=this }.Show();
            DockPanel.SetDock(info,Dock.Right); row.Children.Add(info);
            var covered = Math.Min(seen.GetValueOrDefault(entry.Card.Id), entry.Count);
            var states = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            for (var copy = 1; copy <= entry.Count; copy++)
            {
                var copyNumber = copy;
                var isSeen = copy <= covered;
                var remembered = editor?.Corrections.ContainsKey(entry.Card.Id) == true;
                var state = new Button { Content = (entry.Count > 1 ? $"{copy} · " : "") + (isSeen ? "已看到" : "尚未看到") + (remembered ? " · 记忆" : ""),
                    IsEnabled = editor?.CanEdit == true, FontSize = 11, Padding = new Thickness(7,4,7,4), Margin = new Thickness(3,0,0,0),
                    Foreground = FactionPalette.Brush(isSeen ? "#91D0AD" : "#BBC5C0"),
                    ToolTip = editor is null ? "此预览没有关联可编辑的对手记录" : "点击切换并保存；两张同名铜卡以已看到的数量表示" };
                state.Click += (_,_) => {
                    try { var count = editor!.Count(entry.Card.Id); editor.SetCopies(entry.Card, copyNumber <= count ? count - 1 : count + 1); Refresh(); }
                    catch (Exception error) { MessageBox.Show(this,error.Message,"补录未保存",MessageBoxButton.OK,MessageBoxImage.Error); }
                };
                states.Children.Add(state);
            }
            DockPanel.SetDock(states, Dock.Right); row.Children.Add(states);
            // Absence from the observed set is unknown; it is never labelled as a detected card.
            var label = Text($"{entry.Card.Provision}  ·  {UiTranslation.CardName(entry.Card.Id,entry.Card.Name)}  ×{entry.Count}",14,"#E7D39F");
            label.VerticalAlignment=VerticalAlignment.Center;
            var inspect=new Button {Content=label,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(0),Background=Brushes.Transparent,BorderThickness=new Thickness(0)};
            CardInspection.SetCard(inspect,entry.Card);
            inspect.Click+=(_,_)=>CardInspection.Show(inspect,entry.Card);row.Children.Add(inspect);
            list.Children.Add(new Border { Child=row,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,0,5),Background=FactionPalette.Brush("#121A26"),BorderBrush=FactionPalette.Brush("#303D4D"),BorderThickness=new Thickness(1) });
        }
        var scroll = new ScrollViewer { Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled };
        scroll.Loaded += (_,_) => scroll.ScrollToVerticalOffset(offset); root.Children.Add(scroll);
        Content=root;
        }
        Refresh();
        Activated += (_,_) => Refresh();
    }
    private static TextBlock Text(string text,double size,string color="#B5BFBC") => new() { Text=text,FontSize=size,TextWrapping=TextWrapping.Wrap,
        Foreground=FactionPalette.Brush(color),Margin=new Thickness(0,3,0,0) };
}
