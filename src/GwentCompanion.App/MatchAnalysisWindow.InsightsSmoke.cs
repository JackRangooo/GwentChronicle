using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Platform.Windows.Capture;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunInsightsSmokeAsync(string[] args)
    {
        var root = AppContext.BaseDirectory; var output = Path.Combine(root,"diagnostics","match-insights-smoke");
        Directory.CreateDirectory(output); var checks = new List<string>(); MatchAnalysisWindow? window = null; MatchReviewWindow? editor = null;
        void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            var fixture = Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N")); var rawStore = new LocalMatchStore(fixture);
            var player = new MatchPlayer("Northern Realms","Inspired Zeal",null,[],[],[]);
            var install = Guid.NewGuid(); var games = new List<CompactMatch>();
            string[] results = ["DEFEAT","DEFEAT","DEFEAT","VICTORY","VICTORY","DEFEAT","VICTORY","VICTORY","DEFEAT","DEFEAT","VICTORY"];
            for (var i=0;i<11;i++)
            {
                var game = new CompactMatch(install,Guid.NewGuid(),new(2026,10,14),"fixture","fixture","14.10",false,1,true,true,
                    results[i],null,null,null,false,null,player,player,[],[],StartedAtUtc:new DateTimeOffset(2026,10,14,12+i,0,0,TimeSpan.Zero));
                games.Add(game);rawStore.Save(game);
            }
            var hashes = Directory.GetFiles(fixture,"*.gvm",SearchOption.AllDirectories).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            var initial = MatchAnalysis.Load(fixture).Entries; var totals = MatchAnalysis.Totals(initial);
            Check(totals is {Games:11,Wins:5,Losses:6,MmrSamples:0},"Ordinary ladder results count independently of missing MMR.");
            var resultsSeries=MatchAnalysis.ResultProgress(initial);
            Check(resultsSeries.Metric==MatchProgressMetric.WinBalance && resultsSeries.Points.Length==11 && resultsSeries.Points[^1].Rating==-1,
                "Chronological win/loss progression ends at the actual 5–6 balance.");
            var profiles = new LocalMatchProfileStore(fixture);
            foreach (var game in games) profiles.Set(game,new(User:new(Rank:6,Source:"UserConfirmed")));
            profiles.Set(games[0],new(new("FixtureUser",60,2,6),new("Opponent_测试",51,1,5)));
            var loaded=MatchAnalysis.Load(fixture).Entries;
            Check(MatchAnalysis.RankProgress(loaded).Points.All(p=>p.Rating==6) && loaded.All(e=>e.FactionMmr is null && e.Rank is null),
                "Confirmed rank stays local and does not fabricate detector rank or MMR.");
            var profile=profiles.Get(games[0]);
            Check(profile?.Opponent is {Name:"Opponent_测试",AccountLevel:51,Prestige:1,Rank:5},"Both player profiles persist independently of deck metadata.");
            foreach(var invalid in new[] {new LocalPlayerProfile(AccountLevel:61),new(Rank:31),new(Prestige:11),new(Name:"bad\nname")})
            {
                var rejected=false;try{profiles.Set(games[0],new(User:invalid));}catch(ArgumentException){rejected=true;}
                Check(rejected,"Invalid player field is rejected before replacing existing local details.");
            }
            Check(profiles.Get(games[0])?.Opponent?.Name=="Opponent_测试","Rejected input retains the previous profile.");
            profiles.MergeRecognizedNames(games[0],"WrongAutomaticName","WrongOpponent");
            Check(profiles.Get(games[0])?.User?.Name=="FixtureUser" && profiles.Get(games[0])?.Opponent?.Name=="Opponent_测试",
                "Automatic recognition never overwrites a manually corrected name.");
            profiles.Set(games[0],profile! with {User=profile!.User! with{Name=null,NameSource="Manual"}});
            profiles.MergeRecognizedNames(games[0],"FixtureUser",null);
            Check(profiles.Get(games[0])?.User?.Name is null,"Explicitly clearing a name prevents subsequent OCR from filling it back in.");
            Check(!JsonSerializer.Serialize(new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,PlayerNames:new("PRIVATE_USER","PRIVATE_OTHER"))).Contains("PRIVATE_"),
                "Player name candidates are excluded from diagnostic JSON logs.");
            var json=JsonSerializer.Serialize(MatchReviewStore.Apply(LocalMatchStore.Read(hashes.Keys.First()),new(Result:"VICTORY")));
            Check(!json.Contains("FixtureUser") && !json.Contains("Opponent_测试") && !json.Contains("AccountLevel"),"Compact export/upload payload has no local identifying profile fields.");
            var consensus=new BoardPlayerNameConsensus();var at=DateTimeOffset.UtcNow;
            consensus.Observe(new("FixtureUser","Other"),at);consensus.Observe(new("FixtureUser","Other"),at);
            Check(consensus.UserName is null,"Repeated delivery of one capture cannot confirm a name.");
            consensus.Observe(new("FixtureUser","Different"),at.AddSeconds(1));consensus.Observe(new("FixtureUser","Other"),at.AddSeconds(2));
            Check(consensus.UserName=="FixtureUser" && consensus.OpponentName is null,"Conflicting opponent OCR never inherits the user's three votes.");
            consensus.Observe(new("FixtureUser","Other"),at.AddSeconds(3));consensus.Observe(new("FixtureUser","Other"),at.AddSeconds(4));
            Check(consensus.OpponentName=="Other","Three agreeing capture-separated names are retained as editable OCR.");
            Check(new BoardPlayerNameConsensus().OpponentName is null,"New match name consensus starts empty.");
            Check(BoardPlayerNameRecognizer.Parse([new("13",new(.1,.02,.12,.04))],BoardPlayerNameRecognizer.OpponentLane) is null,
                "Pure numbers from counters are never names.");
            Check(BoardPlayerNameRecognizer.Parse([new("Other",new(.1,.02,.15,.04)),new("Title",new(.1,.04,.15,.05))],BoardPlayerNameRecognizer.OpponentLane) is null,
                "An ambiguous name/title lane is rejected.");
            var unordered=initial.Select(e=>e with{StartedAtUtc=null}).ToArray();
            Check(MatchAnalysis.ResultProgress(unordered).Points.All(p=>p.Rating is null),"Missing within-day order does not invent a trajectory.");
            window=new MatchAnalysisWindow(fixture,root){WindowState=WindowState.Normal,Width=1440,Height=1000};window.Show();
            var loadDeadline=DateTime.UtcNow.AddSeconds(20);while((window._all.Length!=11 || !window.RefreshButton.IsEnabled) && DateTime.UtcNow<loadDeadline)await Task.Delay(25);
            Check(window._all.Length==11,"Statistics window finishes its initial data load.");window.DisplayChoice.SelectedIndex=2;window.Render();
            Check(window.ProgressCharts.Items.Count==2 && window.RatingCharts.Items.Count==0 && window.RatingEmptyNotice.Visibility==Visibility.Visible,
                "Six-rank matches show result and rank charts with one concise MMR notice.");
            Check(window.FactionResults.Items.Count==1,"Faction win-rate summary appears without MMR.");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"statistics.png"),1440,1000);
            window.DisplayChoice.SelectedIndex=0;DeckBuilderSmoke.Render(window,Path.Combine(output,"history.png"),1440,1000);
            editor=new MatchReviewWindow(games[0],games[0],null,[],[],profile){Owner=window};editor.Show();
            DeckBuilderSmoke.Render(editor,Path.Combine(output,"edit-player-details.png"),760,940);
            Check(hashes.All(p=>p.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key)))),"Player edits, statistics and rendering leave every original raw record unchanged.");
            var pixels = new List<object>();
            var index=Array.IndexOf(args,"--name-frames");
            if(index>=0)
            {
                using var reader=new ScreenStateRecognizer("zh-CN");
                using var readerNames=new BoardPlayerNameRecognizer();var sample=DateTimeOffset.UtcNow;var realNames=0;
                foreach(var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[index+1]))!)
                {
                    using var stream=File.OpenRead(path);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                    var frame=BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
                    var screen=await readerNames.ReadAsync(frame,new(GwentViewKind.Board,false,0,0,null,MatchHudVisible:true),sample,reader);
                    sample=sample.AddSeconds(2);pixels.Add(new{Path=path,screen.PlayerNames});
                    File.WriteAllText(Path.Combine(output,"name-pixels.json"),JsonSerializer.Serialize(pixels,new JsonSerializerOptions{WriteIndented=true}));
                    Check(screen.PlayerNames?.User is null or "FixtureUser","Retained real board frame either reads the exact own name or leaves uncertainty blank.");
                    if(screen.PlayerNames?.User=="FixtureUser")realNames++;
                    var rejected=await readerNames.ReadAsync(frame,new(GwentViewKind.Board,false,0,0,null,MatchHudVisible:false),sample,reader);
                    Check(rejected.PlayerNames is null,"Menu/non-board HUD does not trigger name OCR.");
                }
                Check(realNames>=4,"At least four of five retained real board frames recover the exact own name.");
                var beforeCalls=reader.OcrCalls;readerNames.SetEnabled(false,false);
                using var lastFrame=File.OpenRead(JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[index+1]))![^1]);
                var last=BitmapFrameAdapter.ToPixelFrame(BitmapDecoder.Create(lastFrame,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames[0]);
                await readerNames.ReadAsync(last,new(GwentViewKind.Board,false,0,0,null,MatchHudVisible:true),sample.AddSeconds(2),reader);
                Check(reader.OcrCalls==beforeCalls,"Confirmed names disable further name OCR until a new match.");
            }
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks,Pixels=pixels},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally {editor?.Close();window?.Close();}
    }

    internal static int ConfirmLocalRank(string[] args)
    {
        var i=Array.IndexOf(args,"--confirm-local-rank");var directory=args[i+1];var plan=JsonSerializer.Deserialize<Guid[]>(File.ReadAllText(args[i+2]))!;
        var rank=int.Parse(args[i+3]);var report=args[i+4];
        try
        {
            if(rank is <0 or >30 || plan.Length!=11 || plan.Distinct().Count()!=11)throw new InvalidDataException("Invalid confirmed eleven-game plan.");
            var entries=MatchAnalysis.Load(directory).Entries;var selected=plan.Select(id=>entries.Single(e=>e.MatchId==id && e.IsCompleted)).ToArray();
            var hashes=selected.ToDictionary(e=>e.Path,e=>SHA256.HashData(File.ReadAllBytes(e.Path)));
            var store=new LocalMatchProfileStore(directory);
            foreach(var entry in selected)
            {
                var source=LocalMatchStore.Read(entry.Path);var previous=store.Get(source)??new();var user=previous.User??new();
                store.Set(source,previous with{User=user with{Rank=rank,Source="UserConfirmed"}});
            }
            var preserved=hashes.All(p=>p.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key))));
            File.WriteAllText(report,JsonSerializer.Serialize(new{Applied=true,Games=selected.Length,Rank=rank,RawRecordsPreserved=preserved},new JsonSerializerOptions{WriteIndented=true}));
            return preserved?0:1;
        }
        catch(Exception error){File.WriteAllText(report,JsonSerializer.Serialize(new{Applied=false,Error=error.ToString()}));return 1;}
    }
}
