using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _libraryShowingDetails,_libraryWide;
    private void OpenLibraryWorkspace()
    {
        SetExpandedWorkspace(true);
        var area=DesktopDisplays.Nearest(DesktopDisplays.Bounds(this),DesktopDisplays.All()).WorkArea;
        var w=Math.Min(1280,area.Width-32);var h=Math.Min(940,area.Height-32);
        DesktopDisplays.Place(this,new Rect(area.Left+(area.Width-w)/2,area.Top+(area.Height-h)/2,w,h));
        UpdateWorkspaceLayout();
    }
    private void LibraryBrowse_OnClick(object sender,RoutedEventArgs e)
    { _libraryShowingDetails=false;UpdateWorkspaceLayout(); }
    private void ShowLibraryDetails()
    { _libraryShowingDetails=true;UpdateWorkspaceLayout(); }
    private void LibrarySource_OnChanged(object sender,SelectionChangedEventArgs e)
    {if(LibrarySearch is not null && DeckList is not null) ApplyDeckFilter();}
    private void LibraryList_OnKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key==Key.Enter && DeckList.SelectedItem is DeckListItem){ShowLibraryDetails();RefreshLibraryPreview();e.Handled=true;}
        if(e.Key==Key.F2 && DeckList.SelectedItem is DeckListItem){DeckDisplayName_OnClick(sender,new RoutedEventArgs());e.Handled=true;}
    }
    private void LibraryMore_OnClick(object sender,RoutedEventArgs e)
    {
        var menu=new ContextMenu();
        void Add(string name,RoutedEventHandler action,bool enabled=true) {
            var entry=new MenuItem {Header=name,IsEnabled=enabled};entry.Click+=action;menu.Items.Add(entry);
        }
        Add("重命名卡组或卡组簇…",DeckDisplayName_OnClick,DeckList.SelectedItem is not null);
        Add("导出当前变种…",ExportLibraryDeck_OnClick,DeckList.SelectedItem is DeckListItem {Deck:not null});
        Add("打开原始牌表网页 ↗",(_,_)=> {
            if(DeckList.SelectedItem is DeckListItem item && item.SourceUri is { } source && GwentCompanion.Core.Data.DeckLinkFileReader.CanonicalUrl(source) is { } url)
                try {System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri){UseShellExecute=true});}
                catch(Exception error){FooterStatusText.Text="无法打开来源网页："+error.Message;}
        },DeckList.SelectedItem is DeckListItem {SourceUri:not null});
        menu.Items.Add(new Separator());
        Add("删除当前牌表",DeleteLibraryDeck_OnClick,DeleteSelectedDeckButton.IsEnabled);
        menu.PlacementTarget=sender as FrameworkElement;menu.IsOpen=true;
    }
}
