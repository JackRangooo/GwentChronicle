using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private FileSystemWatcher? _localDataWatcher;
    private FileSystemWatcher? _libraryNameWatcher;
    private DispatcherTimer? _localDataRefresh;
    private bool _localDataChanged;
    private bool _deckNamesChanged;
    private LadderProgressSnapshot? _ladderSnapshot;
    private void InitializeLadderUpdates()
    {
        _localDataRefresh=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(900)};
        _localDataRefresh.Tick+=async(_,_)=>
        {
            _localDataRefresh.Stop();
            if(_lifetime.IsCancellationRequested || !IsVisible || (DisplayChoice.SelectedIndex!=2 && !_deckNamesChanged))return;
            if(!RefreshButton.IsEnabled){_localDataRefresh.Start();return;}
            _localDataChanged=false;_deckNamesChanged=false;await RefreshAsync();
        };
        if(Directory.Exists(_directory))
        {
            _localDataWatcher=new FileSystemWatcher(_directory){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
            void Changed(string path)
            {
                if(!path.EndsWith(".gvm",StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))return;
                if(_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted)return;
                _=Dispatcher.BeginInvoke(new Action(()=>{if(_lifetime.IsCancellationRequested)return;_localDataChanged=true;_localDataRefresh.Stop();_localDataRefresh.Start();}));
            }
            _localDataWatcher.Changed+=(_,e)=>Changed(e.FullPath);_localDataWatcher.Created+=(_,e)=>Changed(e.FullPath);
            _localDataWatcher.Deleted+=(_,e)=>Changed(e.FullPath);_localDataWatcher.Renamed+=(_,e)=>Changed(e.FullPath);
            _localDataWatcher.EnableRaisingEvents=true;
        }
        var cache=Path.Combine(_dataRoot,"cache");
        if(Directory.Exists(cache))
        {
            _libraryNameWatcher=new FileSystemWatcher(cache){IncludeSubdirectories=true,NotifyFilter=NotifyFilters.FileName|NotifyFilters.LastWrite};
            void NameChanged(string path)
            {
                var filename=Path.GetFileName(path);
                if(filename is not "deck-library.json" and not "deck-display-names.json" &&
                    !(Path.GetDirectoryName(path)?.StartsWith(Path.Combine(cache,"observed-decks"),StringComparison.OrdinalIgnoreCase)==true && filename.EndsWith(".json",StringComparison.OrdinalIgnoreCase)))return;
                if(_lifetime.IsCancellationRequested || Dispatcher.HasShutdownStarted)return;
                _=Dispatcher.BeginInvoke(new Action(()=>{if(_lifetime.IsCancellationRequested)return;_deckNamesChanged=true;_localDataRefresh.Stop();_localDataRefresh.Start();}));
            }
            _libraryNameWatcher.Changed+=(_,e)=>NameChanged(e.FullPath);_libraryNameWatcher.Created+=(_,e)=>NameChanged(e.FullPath);
            _libraryNameWatcher.Deleted+=(_,e)=>NameChanged(e.FullPath);_libraryNameWatcher.Renamed+=(_,e)=>NameChanged(e.FullPath);
            _libraryNameWatcher.EnableRaisingEvents=true;
        }
        Activated+=(_,_)=>{if(_deckNamesChanged)_localDataRefresh?.Start();};
        Closed+=(_,_)=>{_localDataWatcher?.Dispose();_libraryNameWatcher?.Dispose();_localDataRefresh?.Stop();};
    }
    private void RenderLadderProgress()
    {
        if(_localDataChanged && RefreshButton.IsEnabled){_localDataRefresh?.Start();}
        var settings=new LadderProgressStore(_directory).Load();
        _ladderSnapshot=settings is null?null:LadderProgress.Build(settings.Baseline,_all) with{Series=LadderProgress.History(settings,_all)};
        string T(string value)=>UiTranslation.Translate(value);
        if(_ladderSnapshot is not {} snapshot)
        {
            LadderPositionText.Text="Set your current rank and pieces to begin tracking.";LadderPieces.ItemsSource=null;
            LadderProgressNote.Text="Calibrate once, then completed ranked games update this estimate automatically. Exclude casual games in Edit record.";return;
        }
        var current=snapshot.Current;
        LadderPositionText.Text=current.IsPro?T("Pro Rank"):UiTranslation.Translate($"Rank {current.Rank} · {current.Fragments}/5 pieces");
        LadderPieces.ItemsSource=current.IsPro?[]:Enumerable.Range(1,5).Select(i=>new{
            Fill=Controls.FactionPalette.Brush(i<=current.Fragments?"#D3B66D":"#27332F"),
            Border=Controls.FactionPalette.Brush(i<=current.Fragments?"#F0D68D":"#536158")}).ToArray();
        LadderProgressNote.Text=snapshot.NeedsCalibration?T(snapshot.Reason!):current.IsPro
            ?T("Pro Rank uses faction MMR; mosaic estimates stop here.")
            :T(snapshot.Games.Length==0?"User-calibrated starting position. No later ranked results counted yet.":"Estimated from recorded ranked results after calibration.");
        LadderProgressNote.Text+="\n"+UiTranslation.Translate($"{snapshot.Games.Length} games since calibration")+" · "+
            settings!.Baseline.AtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")+"\n"+T("Ranks 7–1: win +1 piece, loss −1 down to zero, draw unchanged; winning with 5 pieces promotes you. Casual games must be excluded.");
        LadderPositionText.Foreground=snapshot.NeedsCalibration?Brushes.Salmon:Controls.FactionPalette.Brush("#E3C789");
    }
    private async void CalibrateLadderClicked(object sender,RoutedEventArgs e)
    {
        var dialog=new Window{Title=UiTranslation.Translate("Calibrate ladder position"),Owner=this,Width=420,SizeToContent=SizeToContent.Height,
            WindowStartupLocation=WindowStartupLocation.CenterOwner,ResizeMode=ResizeMode.NoResize,Background=Controls.FactionPalette.Brush("#11191C"),Foreground=Brushes.White};
        var panel=new StackPanel{Margin=new Thickness(20)};
        var rank=new ComboBox{ItemsSource=Enumerable.Range(0,8),SelectedItem=_ladderSnapshot?.Current.Rank??6,Margin=new Thickness(0,5,0,12)};
        var pieces=new ComboBox{ItemsSource=Enumerable.Range(0,6),SelectedItem=_ladderSnapshot?.Current.Fragments??0,Margin=new Thickness(0,5,0,12)};
        panel.Children.Add(new TextBlock{Text="Use the position currently shown in Gwent. Existing games are kept; only subsequent ranked results change this checkpoint.",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,14)});
        panel.Children.Add(new TextBlock{Text="Ladder rank (0–7; 0 = Pro Rank)"});panel.Children.Add(rank);
        panel.Children.Add(new TextBlock{Text="Mosaic pieces (0–5)"});panel.Children.Add(pieces);
        rank.SelectionChanged+=(_,_)=>{pieces.IsEnabled=!Equals(rank.SelectedItem,0);if(!pieces.IsEnabled)pieces.SelectedItem=0;};
        var buttons=new WrapPanel{HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button{Content="Cancel",IsCancel=true,Margin=new Thickness(0,0,8,0)};
        var save=new Button{Content="Save calibration",IsDefault=true};save.Click+=(_,_)=>dialog.DialogResult=true;
        buttons.Children.Add(cancel);buttons.Children.Add(save);panel.Children.Add(buttons);dialog.Content=panel;
        if(dialog.ShowDialog()!=true)return;
        try
        {
            // Refresh while keeping the entered current position, so games completed while the dialog was open are excluded from the new baseline.
            await RefreshAsync();new LadderProgressStore(_directory).Calibrate(new((int)rank.SelectedItem,(int)pieces.SelectedItem),_all);
            Render();
        }
        catch(Exception error){StatusText.Text=UiTranslation.Translate("Could not calibrate ladder position: ")+error.Message;}
    }
}
