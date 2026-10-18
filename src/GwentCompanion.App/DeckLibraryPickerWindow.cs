using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

/// <summary>Shared library selection contract. Index/filter off the UI thread; only one page and one deck preview are materialized.</summary>
internal sealed class DeckLibraryPickerWindow : Window
{
    internal readonly DeckSearchBar SearchBar = new();
    internal readonly ListBox Results = new() { HorizontalContentAlignment=HorizontalAlignment.Stretch };
    internal readonly Button UseButton = new() {Content="选择这套卡组",IsEnabled=false,IsDefault=true,Padding=new Thickness(18,8,18,8)};
    internal readonly TextBlock Status = new() {Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap};
    private readonly StackPanel _preview=new();
    private readonly DispatcherTimer _debounce=new(){Interval=TimeSpan.FromMilliseconds(180)};
    private readonly Task<Entry[]> _index;
    private Entry[] _filtered=[];
    private int _page,_version;
    private bool _closed;
    internal const int PageSize=40;
    public DeckDefinition? SelectedDeck {get;private set;}
    private sealed record Entry(DeckDefinition Deck,string Label,string Search);
    public DeckLibraryPickerWindow(IEnumerable<DeckDefinition> decks,IReadOnlyList<CardDefinition> catalog,string? faction=null,string? leader=null,string action="选择这套卡组")
    {
        Title="卡组库 · 选择牌表";Width=1280;Height=850;MinWidth=850;MinHeight=550;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=FactionPalette.Brush("#0B1018");Foreground=Brushes.White;
        var snapshot=decks.Where(d=>d.CardCount>=25).ToArray();
        _index=Task.Run(()=>snapshot.Select(d=>new Entry(d,
            DeckDisplayNames.DisplayCandidate(d.Id,d.Name)+"\n"+UiTranslation.Translate(d.Faction)+" · "+UiTranslation.Translate(d.Leader)+$" · {d.CardCount} 张",
            DeckSearchCatalog.Normalize(DeckDisplayNames.DisplayCandidate(d.Id,d.Name)+" "+d.Name+" "+UiTranslation.Translate(d.Faction)+" "+UiTranslation.Translate(d.Leader)+" "+
                string.Join(" ",d.Cards.Select(c=>UiTranslation.CardName(c.Card.Id,c.Card.Name)+" "+c.Card.Name))))).ToArray());
        var root=new DockPanel{Margin=new Thickness(20)};var top=new StackPanel{Margin=new Thickness(0,0,0,12)};
        top.Children.Add(Text("卡组库",24,"#D9BC7B"));top.Children.Add(Text("搜索名称或关键卡，按阵营、领袖筛选；右侧查看完整牌表，选择后返回原来的窗口。",12));
        SearchBar.Configure("Picker","PickerDeckQuery",false);SearchBar.SetOptions(GwentOneCardCatalog.StartingLeaders(catalog).Select(c=>(c.Name,c.Faction)),snapshot.SelectMany(d=>d.Patches??[]).Select(p=>p.Label));
        if(MatchAnalysis.Factions.Contains(faction))SearchBar.Faction.SelectedItem=faction;
        if(leader is not null && SearchBar.Leader.Items.Contains(leader))SearchBar.Leader.SelectedItem=leader;
        top.Children.Add(SearchBar);top.Children.Add(Status);DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom=new DockPanel{Margin=new Thickness(0,12,0,0)};UseButton.Content=action;DockPanel.SetDock(UseButton,Dock.Right);bottom.Children.Add(UseButton);
        var cancel=new Button{Content="取消",IsCancel=true,Padding=new Thickness(14,8,14,8),Margin=new Thickness(0,0,12,0)};DockPanel.SetDock(cancel,Dock.Right);bottom.Children.Add(cancel);
        var previous=new Button{Content="上一页",Padding=new Thickness(12,7,12,7)};var next=new Button{Content="下一页",Padding=new Thickness(12,7,12,7),Margin=new Thickness(8,0,0,0)};
        previous.Click+=(_,_)=>{if(_page>0){_page--;RenderPage();}};next.Click+=(_,_)=>{if((_page+1)*PageSize<_filtered.Length){_page++;RenderPage();}};
        var pages=new StackPanel{Orientation=Orientation.Horizontal};pages.Children.Add(previous);pages.Children.Add(next);bottom.Children.Add(pages);DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var columns=new Grid();columns.ColumnDefinitions.Add(new(){Width=new GridLength(5,GridUnitType.Star)});columns.ColumnDefinitions.Add(new(){Width=new GridLength(18)});columns.ColumnDefinitions.Add(new(){Width=new GridLength(6,GridUnitType.Star)});
        var template=new DataTemplate();var label=new FrameworkElementFactory(typeof(TextBlock));label.SetBinding(TextBlock.TextProperty,new Binding(nameof(Entry.Label)));label.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);label.SetValue(TextBlock.MarginProperty,new Thickness(10,9,10,9));label.SetValue(TextBlock.ForegroundProperty,Brushes.White);template.VisualTree=label;Results.ItemTemplate=template;
        VirtualizingPanel.SetIsVirtualizing(Results,true);VirtualizingPanel.SetVirtualizationMode(Results,VirtualizationMode.Recycling);ScrollViewer.SetCanContentScroll(Results,true);
        columns.Children.Add(Results);var scroll=new ScrollViewer{Content=_preview,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled};Grid.SetColumn(scroll,2);columns.Children.Add(scroll);root.Children.Add(columns);Content=root;
        Results.SelectionChanged+=(_,_)=>RenderPreview();UseButton.Click+=(_,_)=>{if(Results.SelectedItem is Entry item){SelectedDeck=item.Deck;DialogResult=true;}};
        SearchBar.Changed+=(_,_)=>{_debounce.Stop();_debounce.Start();};SearchBar.ResetRequested+=(_,_)=>SearchBar.ClearAll();
        _debounce.Tick+=async(_,_)=>{_debounce.Stop();await RefreshAsync();};Loaded+=async(_,_)=>await RefreshAsync();
        Closed+=(_,_)=>{_closed=true;_version++;_debounce.Stop();};
    }
    internal async Task RefreshAsync()
    {
        var version=++_version;Status.Text="正在搜索卡组库…";
        var terms=DeckSearchCatalog.Terms(SearchBar.Query.Text);var faction=SearchBar.Faction.SelectedIndex>0?SearchBar.Faction.SelectedItem as string:null;
        var leader=SearchBar.Leader.SelectedIndex>0?SearchBar.Leader.SelectedItem as string:null;var filters=SearchBar.Filters;
        var contains=SearchBar.CardNames.Text;var excludes=SearchBar.ExcludedCards;var sort=SearchBar.SortOrder;
        var index=await _index;
        var found=await Task.Run(()=>{
            var values=index.Where(e=>(faction is null || e.Deck.Faction==faction) && (leader is null || e.Deck.Leader==leader) && DeckSearchCatalog.Matches(e.Search,terms) &&
                filters.Matches(e.Deck) && ChineseDeckSearchCatalog.ContainsCards(e.Deck,contains) && ChineseDeckSearchCatalog.ExcludesCards(e.Deck,excludes));
            return (sort switch{1=>values.OrderBy(e=>e.Label),2=>values.OrderByDescending(e=>e.Deck.ProvisionTotal),3=>values.OrderBy(e=>e.Deck.ProvisionTotal),_=>values.OrderByDescending(e=>e.Deck.Id.StartsWith("manual-",StringComparison.Ordinal)).ThenBy(e=>e.Deck.RecencyRank)}).ToArray();});
        if(_closed || version!=_version)return;_filtered=found;_page=0;RenderPage();
    }
    private void RenderPage(){Results.ItemsSource=_filtered.Skip(_page*PageSize).Take(PageSize).ToArray();Status.Text=$"共 {_filtered.Length:N0} 套牌表 · 第 {_page+1}/{Math.Max(1,(_filtered.Length+PageSize-1)/PageSize)} 页 · 每页最多 {PageSize} 套";UseButton.IsEnabled=false;_preview.Children.Clear();}
    private void RenderPreview()
    {
        _preview.Children.Clear();UseButton.IsEnabled=Results.SelectedItem is Entry;
        if(Results.SelectedItem is not Entry selected)return;_preview.Children.Add(Text(selected.Label,17,"#D9BC7B"));
        foreach(var entry in selected.Deck.Cards.OrderBy(c=>c.Card,DeckBuilderOrder.Comparer))
        {
            var button=new Button{Content=$"{entry.Card.Provision}  ·  {UiTranslation.CardName(entry.Card.Id,entry.Card.Name)}  ×{entry.Count}",HorizontalContentAlignment=HorizontalAlignment.Left,Padding=new Thickness(10,8,10,8),Margin=new Thickness(0,0,0,4)};
            CardInspection.SetCard(button,entry.Card);button.Click+=(_,_)=>CardInspection.Show(button,entry.Card);_preview.Children.Add(button);
        }
    }
    private static TextBlock Text(string text,double size,string color="#BBC6C0")=>new(){Text=text,FontSize=size,Foreground=FactionPalette.Brush(color),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,8)};
}
