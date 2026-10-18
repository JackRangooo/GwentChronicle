using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private bool _expandedWorkspace, _wideWorkspace, _fullScreen, _applyingWorkspace;
    private double _workspaceZoom = 1;
    private Rect? _compactBounds, _windowedBounds;
    private WindowState _compactWindowState, _windowedState;

    private void WorkspaceToggle_OnClick(object sender, RoutedEventArgs e)
    {
        try { SetExpandedWorkspace(!_expandedWorkspace); }
        catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; }
    }

    private void SetExpandedWorkspace(bool expanded)
    {
        if (_expandedWorkspace == expanded) return;
        if (expanded)
        {
            _compactWindowState = WindowState;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _compactBounds = DesktopDisplays.Bounds(this);
            _expandedWorkspace = true;
            var display = DesktopDisplays.Nearest(_compactBounds.Value, DesktopDisplays.All());
            DesktopDisplays.Place(this, display.WorkArea);
        }
        else
        {
            if (_fullScreen) SetFullScreen(false);
            _expandedWorkspace = false;
            WindowState = WindowState.Normal;
            if (_compactBounds is { } bounds)
            {
                var display = DesktopDisplays.Nearest(bounds, DesktopDisplays.All());
                DesktopDisplays.Place(this, DesktopDisplays.Fit(bounds, display.WorkArea));
            }
            WindowState = _compactWindowState;
        }
        UpdateWorkspaceLayout();
    }

    private void SetFullScreen(bool fullScreen)
    {
        if (_fullScreen == fullScreen) return;
        if (fullScreen)
        {
            if (!_expandedWorkspace) SetExpandedWorkspace(true);
            _windowedState = WindowState;
            if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
            _windowedBounds = DesktopDisplays.Bounds(this);
            _fullScreen = true;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            var display = DesktopDisplays.Nearest(_windowedBounds.Value, DesktopDisplays.All());
            DesktopDisplays.Place(this, display.Bounds);
        }
        else
        {
            _fullScreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            if (_windowedBounds is { } bounds)
                DesktopDisplays.Place(this, DesktopDisplays.Fit(bounds, DesktopDisplays.Nearest(bounds, DesktopDisplays.All()).WorkArea));
            WindowState = _windowedState;
        }
        UpdateWorkspaceLayout();
    }

    private void Workspace_OnKeyDown(object sender, KeyEventArgs e)
    {
        if(e.Key==Key.Escape && _liveInspectorOpen && !_wideWorkspace){CloseLiveInspector_OnClick(this,new RoutedEventArgs());e.Handled=true;return;}
        if(e.Key==Key.Escape && !_fullScreen && _page==UiPage.Library && !_libraryWide && _libraryShowingDetails){LibraryBrowse_OnClick(this,new RoutedEventArgs());e.Handled=true;return;}
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _page is UiPage.Library or UiPage.Candidates)
        {
            if(_page==UiPage.Library && !_libraryWide && _libraryShowingDetails) LibraryBrowse_OnClick(this,new RoutedEventArgs());
            var query = _page == UiPage.Candidates ? CandidateCardSearch : LibrarySearch.Query;
            query.Focus(); query.SelectAll(); e.Handled = true; return;
        }
        if (e.Key != Key.F11 && !(e.Key == Key.Escape && _fullScreen)) return;
        e.Handled = true;
        try { SetFullScreen(e.Key == Key.F11 && !_fullScreen); }
        catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; }
    }

    private void WorkspaceDisplay_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        var menu = CreateWorkspaceDisplayMenu();
        menu.PlacementTarget = target; menu.Placement = PlacementMode.Bottom; menu.IsOpen = true;
    }

    private ContextMenu CreateWorkspaceDisplayMenu()
    {
        var menu = new ContextMenu();
        void Add(string label, Action action, bool check = false)
        {
            var item = new MenuItem { Header = label, IsChecked = check };
            item.Click += (_, _) => { try { action(); } catch (Exception error) { FooterStatusText.Text = "Display change failed: " + error.Message; } };
            menu.Items.Add(item);
        }
        Add(_fullScreen ? "Exit full screen · F11 / Esc" : "Enter full screen · F11", () => SetFullScreen(!_fullScreen));
        menu.Items.Add(new Separator());
        var displays = DesktopDisplays.All();
        var current = DesktopDisplays.Nearest(DesktopDisplays.Bounds(this), displays);
        foreach (var (display, index) in displays.Select((d, i) => (d, i)))
            Add($"Display {index + 1}{(display.Primary ? " · primary" : "")} · {display.Bounds.Width:0} × {display.Bounds.Height:0}", () => MoveWorkspaceToDisplay(display.Id), display.Id == current.Id);
        menu.Items.Add(new Separator());
        foreach (var zoom in new[] { 1d, 1.15, 1.3 })
            Add($"Content size · {zoom:P0}", () => { _workspaceZoom = zoom; UpdateWorkspaceLayout(); }, Math.Abs(_workspaceZoom - zoom) < .01);
        Add("Return to compact companion", () => SetExpandedWorkspace(false));
        return menu;
    }

    private void MoveWorkspaceToDisplay(string id)
    {
        // Resolve again at click time: a monitor may have been unplugged while the menu was open.
        var target = DesktopDisplays.All().FirstOrDefault(d => d.Id == id);
        if (target is null) { FooterStatusText.Text = "That display is no longer connected. Open Display to choose again."; return; }
        if (!_expandedWorkspace) SetExpandedWorkspace(true);
        WindowState = WindowState.Normal;
        DesktopDisplays.Place(this, _fullScreen ? target.Bounds : target.WorkArea);
        if (_fullScreen) { _windowedBounds = target.WorkArea; _windowedState = WindowState.Normal; }
        UpdateWorkspaceLayout();
    }

    private void UpdateWorkspaceLayout(double? width = null,double? height = null)
    {
        if (BrandTitle is null || Pages is null || _applyingWorkspace) return;
        _applyingWorkspace = true;
        try
        {
            var available=(width ?? ActualWidth)/_workspaceZoom;
            _wideWorkspace=available>=1180;
            WorkspaceBody.LayoutTransform=new ScaleTransform(_workspaceZoom,_workspaceZoom);
            HoverThreatViewport.MaxHeight=200;
            var shortWindow=(height??ActualHeight)/_workspaceZoom<760;
            ReachDisclosure.Visibility=shortWindow?Visibility.Collapsed:Visibility.Visible;
            LiveConstructionDisclosure.Visibility=shortWindow?Visibility.Collapsed:Visibility.Visible;
            OpponentProvisionText.Visibility=shortWindow?Visibility.Collapsed:Visibility.Visible;
            LiveFootnote.Visibility=shortWindow?Visibility.Collapsed:Visibility.Visible;
            BrandTitle.FontSize=available<520?14:18;
            SideNavigation.Visibility=_wideWorkspace?Visibility.Visible:Visibility.Collapsed;
            PrimaryNavigation.Visibility=_wideWorkspace?Visibility.Collapsed:Visibility.Visible;
            WorkspaceDisplayButton.Visibility=available<500?Visibility.Collapsed:Visibility.Visible;
            WorkspaceToggleIcon.Data=Geometry.Parse(_expandedWorkspace?"M 5,1 L 15,1 L 15,11 M 1,5 L 11,5 L 11,15 L 1,15 Z":"M 6,1 L 1,1 L 1,6 M 10,1 L 15,1 L 15,6 M 1,10 L 1,15 L 6,15 M 10,15 L 15,15 L 15,10");
            WorkspaceToggle.ToolTip=_expandedWorkspace?"Return to compact companion":"Expanded workspace · F11 for full screen";
            AutomationProperties.SetName(WorkspaceToggle,_expandedWorkspace?"Return to compact companion":"Expand workspace");
            WindowLayout.Margin=new Thickness(available<500?10:18);
            Grid.SetRow(PlayerDeckBanner,3);Grid.SetColumn(PlayerDeckBanner,0);Grid.SetColumnSpan(PlayerDeckBanner,3);
            var isLive=_page is UiPage.Plays or UiPage.Deck or UiPage.Candidates or UiPage.Pinned;
            ReachDisclosure.Visibility=isLive&&!shortWindow?Visibility.Visible:Visibility.Collapsed;
            PageBackBar.Visibility=_page==UiPage.Memory?Visibility.Visible:Visibility.Collapsed;
            Pages.ColumnDefinitions[0].Width=new GridLength(1,GridUnitType.Star);
            Pages.ColumnDefinitions[1].Width=new GridLength(_wideWorkspace&&isLive?12:0);
            Pages.ColumnDefinitions[2].Width=new GridLength(_wideWorkspace&&isLive?300:0);
            foreach(var page in Pages.Children.OfType<FrameworkElement>().Where(c=>c!=WorkspaceLaneHeadings)) {
                Grid.SetRow(page,1);Grid.SetColumn(page,0);Grid.SetColumnSpan(page,3);page.Margin=new Thickness(0);
                page.Visibility=page.Name==_page+"Page"?Visibility.Visible:Visibility.Collapsed;
            }
            WorkspaceLaneHeadings.Visibility=Visibility.Collapsed;
            if(_wideWorkspace&&isLive) {
                DeckPage.Visibility=Visibility.Visible;Grid.SetColumnSpan(DeckPage,1);
                FrameworkElement context=_page==UiPage.Candidates?CandidatesPage:_page==UiPage.Pinned?PinnedPage:_page==UiPage.Plays?PlaysPage:LiveInspector;
                context.Visibility=Visibility.Visible;Grid.SetColumn(context,2);Grid.SetColumnSpan(context,1);
                CloseLiveInspectorButton.Visibility=Visibility.Collapsed;
            } else if(isLive&&_liveInspectorOpen&&_page==UiPage.Deck) {
                DeckPage.Visibility=Visibility.Collapsed;LiveInspector.Visibility=Visibility.Visible;CloseLiveInspectorButton.Visibility=Visibility.Visible;
            }
            LiveModeBar.Visibility=isLive?Visibility.Visible:Visibility.Collapsed;
            PlayerDeckBanner.Visibility=isLive?Visibility.Visible:Visibility.Collapsed;
            _libraryWide=available>=900;ArrangeLibraryColumns(_libraryWide);
            OpponentDeckCards.RowHeight=available<500?48:56;OpponentDeckCards.ShowDetails=true;
            LibraryDeckCards.RowHeight=52;LibraryManagementScroll.MaxHeight=available<500?100:180;
            // Keep the single review controls beside their faction synergy meters.
            if (SynergyHistory.Parent != SynergyHistoryHost)
            {
                if (SynergyHistory.Parent is Panel oldParent) oldParent.Children.Remove(SynergyHistory);
                SynergyHistoryHost.Children.Add(SynergyHistory); SynergyHistory.Visibility = Visibility.Visible;
            }
        }
        finally { _changingNavigation = false; _applyingWorkspace = false; }
    }

    private static void ApplyBrowserColumns(Grid grid, bool wide)
    {
        grid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        grid.ColumnDefinitions[1].Width = new GridLength(wide ? 24 : 0);
        grid.ColumnDefinitions[2].Width = wide ? new GridLength(1.15, GridUnitType.Star) : new GridLength(0);
    }
    private static void Cell(FrameworkElement control, int row, int column, int rows = 1, int columns = 1)
    {
        Grid.SetRow(control, row); Grid.SetColumn(control, column); Grid.SetRowSpan(control, rows); Grid.SetColumnSpan(control, columns);
    }
    private void ArrangeLibraryColumns(bool wide)
    {
        LibraryLayout.ColumnDefinitions[0].Width=wide ? new GridLength(340) : new GridLength(1,GridUnitType.Star);
        LibraryLayout.ColumnDefinitions[1].Width=new GridLength(wide ? 18 : 0);
        LibraryLayout.ColumnDefinitions[2].Width=wide ? new GridLength(1,GridUnitType.Star) : new GridLength(0);
        LibraryLayout.RowDefinitions[1].Height = new GridLength(1,GridUnitType.Star);
        LibraryLayout.RowDefinitions[2].Height=LibraryLayout.RowDefinitions[3].Height=new GridLength(0);
        LibraryLayout.RowDefinitions[1].MinHeight=100;LibraryLayout.RowDefinitions[3].MinHeight=0;
        var details=!wide && _libraryShowingDetails;
        LibraryToolbar.Visibility=DeckList.Visibility=details ? Visibility.Collapsed : Visibility.Visible;
        LibraryInspector.Visibility=wide || details ? Visibility.Visible : Visibility.Collapsed;
        LibraryPreviewHeading.Visibility=LibraryDeckCards.Visibility=wide || details ? Visibility.Visible : Visibility.Collapsed;
        LibraryBrowseButton.Visibility=details ? Visibility.Visible : Visibility.Collapsed;
        Cell(LibraryToolbar,0,0);Cell(DeckList,1,0,3);
        Cell(LibraryInspector,0,wide ? 2 : 0,4);
    }
}
