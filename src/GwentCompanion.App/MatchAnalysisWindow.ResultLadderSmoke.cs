using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private static async Task<GwentVisualObservation> ReadProvidedResultAsync(string imagePath, int? opponentShift = null,bool withoutScores=false)
    {
        using var stream=File.OpenRead(imagePath);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var prepared=VisionFrameNormalizer.Prepare(BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]),false);
        if(!prepared.Supported)throw new InvalidDataException("Unsupported result screenshot geometry.");
        var frame=prepared.Frame;
        if(opponentShift is { } shift)
        {
            var pixels=frame.BgraPixels.ToArray();var source=frame.BgraPixels;
            var left=(int)(frame.Width*.875);var right=(int)(frame.Width*.935);
            var top=(int)(frame.Height*.125);var bottom=(int)(frame.Height*.225);
            for(var y=top;y<bottom;y++)for(var x=left;x<right;x++)
            {
                var from=x-shift;var offset=(y*frame.Width+x)*4;
                if(shift==0 || from<left || from>=right){pixels[offset]=pixels[offset+1]=pixels[offset+2]=0;pixels[offset+3]=255;}
                else Array.Copy(source,(y*frame.Width+from)*4,pixels,offset,4);
            }
            frame=new(frame.Width,frame.Height,pixels);
        }
        using var reader=new ScreenStateRecognizer("zh-Hans-CN");using var playerReader=new ResultPlayerRecognizer();var scores=new PostMatchScoreRecognizer();
        var initial=await reader.AnalyzeAsync(frame);var observed=initial;var at=DateTimeOffset.UtcNow;
        for(var i=0;i<3;i++)
        {
            observed=await scores.ReadAsync(frame,initial,at.AddMilliseconds(i*250),reader);
            if(withoutScores)observed=observed with{PostMatchRoundScores=null};
            observed=await playerReader.ReadAsync(frame,observed,at.AddMilliseconds(i*250),reader);
        }
        return observed;
    }
    internal static async Task<int> RunResultLadderSmokeAsync(string imagePath)
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","result-ladder-smoke");Directory.CreateDirectory(output);
        var checks=new List<string>();MatchAnalysisWindow? window=null;MatchReviewWindow? editor=null;
        void Check(bool ok,string label){if(!ok)throw new InvalidOperationException(label);checks.Add(label);}
        try
        {
            var screen=await ReadProvidedResultAsync(imagePath);
            Check(screen.ScreenHeader=="DEFEAT" && screen.ResultPlayers is {User.Name:"FixtureUser",User.Rank:6,Opponent.Name:"FixtureOpponentA",Opponent.Rank:6},
                "Real result screenshot confirms both rank badges and both names.");
            Check(screen.PostMatchRoundScores is [{UserScore:25,OpponentScore:37},{UserScore:4,OpponentScore:11}],
                "Two actual rounds are retained; the unused third 0–0 is excluded.");
            var shiftedRight=await ReadProvidedResultAsync(imagePath,6);
            Check(shiftedRight.ResultPlayers is {User.Rank:6,Opponent.Rank:6},"Moving only the opponent badge six normalized pixels right retains both independently read ranks.");
            var shiftedLeft=await ReadProvidedResultAsync(imagePath,-6);
            Check(shiftedLeft.ResultPlayers is {User.Rank:6,Opponent.Rank:6},"Moving only the opponent badge six normalized pixels left retains both independently read ranks.");
            var blankOpponent=await ReadProvidedResultAsync(imagePath,0);
            Check(blankOpponent.ResultPlayers is {User.Rank:6,Opponent.Rank:null},"A blank opponent badge stays unknown even when the user badge clearly reads six.");
            Check(!JsonSerializer.Serialize(screen).Contains("FixtureOpponentA"),"Result-screen names stay out of diagnostic JSON.");
            Check(LadderProgress.Step(new(6,0),MatchOutcome.Loss)==new LadderPosition(6,0),"Losing at zero pieces never drops a rank.");
            Check(LadderProgress.Step(new(6,4),MatchOutcome.Win)==new LadderPosition(6,5),"The fifth piece completes the mosaic without early promotion.");
            Check(LadderProgress.Step(new(6,5),MatchOutcome.Win)==new LadderPosition(5,0),"One further win after five pieces promotes and clears pieces.");
            Check(LadderProgress.Step(new(6,3),MatchOutcome.Loss)==new LadderPosition(6,2),"Each loss removes exactly one available piece.");
            Check(LadderProgress.Step(new(6,3),MatchOutcome.Draw)==new LadderPosition(6,3),"A draw leaves pieces unchanged.");
            var streak=new LadderPosition(7,0);for(var i=0;i<3;i++)streak=LadderProgress.Step(streak,MatchOutcome.Win);
            Check(streak==new LadderPosition(7,3),"Three wins at rank seven have no win-streak bonus.");
            Check(LadderProgress.Step(new(0,0),MatchOutcome.Win)==new LadderPosition(0,0),"Pro Rank does not create mosaic pieces.");
            var invalid=false;try{LadderProgress.Validate(new(8,0));}catch(ArgumentException){invalid=true;}
            Check(invalid,"Lower ladder ranks with different streak rules are not silently simulated.");
            var directory=Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N"));var store=new LocalMatchStore(directory);
            var player=new MatchPlayer("Northern Realms","Inspired Zeal",null,[],[],[]);var install=Guid.NewGuid();var at=new DateTimeOffset(2026,10,15,1,54,12,TimeSpan.Zero);
            CompactMatch Game(int n,string result="VICTORY")=>new(install,new Guid(n,0,0,new byte[8]),new(2026,10,15),"fixture","fixture","14.10",false,1,true,true,result,
                null,null,null,false,null,player,player,[],[],StartedAtUtc:at.AddMinutes(n));
            var old=Game(-1,"DEFEAT");var oldPath=store.Save(old);var rawHash=SHA256.HashData(File.ReadAllBytes(oldPath));
            var profiles=new LocalMatchProfileStore(directory);profiles.MergeResultPlayers(old,screen.ResultPlayers!,screen.PostMatchRoundScores!);
            var profile=profiles.Get(old)!;
            Check(profile is {User.Rank:6,Opponent.Rank:6,Opponent.Name:"FixtureOpponentA",Opponent.RankSource:"ResultBanner",ResultRoundScores.Length:2},
                "Confirmed result badges, name and settled rounds persist beside original records.");
            profiles.Set(old,profile with{Opponent=profile.Opponent! with{Name="我的标记",NameSource="Manual",Rank=5,RankSource="Manual"},CountForLadder=false});
            profiles.MergeResultPlayers(old,screen.ResultPlayers!,screen.PostMatchRoundScores!);
            Check(profiles.Get(old) is {Opponent.Name:"我的标记",Opponent.Rank:5,CountForLadder:false},"Manual identity/rank corrections and casual exclusion survive automatic result reads.");
            profiles.Set(old,profile with{Opponent=profile.Opponent! with{Rank=5,RankSource="UserConfirmed"}});
            profiles.MergeResultPlayers(old,screen.ResultPlayers!,screen.PostMatchRoundScores!);
            Check(profiles.Get(old)?.Opponent is {Rank:5,RankSource:"UserConfirmed"},"A user-confirmed opponent rank cannot be overwritten by a later automatic badge read.");
            profiles.Set(old,profile);
            var ladder=new LadderProgressStore(directory);var calibrated=ladder.Calibrate(new(6,0),MatchAnalysis.Load(directory).Entries,at);
            Check(LadderProgress.Build(calibrated.Baseline,MatchAnalysis.Load(directory).Entries).Games.Length==0,"Calibration excludes all existing matches.");
            var ongoing=MatchAnalysisEntry.From("fixture",Game(20) with{Result=null,CaptureStopped=false});
            var otherCheckpoint=new LadderProgressStore(Path.Combine(output,"other-calibration",Guid.NewGuid().ToString("N"))).Calibrate(new(6,0),[ongoing],at);
            Check(LadderProgress.Build(otherCheckpoint.Baseline,[ongoing with{Outcome=MatchOutcome.Win}]).Games.Length==0,"Calibration also excludes captures that already existed but had not finished.");
            var newGames=new List<CompactMatch>();for(var i=1;i<=6;i++){newGames.Add(Game(i));store.Save(newGames[^1]);}
            var six=MatchAnalysis.Load(directory).Entries;
            var sixProgress=LadderProgress.Build(calibrated.Baseline,six);
            Check(sixProgress is {Current.Rank:5,Current.Fragments:0,Games.Length:6,NeedsCalibration:false},"Six subsequent wins produce one promotion, with no duplicate counting.");
            Check(LadderProgress.Build(calibrated.Baseline,six).Current==sixProgress.Current,"Refreshing/reopening does not reapply a result twice.");
            profiles.Set(newGames[^1],new(CountForLadder:false));
            Check(LadderProgress.Build(calibrated.Baseline,MatchAnalysis.Load(directory).Entries).Current==new LadderPosition(6,5),"Casual games can be excluded independently of win-rate statistics.");
            profiles.Set(newGames[^1],new());
            Check(LadderProgress.Build(calibrated.Baseline,six.Select(e=>e.MatchId==newGames[0].MatchId?e with{StartedAtUtc=null}:e)).NeedsCalibration,
                "Unordered games pause the estimate rather than invent a sequence.");
            Check(LadderProgress.Build(calibrated.Baseline,six.Select(e=>e.MatchId==newGames[0].MatchId?e with{Patch="14.11"}:e)).NeedsCalibration,
                "A changed patch/season requires calibration.");
            Check(LadderProgress.Build(calibrated.Baseline,six.Select(e=>e.MatchId==newGames[0].MatchId?e with{LocalProfile=new(User:new(Rank:4))}:e)).NeedsCalibration,
                "A mismatching visible rank pauses estimates.");
            var pro=calibrated.Baseline with{Position=new(1,5)};
            Check(LadderProgress.Build(pro,six).NeedsCalibration,"Pro Rank promotion is not assumed before the game confirms it.");
            Check(LadderProgress.Build(pro,six.Take(1).Select(e=>e with{Rank=0})).Current.IsPro,"A confirmed post-match rank zero permits the Pro Rank transition.");
            Check(LadderProgress.Build(pro with{Position=new(0,0)},six.Take(1).Select(e=>e with{Date=new(2026,11,1),Patch="14.11"})).NeedsCalibration,
                "Pro Rank still detects a later season rather than skipping reset checks.");
            var utcBoundary=calibrated.Baseline with{AtUtc=new DateTimeOffset(2026,10,31,16,30,0,TimeSpan.Zero)};
            Check(!LadderProgress.Build(utcBoundary,six.Take(1).Select(e=>e with{Date=new(2026,10,31)})).NeedsCalibration,
                "UTC game dates and calibration use the same month at the Asia/Shanghai month boundary.");
            Check(LadderProgress.Build(calibrated.Baseline,six.Take(1).Select(e=>e with{IncludeInStatistics=false,LocalProfile=new(CountForLadder:true)})).Games.Length==1,
                "An explicit ladder choice is independent of the general statistics checkbox.");
            window=new MatchAnalysisWindow(directory,AppContext.BaseDirectory){WindowState=WindowState.Normal,Width=1440,Height=1000};window.Show();
            var deadline=DateTime.UtcNow.AddSeconds(20);while((window._all.Length!=7 || !window.RefreshButton.IsEnabled) && DateTime.UtcNow<deadline)await Task.Delay(25);
            window.DisplayChoice.SelectedIndex=2;window.Render();
            Check(window._ladderSnapshot?.Current==new LadderPosition(5,0) && window.LadderPieces.Items.Count==5,"Statistics shows the current rank and five mosaic slots.");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"ladder-statistics.png"),1440,1000);
            editor=new MatchReviewWindow(old,old,null,[],[],profile){Owner=window};editor.Show();
            IEnumerable<DependencyObject> Descendants(DependencyObject parent)
            {for(var n=0;n<VisualTreeHelper.GetChildrenCount(parent);n++){var child=VisualTreeHelper.GetChild(parent,n);yield return child;foreach(var nested in Descendants(child))yield return nested;}}
            editor.UpdateLayout();
            Check(!Descendants(editor).OfType<Localization.LocalizedTextBlock>().Any(t=>t.Text.Contains("Account level") || t.Text.Contains("Prestige")),
                "The editor removes account-level and prestige fields.");
            DeckBuilderSmoke.Render(editor,Path.Combine(output,"result-record-editor.png"),760,780);editor.Close();editor=null;
            var live=Game(7);store.Save(live);deadline=DateTime.UtcNow.AddSeconds(12);
            while(window._ladderSnapshot?.Current.Fragments!=1 && DateTime.UtcNow<deadline)await Task.Delay(50);
            Check(window._ladderSnapshot?.Current==new LadderPosition(5,1),"A new saved result updates the open statistics window automatically.");
            Check(rawHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(oldPath))),"Calibration, result metadata and live updates preserve the original raw record.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{editor?.Close();window?.Close();}
    }
    private sealed record LadderInitialization(int Rank,int Fragments,DateTimeOffset AtUtc,Guid[] ExcludedMatchIds);
    internal static async Task<int> InitializeResultLadderAsync(string[] args)
    {
        var i=Array.IndexOf(args,"--initialize-result-ladder");var directory=args[i+1];var report=args[i+4];
        try
        {
            var plan=JsonSerializer.Deserialize<LadderInitialization>(File.ReadAllText(args[i+2]))!;
            var entries=MatchAnalysis.Load(directory).Entries;var past=plan.ExcludedMatchIds.Select(id=>entries.Single(e=>e.MatchId==id)).ToArray();
            var hashes=entries.ToDictionary(e=>e.Path,e=>SHA256.HashData(File.ReadAllBytes(e.Path)));
            var store=new LadderProgressStore(directory);if(store.Load() is not null)throw new InvalidDataException("A ladder checkpoint already exists.");
            var screenshot=await ReadProvidedResultAsync(args[i+3]);
            if(screenshot.ResultPlayers is not {User.Rank:6,Opponent.Rank:6,Opponent.Name:"FixtureOpponentA"} || screenshot.PostMatchRoundScores is not{Length:2})
                throw new InvalidDataException("The provided result screenshot was not confirmed.");
            var entry=entries.Single(e=>e.Outcome==MatchOutcome.Loss && e.LocalProfile?.Opponent?.Name==screenshot.ResultPlayers.Opponent.Name);
            var match=LocalMatchStore.Read(entry.Path);
            if(match.Rounds.Length!=2 || !match.Rounds.Zip(screenshot.PostMatchRoundScores).All(pair=>pair.First.Number==pair.Second.Round &&
                pair.First.UserScore==pair.Second.UserScore && pair.First.OpponentScore==pair.Second.OpponentScore))throw new InvalidDataException("Screenshot rounds do not match the unique recorded opponent.");
            new LocalMatchProfileStore(directory).MergeResultPlayers(match,screenshot.ResultPlayers,screenshot.PostMatchRoundScores);
            var settings=store.Calibrate(new(plan.Rank,plan.Fragments),past,plan.AtUtc);
            using var window=new WindowCloser(new MatchAnalysisWindow(directory,AppContext.BaseDirectory){WindowState=WindowState.Normal,Width=1600,Height=1100});
            await window.Window.RefreshAsync();window.Window.DisplayChoice.SelectedIndex=2;window.Window.Render();
            DeckBuilderSmoke.Render(window.Window,Path.Combine(Path.GetDirectoryName(report)!,"actual-ladder-statistics-v8.png"),1600,1100);
            var preserved=hashes.All(p=>p.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key))));
            var progress=LadderProgress.Build(settings.Baseline,MatchAnalysis.Load(directory).Entries);
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=preserved,RawRecordsPreserved=preserved,ConfirmedMatch=entry.MatchId,
                ResultPlayers=screenshot.ResultPlayers,Rounds=screenshot.PostMatchRoundScores,Baseline=settings.Baseline,Current=progress.Current,LaterRankedGames=progress.Games.Length},new JsonSerializerOptions{WriteIndented=true}));return preserved?0:1;
        }
        catch(Exception error){File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=false,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
    private sealed class WindowCloser(MatchAnalysisWindow window):IDisposable
    {public MatchAnalysisWindow Window=>window;public void Dispose()=>window.Close();}
}
