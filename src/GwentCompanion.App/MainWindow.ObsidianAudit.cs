using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static void RenderNativeWindow(Window window,string path,double scale=1)
    {
        window.UpdateLayout();var surface=(FrameworkElement)VisualTreeHelper.GetChild(window,0);
        var image=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth*scale),(int)Math.Ceiling(surface.ActualHeight*scale),96*scale,96*scale,PixelFormats.Pbgra32);
        image.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
        using var file=File.Create(path);encoder.Save(file);
    }
    internal static async Task<int> RunObsidianAuditAsync(bool baseline)
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","obsidian-ui",baseline?"before":"after");Directory.CreateDirectory(output);
        var checks=new List<string>();var shots=new List<object>();MainWindow? window=null;MatchAnalysisWindow? history=null;
        var proof=SHA256.HashData(File.ReadAllBytes(LibraryPath));
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks.Add(why);}
        async Task Settle(){await Task.Delay(180);await Application.Current.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);window?.UpdateLayout();}
        try {
            var library=DeckLibrary.Load(LibraryPath);var catalog=GwentOneCardCatalog.Load(CardDataPath);
            var deck=library.Decks.First(d=>d.CardCount==25 && d.Name.Contains("秋季大师赛"));
            window=new MainWindow();window.Loaded-=window.OnLoaded;window._matchStorageDirectoryOverride=Path.Combine(output,"isolated-matches");
            window._library=library;window._libraryReady=true;window._cachedDecks=library.Decks;window._candidateCatalog=catalog;
            window._selectedUserDeck=deck;window._useOpponentModel=false;
            Localization.DeckDisplayNames.ConfigureGroups(library);window.HideLoadingShell();window.Show();
            var observed=deck.Cards.Select(c=>new ObservedCard(c.Card,CardProvenance.ConfirmedStartingDeck,1,DateTimeOffset.UtcNow,"隔离布局样例：源自赛事牌表，非实际识别对局",c.Count)).ToArray();
            window._lastProjection=new OpponentDeckProjector().Build(library.Decks,observed,deck.Faction,catalog:catalog);
            window.RenderCompletedProjection();window.GameStatusText.Text="布局验证 · 25 张真实牌表样例 · 未启动采集";
            window.SetAnalysisButtonState(false);window.DiagnosticButton.IsEnabled=true;window.SnapshotButton.IsEnabled=true;
            foreach(var (w,h) in new[]{(1600,1000),(1280,800),(1024,768),(410,860),(370,620)}) {
                window.WindowState=WindowState.Normal;window.Width=w;window.Height=h;window._expandedWorkspace=w>=900;window._workspaceZoom=1;
                window.ShowPage(UiPage.Deck);window.UpdateWorkspaceLayout(w);await Settle();
                RenderNativeWindow(window,Path.Combine(output,$"live-{w}x{h}.png"));
                shots.Add(new {File=$"live-{w}x{h}.png",RequestedWidth=w,RequestedHeight=h,ActualWidth=window.ActualWidth,ActualHeight=window.ActualHeight,Dpi=VisualTreeHelper.GetDpi(window).PixelsPerInchX,Rows=25,Source="real tournament deck / isolated layout state"});
                if(!baseline) {
                    var stop=window.DiagnosticButton.TransformToAncestor(window).TransformBounds(new Rect(window.DiagnosticButton.RenderSize));
                    var snap=window.SnapshotButton.TransformToAncestor(window).TransformBounds(new Rect(window.SnapshotButton.RenderSize));
                    Check(stop.Top>=0&&stop.Bottom<=window.ActualHeight&&stop.Width>=90,$"{w}x{h}: labeled start/stop stays within the window.");
                    Check(snap.Top>=0&&snap.Bottom<=window.ActualHeight&&snap.Width>=60,$"{w}x{h}: screenshot remains reachable with 25 cards.");
                    Check(window.OpponentDeckCards.ActualHeight>=120,$"{w}x{h}: observed cards have a bounded usable viewport.");
                    if(w==1024)Check(window.OpponentDeckCards.ActualWidth>=620&&window.PinnedPage.Visibility==Visibility.Collapsed,"1024 DIP prioritizes the main card list without three competing columns.");
                    if(w>=1180)Check(window.DeckPage.IsVisible&&!(window.PinnedPage.IsVisible&&window.CandidatesPage.IsVisible),"Wide layout has one primary list and at most one secondary context pane.");
                }
            }
            if(!baseline) {
                window.Width=1280;window.Height=800;window._workspaceZoom=1;window.ShowPage(UiPage.Deck);
                var inspected=deck.Cards.First().Card;window.MarkCandidateSeen(inspected,1);window.RenderLiveInference();
                await window._projectionQueue!.Idle.WaitAsync(TimeSpan.FromSeconds(30));await Settle();
                var row=((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).First(r=>r.Slot.Card?.Id==inspected.Id);
                var before=window._opponentEdits.ApplyEvidenceCorrections(window._opponentTracker.DeckBuildingObservations).Sum(o=>o.ObservedCopies);
                window.LiveCardSelected(null,row);await Settle();
                Check(window.LiveInspector.IsVisible&&window.LiveRejectButton.IsEnabled,"Selecting a card exposes actual source, ability and correction actions.");
                RenderNativeWindow(window,Path.Combine(output,"live-inspector-wide.png"));
                window.LiveRejectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window._projectionQueue.Idle.WaitAsync(TimeSpan.FromSeconds(30));
                Check(window._opponentEdits.ApplyEvidenceCorrections(window._opponentTracker.DeckBuildingObservations).Sum(o=>o.ObservedCopies)==before-1,"Visible correction removes exactly one effective copy, preserving the raw sighting.");
                var generated=deck.Cards.Last().Card;window._opponentTracker.ConsiderDirectPlay(generated,1,DateTimeOffset.UtcNow,"生成记录验证",CardProvenance.Created);
                window._opponentTracker.SetProvenance(generated.Id,CardProvenance.Created,"生成记录验证");
                window.LiveSourceChoice.SelectedIndex=1;window.RenderCompletedProjection();
                window.LiveCardSelected(null,((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).First());
                window.LiveUndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await window._projectionQueue.Idle.WaitAsync(TimeSpan.FromSeconds(30));
                Check(window._opponentEdits.ApplyEvidenceCorrections(window._opponentTracker.DeckBuildingObservations).Sum(o=>o.ObservedCopies)==before,"Visible undo restores the effective card count.");
                Check(!window.LiveRejectButton.IsEnabled,"Undoing another card never enables starting-deck rejection for a selected generated card.");
                window.LiveReject_OnClick(window,new RoutedEventArgs());
                Check(window._opponentEdits.ApplyEvidenceCorrections(window._opponentTracker.DeckBuildingObservations).Sum(o=>o.ObservedCopies)==before,"The correction handler independently rejects generated-card removal.");
                window.LiveSourceChoice.SelectedIndex=0;window.RenderCompletedProjection();window.LiveCardSelected(null,row);
                window.Width=410;window.Height=860;window.UpdateWorkspaceLayout(410);await Settle();
                Check(window.LiveInspector.IsVisible&&window.CloseLiveInspectorButton.IsVisible,"Compact selection opens visible same-window details with a return action.");
                RenderNativeWindow(window,Path.Combine(output,"live-inspector-410.png"));window.CloseLiveInspector_OnClick(window,new RoutedEventArgs());
                Check(window.DeckPage.IsVisible,"Returning from compact details restores the main card list.");
                window.LiveSourceChoice.SelectedIndex=1;window.RenderCompletedProjection();
                Check(((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).All(r=>r.Slot.Reason.StartsWith("生成")),"Generated-view rows are explicit generated records, never matched-deck predictions.");
                window.LiveSourceChoice.SelectedIndex=0;
                window._lastProjection=new OpponentDeckProjector().Build(library.Decks,observed,deck.Faction,catalog:catalog);window.RenderCompletedProjection();
                var stress=((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).ToArray();window.OpponentDeckCards.Rows=Enumerable.Range(0,40).Select(i=>stress[i%stress.Length]).ToArray();await Settle();
                var realized=Enumerable.Range(0,40).Count(i=>window.OpponentDeckCards.CardRows.ItemContainerGenerator.ContainerFromIndex(i) is not null);
                Check(realized<40&&VirtualizingPanel.GetIsVirtualizing(window.OpponentDeckCards.CardRows),"Forty-row card fixture retains actual WPF item virtualization.");
                RenderNativeWindow(window,Path.Combine(output,"compact-40-rows.png"));window.RenderCompletedProjection();
                window._matchIdentity=InstallationIdentity.LoadOrCreate(Path.Combine(output,"isolated-installation.json"));window.BeginMatchAcquisition();
                var manual=window.LiveEvidenceEditor()!;manual.SetCopies(inspected,1);window.RenderLiveInference();
                await window._projectionQueue.Idle.WaitAsync(TimeSpan.FromSeconds(30));await Settle();
                window.LiveCardSelected(null,((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).First(r=>r.Slot.Card?.Id==inspected.Id));
                window.LiveRejectButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));manual.SetCopies(generated,1);
                window.LiveUndoButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Check(manual.Count(inspected.Id)==1&&manual.Count(generated.Id)==1,"Undo restores only its card and preserves later confirmed manual edits.");
                await window._matchStorageWrite;window._matchAcquisition=null;window._matchIdentity=null;
                var unknown=new MatchPlayer(null,null,null,[],[],[]);var directory=window._matchStorageDirectoryOverride;
                var source=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,18),"UI-audit","UI-audit","未标注",false,1,true,true,"DEFEAT",null,null,null,false,null,unknown,unknown,[],[]);
                new LocalMatchStore(directory).Save(source);
                var editor=new OpponentEvidenceEditor(directory,MatchReviewStore.Key(source),[],deck.Faction).WithCatalog(catalog);
                var draft=new MatchEvidenceWindow(editor,catalog,deck.Faction,library.Decks){Owner=window};draft.LoadDraft(deck);draft.Show();await Settle();
                RenderNativeWindow(draft,Path.Combine(output,"bulk-draft-native.png"));Check(editor.Corrections.Count==0,"Native bulk-draft review is unsaved until explicit confirmation.");draft.Close();
            }
            var oldBounds=DesktopDisplays.Bounds(window);window.ShowPage(UiPage.Library);await Settle();
            if(!baseline)Check(DesktopDisplays.Bounds(window)==oldBounds,"Navigation to library preserves window placement and size.");
            window.Width=1280;window.Height=800;window.UpdateWorkspaceLayout(1280);window.SetDeckListItems(window.CreateDeckItems(library.Decks));
            window.DeckList.SelectedItem=window.DeckList.Items.Cast<DeckListItem>().First(i=>i.Deck is not null);await Settle();
            RenderNativeWindow(window,Path.Combine(output,"library.png"));
            window.ShowPage(UiPage.Settings);await Settle();RenderNativeWindow(window,Path.Combine(output,"settings.png"));
            if(!baseline) {
                Check(window.FindName("ReducedTransparencyChoice") is CheckBox,"Reduced-transparency setting is present.");
                var reduced=(CheckBox)window.FindName("ReducedTransparencyChoice");reduced.IsChecked=true;await Settle();RenderNativeWindow(window,Path.Combine(output,"settings-solid.png"));reduced.IsChecked=false;
            }
            window.ShowPage(UiPage.Deck);window.Width=1024;window.Height=768;await Settle();
            foreach(var scale in new[]{1.25,1.5,2.0}) {
                window._workspaceZoom=scale;window.UpdateWorkspaceLayout(1024);await Settle();RenderNativeWindow(window,Path.Combine(output,$"content-scale-{scale:0.00}.png"),scale);
            }
            window._workspaceZoom=1;window.UpdateWorkspaceLayout();
            var card=deck.Cards[0].Card;
            var preview=new CardPreviewWindow(card,Path.Combine(ResolveArtCacheDirectory(),card.Id+".jpg")){Owner=window};preview.Show();await Settle();preview.Topmost=true;
            RenderNativeWindow(preview,Path.Combine(output,"card-pinned.png"));preview.Close();
            var picker=new DeckLibraryPickerWindow(library.Decks,catalog){Owner=window};picker.Show();await Settle();RenderNativeWindow(picker,Path.Combine(output,"library-picker.png"));picker.Close();
            var matches=Path.Combine(PersistentUserDataDirectory,"match-data");
            history=new MatchAnalysisWindow(matches,FindDataRoot()){WindowState=WindowState.Normal,Width=1280,Height=900};history.Show();await history.RefreshAsync();await Settle();
            RenderNativeWindow(history,Path.Combine(output,"history.png"));
            var expand=VisualChildren(history).OfType<Expander>().FirstOrDefault(e=>e.DataContext?.GetType().Name=="HistoryVm");
            if(expand is not null){expand.IsExpanded=true;await Settle();RenderNativeWindow(history,Path.Combine(output,"history-expanded.png"));}
            history.DisplayChoice.SelectedIndex=2;await Settle();RenderNativeWindow(history,Path.Combine(output,"statistics.png"));
            Check(window._visionWorker is null&&window._diagnosticSession is null,"Audit never starts recognition/capture.");
            Check(proof.SequenceEqual(SHA256.HashData(File.ReadAllBytes(LibraryPath))),"Audit preserves the real library byte-for-byte.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=true,Baseline=baseline,Checks=checks,Screenshots=shots,MonitorDpi=VisualTreeHelper.GetDpi(window).PixelsPerInchX,ScaleNote="Content-scale and render checks, not physical monitor DPI changes"},new JsonSerializerOptions{WriteIndented=true}));return 0;
        } catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=false,Baseline=baseline,Checks=checks,Screenshots=shots,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally {history?.Close();window?.Close();}
    }
}
