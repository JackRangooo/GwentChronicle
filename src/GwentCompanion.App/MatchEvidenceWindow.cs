using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

internal sealed class MatchEvidenceWindow : Window
{
    internal readonly TextBox Search = new() { MinHeight=32,ToolTip="输入中文或英文卡名，搜索全部可加入起始牌组的卡牌" };
    internal readonly CheckBox OnlyRecorded = new() {Content="只看本场已记录卡牌（搜索时会查找全部可用卡牌）",IsChecked=true};
    internal readonly StackPanel Rows = new();
    internal readonly TextBlock Status = new() {TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGreen};
    private readonly OpponentEvidenceEditor _editor;
    private readonly (CardDefinition Card,string SearchName)[] _cards;
    private readonly IReadOnlyList<CardDefinition> _catalog;
    private readonly IEnumerable<DeckDefinition> _decks;
    private readonly string? _faction;
    private Dictionary<string,int>? _draft;
    private IReadOnlyDictionary<string,int>? _undo;
    internal readonly Button ChooseDeck = new(){Content="从卡组库选择底稿…",Padding=new Thickness(12,7,12,7)};
    internal readonly Button SaveDraft = new(){Content="确认底稿并批量补录",Visibility=Visibility.Collapsed,Padding=new Thickness(12,7,12,7),Margin=new Thickness(8,0,0,0)};
    private readonly Button _cancelDraft=new(){Content="取消底稿",Visibility=Visibility.Collapsed,Margin=new Thickness(8,0,0,0)};
    private readonly Button _undoButton=new(){Content="撤销上次批量补录",IsEnabled=false,Margin=new Thickness(8,0,0,0)};
    private readonly ScrollViewer _scroll = new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};

    internal MatchEvidenceWindow(OpponentEvidenceEditor editor,IEnumerable<CardDefinition> catalog,string? faction,IEnumerable<DeckDefinition>? decks=null)
    {
        _editor=editor;
        var knownFaction=GwentCompanion.Core.Data.MatchAnalysis.Factions.Contains(faction)?faction:null;
        _catalog=catalog.ToArray();_decks=decks??[];_faction=knownFaction;
        _cards=_catalog.Where(c=>StartingDeckRules.IsLegalStartingCard(c,knownFaction))
            .DistinctBy(c=>c.Id).OrderBy(c=>c,DeckBuilderOrder.Comparer)
            .Select(c=>(c,DeckSearchCatalog.Normalize(UiTranslation.CardName(c.Id,c.Name)+" "+c.Name+" "+c.Id))).ToArray();
        Title=editor.Side==PlayerSide.User?"补录／校对你的已识别卡牌":"补录／校对对手的已识别卡牌";
        Width=860;Height=Math.Min(850,SystemParameters.WorkArea.Height-50);MinWidth=620;MinHeight=450;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;Background=FactionPalette.Brush("#0B1018");Foreground=Brushes.White;
        var root=new DockPanel{Margin=new Thickness(18)};var heading=new StackPanel{Margin=new Thickness(0,0,0,12)};
        heading.Children.Add(Text(Title,20,"#D9BC7B"));
        heading.Children.Add(Text("＋增加一张，−撤回一张，设为 0 可排除误识别。单张补录即时保存；整套底稿确认后保存。",12));
        var actions=new WrapPanel{Margin=new Thickness(0,5,0,8)};actions.Children.Add(ChooseDeck);actions.Children.Add(SaveDraft);actions.Children.Add(_cancelDraft);actions.Children.Add(_undoButton);heading.Children.Add(actions);
        heading.Children.Add(Text("也可选择整套牌表作底稿，再增减卡牌。载入底稿不会保存；确认后记为人工校对，原始自动识别不改写。",12));
        heading.Children.Add(Search);OnlyRecorded.Margin=new Thickness(0,8,0,5);heading.Children.Add(OnlyRecorded);heading.Children.Add(Status);
        DockPanel.SetDock(heading,Dock.Top);root.Children.Add(heading);_scroll.Content=Rows;root.Children.Add(_scroll);Content=root;
        Search.TextChanged+=(_,_)=>Refresh(true);OnlyRecorded.Checked+=(_,_)=>Refresh(true);OnlyRecorded.Unchecked+=(_,_)=>Refresh(true);
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){Close();e.Handled=true;}};
        ChooseDeck.Click+=(_,_)=>{var picker=new DeckLibraryPickerWindow(_decks,_catalog,_faction,action:"载入这套牌表为底稿"){Owner=this};if(picker.ShowDialog()==true && picker.SelectedDeck is {} deck)LoadDraft(deck);};
        SaveDraft.Click+=(_,_)=>{try{CommitDraft();}catch(Exception error){Status.Text="未保存："+error.Message;}};
        _cancelDraft.Click+=(_,_)=>{_draft=null;SetDraftMode();Refresh();};
        _undoButton.Click+=(_,_)=>{try{if(_undo is {} undo){_editor.RestoreCorrections(undo);_undo=null;_undoButton.IsEnabled=false;Refresh();}}catch(Exception error){Status.Text="未撤销："+error.Message;}};
        Refresh();
    }
    internal void LoadDraft(DeckDefinition deck)
    {
        if(_faction is not null && deck.Faction!=_faction){Status.Text="请选择与本场阵营相同的底稿。";return;}
        _draft=deck.Cards.Where(c=>_cards.Any(d=>d.Card.Id==c.Card.Id)).ToDictionary(c=>c.Card.Id,c=>c.Count);
        foreach(var seen in _editor.Evidence())_draft[seen.Card.Id]=Math.Max(_draft.GetValueOrDefault(seen.Card.Id),seen.ObservedCopies);
        Search.Clear();OnlyRecorded.IsChecked=true;SetDraftMode();Refresh(true);
    }
    internal void CommitDraft()
    {
        if(_draft is null)return;_undo=_editor.Corrections.ToDictionary(p=>p.Key,p=>p.Value);
        _editor.ApplyDraft(_cards.Where(c=>_draft.GetValueOrDefault(c.Card.Id)>0).Select(c=>new DeckCard(c.Card,_draft[c.Card.Id])).ToArray());
        _draft=null;_undoButton.IsEnabled=true;SetDraftMode();Refresh();
    }
    private void SetDraftMode(){SaveDraft.Visibility=_cancelDraft.Visibility=_draft is null?Visibility.Collapsed:Visibility.Visible;}
    internal void Refresh(bool resetScroll=false)
    {
        var offset=resetScroll?0:_scroll.VerticalOffset;Rows.Children.Clear();
        var seen=_draft??_editor.Evidence().GroupBy(c=>c.Card.Id).ToDictionary(g=>g.Key,g=>g.Max(c=>c.ObservedCopies));
        var corrections=_editor.Corrections;
        var query=DeckSearchCatalog.Normalize(Search.Text.Trim());
        var results=_cards.Where(c=>query.Length>0 ? c.SearchName.Contains(query,StringComparison.OrdinalIgnoreCase)
            : OnlyRecorded.IsChecked!=true || seen.GetValueOrDefault(c.Card.Id)>0 || corrections.ContainsKey(c.Card.Id)).ToArray();
        foreach(var item in results.Take(160))
        {
            var card=item.Card;var count=seen.GetValueOrDefault(card.Id);var limit=card.IsGold?1:2;
            var row=new DockPanel{LastChildFill=true};
            var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(actions,Dock.Right);row.Children.Add(actions);
            Button Action(string label,int value,bool enabled)
            {
                var b=new Button{Content=label,IsEnabled=enabled && _editor.CanEdit,Padding=new Thickness(9,5,9,5),Margin=new Thickness(4,0,0,0),Tag=(card,value)};
                b.Click+=(_,_)=>{try{if(_draft is not null)_draft[card.Id]=value;else _editor.SetCopies(card,value);Refresh();}catch(Exception error){Status.Text="未保存："+error.Message;}};actions.Children.Add(b);return b;
            }
            Action("−",Math.Max(0,count-1),count>0);Action("＋",Math.Min(limit,count+1),count<limit);Action("设为 0",0,count>0);
            var info=new Button{Content="ⓘ",Padding=new Thickness(9,5,9,5),Margin=new Thickness(7,0,0,0)};
            info.Click+=(_,_)=>CardInspection.Show(info,card);actions.Children.Add(info);
            var label=new StackPanel();label.Children.Add(Text($"{card.Provision}  ·  {UiTranslation.CardName(card.Id,card.Name)}  ×{count}/{limit}",14,"#E7D39F"));
            label.Children.Add(Text(_draft is not null?"底稿 · 尚未保存":corrections.ContainsKey(card.Id)?count>0?"记忆补录 · 用户确认":"已排除／撤回":count>0?"本场自动识别":"尚未记录",11));
            var inspect=new Button{Content=label,HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(0),BorderThickness=new Thickness(0),Background=Brushes.Transparent};
            CardInspection.SetCard(inspect,card);inspect.Click+=(_,_)=>CardInspection.Show(inspect,card);row.Children.Add(inspect);
            Rows.Children.Add(new Border{Child=row,Padding=new Thickness(10,7,10,7),Margin=new Thickness(0,0,0,5),Background=FactionPalette.Brush("#121A26"),BorderBrush=FactionPalette.Brush("#303D4D"),BorderThickness=new Thickness(1)});
        }
        Status.Text=(_draft is null?"本场已记录":"底稿待确认")+$" {seen.Values.Sum()} 张 · 找到 {results.Length} 种卡牌"+(results.Length>160?"，请继续输入卡名缩小范围":"");
        _scroll.ScrollToVerticalOffset(offset);
    }
    private static TextBlock Text(string value,double size,string color="#BBC6C0")=>new(){Text=value,FontSize=size,TextWrapping=TextWrapping.Wrap,Foreground=FactionPalette.Brush(color),Margin=new Thickness(0,2,0,4)};
}
