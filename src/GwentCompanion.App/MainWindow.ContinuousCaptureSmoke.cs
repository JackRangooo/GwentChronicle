using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Windows;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunContinuousCaptureSmokeAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","continuous-capture-smoke");Directory.CreateDirectory(output);
        var fixture=Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(fixture);
        var checks=new List<string>();MainWindow? window=null;DiagnosticCaptureSession? capture=null;
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks.Add(why);}
        try
        {
            window=new MainWindow();window.Loaded-=window.OnLoaded;
            window._matchStorageDirectoryOverride=Path.Combine(fixture,"match-data");
            window._matchIdentity=InstallationIdentity.LoadOrCreate(Path.Combine(fixture,"installation.json"));
            window._candidateCatalog=GwentOneCardCatalog.Load(CardDataPath);window._libraryReady=true;
            window._dataContributionPreferences=DataContributionPreferences.Initial.Decide(false,DateTimeOffset.UtcNow);
            var cards=window._candidateCatalog.Where(c=>c.CanBeInStartingDeck && c.Kind==CardKind.Unit).Take(2).ToArray();
            window.BeginMatchAcquisition();window._gameState.Reset("continuous-fixture");
            var alive=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);window._visionWorker=alive.Task;
            window._visionCancellation=new CancellationTokenSource();
            var at=DateTimeOffset.UtcNow.AddHours(-1);
            var board=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,MatchHudVisible:true,UserScore:10,OpponentScore:8);
            CardVisionResult Frame(double seconds,GwentVisualObservation screen,CardDefinition? card=null)=>new(at.AddSeconds(seconds),screen,[],
                card is null?[]:[new(at.AddSeconds(seconds),new(card,PlayerSide.Opponent,CardSightSource.PlayPreview,new(.4,.2,.5,.4),.02,.5),"fixture")],false);
            window.ApplyVisionResult(Frame(0,board with {ScreenHeader="ROUND 1"},cards[0]));
            var firstId=window._matchAcquisition!.MatchId;
            var popup=board with {MatchHudVisible=false,PostMatchExitCue=true,ScreenHeader="STANDARD MODE",IsCardSelectionOverlay=true};
            for(var i=1;i<=3;i++){window.ApplyVisionResult(Frame(i*2,popup));window.ApplyVisionResult(Frame(i*2+1,board));}
            Check(window._matchAcquisition.MatchId==firstId && window._continuousMatchNumber==0 && ReferenceEquals(window._visionWorker,alive.Task),
                "Repeated popup/menu/return cycles preserve the same match and live recognition worker");
            window.ApplyVisionResult(Frame(8,board with {MatchHudVisible=false,ScreenHeader="VICTORY",IsCardSelectionOverlay=true}));
            window.ApplyVisionResult(Frame(9,board));
            Check(window._matchAcquisition.Snapshot()?.Result is null,"A one-frame false result does not enter the win-rate record");
            window._restoringReviewPreference=true;window.AutoStopOnMmrChoice.IsChecked=true;window._restoringReviewPreference=false;
            window.TryAutoStopOnMmr(Frame(9.1,popup with {PostMatchCaptureEnded=true,PostMatchMmr=new(2430,8,true,"fixture")}));
            Check(window._autoStopTask is null && !window._visionCancellation.IsCancellationRequested,"Legacy automatic-stop flags and next-game signals cannot stop capture");
            window.OpenLibrary_OnClick(window,new RoutedEventArgs());
            Check(ReferenceEquals(window._visionWorker,alive.Task) && window._libraryWindow is null,"Opening import while live leaves capture running and asks for explicit manual stop");
            var language=ScreenStateRecognizer.DefaultOcrLanguage;
            window.GameLanguageBox.SelectedIndex=language=="en-US"?0:1;
            Check(ScreenStateRecognizer.DefaultOcrLanguage==language && ReferenceEquals(window._visionWorker,alive.Task),"Changing recognition language cannot silently stop a live session");
            var result=board with {MatchHudVisible=false,ScreenHeader="VICTORY",PostMatchRoundScores=[new(1,30,10),new(2,42,20)]};
            window.ApplyVisionResult(Frame(10,result));window.ApplyVisionResult(Frame(10.4,result));
            window.ApplyVisionResult(Frame(11,board));window.ApplyVisionResult(Frame(12,popup));
            Check(window._matchAcquisition.MatchId==firstId && ReferenceEquals(window._visionWorker,alive.Task),"Result screens and fading boards save the result without terminating recording");
            window.ApplyVisionResult(Frame(14,board with {ScreenHeader="ROUND 1"},cards[1]));
            Check(window._matchAcquisition.MatchId==firstId,"One possible opening is held outside the completed match until corroborated");
            window.ApplyVisionResult(Frame(14.5,board with {ScreenHeader="ROUND 1"}));
            window.QueueMatchCheckpoint(force:true);await window._matchStorageWrite;
            var records=MatchAnalysis.Load(window.MatchStorageDirectory).Entries;var sources=records.Select(e=>LocalMatchStore.Read(e.Path)).ToArray();
            Check(sources.Length==2 && window._continuousMatchNumber==1 && ReferenceEquals(window._visionWorker,alive.Task),"A real next game creates a second record while the same recording session continues");
            var old=sources.Single(m=>m.MatchId==firstId);var next=sources.Single(m=>m.MatchId!=firstId);
            Check(old.CaptureStopped && old.Result=="VICTORY" && old.Opponent.Observations.All(c=>c.CardId!=cards[1].Id),"The first match keeps its result and cannot acquire next-game cards");
            Check(next.Opponent.Observations.Any(c=>c.CardId==cards[1].Id) && !next.CaptureStopped,"The held first opening frame reaches the second match without being lost");
            window.DiagnosticButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            Check(window._analysisTransition && !alive.Task.IsCompleted,"The real manual Stop button waits for queued recognition to finish");
            alive.SetResult();var until=DateTime.UtcNow.AddSeconds(15);
            while(window._analysisTransition && DateTime.UtcNow<until)await Task.Delay(25);
            Check(!window._analysisTransition && window._visionWorker is null,"Only explicit manual Stop ends the recognition session");
            Check(MatchAnalysis.Load(window.MatchStorageDirectory).Entries.All(e=>e.CaptureStopped),"Manual Stop flushes the last match and preserves both histories");
            window.HideLoadingShell();window.ShowPage(UiPage.Settings);DeckBuilderSmoke.Render(window,Path.Combine(output,"settings.png"),650,1100);
            window.Close();window=null;

            var pixels=new byte[640*360*4];var bitmap=BitmapSource.Create(640,360,96,96,PixelFormats.Bgra32,null,pixels,640*4);bitmap.Freeze();
            var snapshot=new GwentWindowSnapshot((nint)1,4242,"fixture",new(0,0,640,360),new(0,0,640,360),new(0,0,640,360),new(0,0,640,360),false,false);
            var reads=0;var attempts=0;var reconnects=0;var callbackFault=0;
            var recovered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            capture=new DiagnosticCaptureSession(null,Path.Combine(fixture,"capture"),
                snapshot:_=>{if(Interlocked.Increment(ref reads)<=1)throw new InvalidOperationException("window temporarily unavailable");return snapshot;},
                captureFrame:_=>{var n=Interlocked.Increment(ref attempts);if(n==1)throw new OperationCanceledException("transient non-user cancellation");if(n==2)throw new InvalidOperationException("temporary minimized window");return bitmap;},
                rediscoverWindow:pid=>{Interlocked.Increment(ref reconnects);return snapshot with{Handle=(nint)2};});
            capture.RetainTrainingFrames=true;
            capture.Progress+=(_,_)=>{if(Interlocked.Increment(ref callbackFault)==1)throw new InvalidOperationException("one broken observer");};
            capture.Progress+=(_,p)=>{if(p.CapturedFrames>=8 && p.SavedFrames>=1)recovered.TrySetResult();};
            capture.Start(snapshot);
            await Task.WhenAny(recovered.Task,Task.Delay(TimeSpan.FromSeconds(25)));
            Check(recovered.Task.IsCompleted && capture.IsRunning,"Transient window/capture/callback errors recover without ending the actual capture worker");
            Check(reconnects>0 && attempts>3,"The target is rediscovered and sampling resumes after window loss");
            var capturePath=capture.CurrentSessionDirectory!;await capture.StopAsync();
            Check(!capture.IsRunning && Directory.GetFiles(capturePath,"frame-*.jpg").Length>0,"Manual capture Stop drains and retains recorded frames");
            using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(capturePath,"session.json")));
            Check(manifest.RootElement.GetProperty("CapturedFrames").GetInt32()>=8,"The recording manifest confirms continued frames after the interruption");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks,LiveScreenCaptured=false,LiveDataChanged=false},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{if(capture is not null)await capture.DisposeAsync();if(window is not null){window._visionWorker=null;window._projectionQueue?.Dispose();await window._matchStorageWrite;window.Close();}}
    }
}
