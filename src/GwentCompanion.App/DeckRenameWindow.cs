using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

internal sealed class DeckRenameWindow : Window
{
    private readonly TextBox _name = new() { MaxLength = 160 };
    private readonly ComboBox _scope = new();
    internal readonly ListBox Suggestions = new() { Height = 230, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    public string DeckName => _name.Text;
    public bool RenameGroup => _scope.SelectedIndex == 0 && _scope.Items.Count > 1;
    public DeckRenameWindow(string name,string original,string? faction,string? leader,IEnumerable<DeckCard> cards,bool grouped)
    {
        Title="卡组重命名"; Width=620; SizeToContent=SizeToContent.Height; MaxHeight=SystemParameters.WorkArea.Height-40;
        WindowStartupLocation=WindowStartupLocation.CenterOwner; Background=FactionPalette.Brush("#151D22"); Foreground=Brushes.White;
        var panel = new StackPanel { Margin=new Thickness(18) };
        panel.Children.Add(new TextBlock { Text="原名称："+original,TextWrapping=TextWrapping.Wrap,Foreground=FactionPalette.Brush("#ADB9B2") });
        _scope.ItemsSource=grouped ? new[] {"整个卡组簇（所有变种的共同标题）","当前变种（只修改这张完整牌表）"} : new[] {"当前卡组或对手记录"};
        _scope.SelectedIndex=0; _scope.Margin=new Thickness(0,10,0,8); panel.Children.Add(_scope);
        _name.Text=name; _name.Margin=new Thickness(0,0,0,12); panel.Children.Add(_name);
        panel.Children.Add(new TextBlock { Text="可直接输入名称，也可点击下面的社区名称填入。建议按阵营、领袖与关键卡排序，需由你确认玩法。",TextWrapping=TextWrapping.Wrap,FontSize=12 });
        var search = new TextBox { Margin=new Thickness(0,8,0,5),ToolTip="搜索社区名称或别名" };
        panel.Children.Add(search);
        var all = new CheckBox { Content="查看所有阵营的名称",Margin=new Thickness(0,0,0,5) }; panel.Children.Add(all);
        var template = new DataTemplate(typeof(CommunityNameSuggestion));
        var item = new FrameworkElementFactory(typeof(StackPanel)); item.SetValue(FrameworkElement.MarginProperty,new Thickness(5));
        var title=new FrameworkElementFactory(typeof(TextBlock)); title.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Name")); title.SetValue(TextBlock.ForegroundProperty, FactionPalette.Brush("#D9BC7B")); item.AppendChild(title);
        var reason=new FrameworkElementFactory(typeof(TextBlock)); reason.SetBinding(TextBlock.TextProperty,new System.Windows.Data.Binding("Summary")); reason.SetValue(TextBlock.FontSizeProperty,11d); reason.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap); item.AppendChild(reason);
        template.VisualTree=item; Suggestions.ItemTemplate=template; panel.Children.Add(Suggestions);
        var detail = new TextBlock { TextWrapping=TextWrapping.Wrap,FontSize=11,Margin=new Thickness(0,8,0,8) }; panel.Children.Add(detail);
        var source = new Button { Content="查看该名称的社区来源 ↗",HorizontalAlignment=HorizontalAlignment.Left,Padding=new Thickness(8,4,8,4),IsEnabled=false }; panel.Children.Add(source);
        Suggestions.SelectionChanged += (_,_) => {
            if (Suggestions.SelectedItem is not CommunityNameSuggestion suggestion) return;
            _name.Text=suggestion.Name;
            detail.Text=suggestion.Archetype.Description+"\n"+suggestion.Reason+"\n"+(suggestion.Archetype.Sources.FirstOrDefault()?.Title ?? "");
            source.IsEnabled=suggestion.Archetype.Sources.Length>0;
        };
        source.Click += (_,_) => {
            if (Suggestions.SelectedItem is not CommunityNameSuggestion suggestion || suggestion.Archetype.Sources.FirstOrDefault() is not { } evidence) return;
            try { Process.Start(new ProcessStartInfo(evidence.Url) { UseShellExecute=true }); }
            catch(Exception error) { detail.Text=error.Message; }
        };
        var save = new Button { Content="保存",IsDefault=true,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0),Padding=new Thickness(22,6,22,6) };
        save.Click += (_,_) => { DialogResult=true; }; panel.Children.Add(save);
        void Refresh() { Suggestions.ItemsSource=CommunityArchetypes.Suggest(faction,leader,cards,all.IsChecked==true,search.Text); }
        search.TextChanged += (_,_) => Refresh(); all.Checked += (_,_) => Refresh(); all.Unchecked += (_,_) => Refresh();
        Refresh(); Content=new ScrollViewer { Content=panel,VerticalScrollBarVisibility=ScrollBarVisibility.Auto };
    }
}
