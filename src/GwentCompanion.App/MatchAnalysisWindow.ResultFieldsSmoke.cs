using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunResultFieldsSmokeAsync(string imagePath)
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","result-fields-smoke");Directory.CreateDirectory(output);
        var checks=new List<string>();
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks.Add(why);}
        try
        {
            var screen=await ReadProvidedResultAsync(imagePath,withoutScores:true);
            Check(screen.PostMatchRoundScores is null && screen.ResultPlayers is {User.Rank:5,Opponent.Rank:5,Opponent.Name:"FixtureOpponentB"},
                "The real rank-five result independently confirms both badges and the opponent name even with no settled score table");
            var blank=await ReadProvidedResultAsync(imagePath,0,true);
            Check(blank.ResultPlayers is {User.Rank:5,Opponent.Rank:null},"Removing the opponent badge leaves it unknown rather than copying the user rank");
            foreach(var shift in new[]{-6,6})
            {
                var shifted=await ReadProvidedResultAsync(imagePath,shift,true);
                Check(shifted.ResultPlayers is {User.Rank:5,Opponent.Rank:5},"Both rank-five badges survive a six-pixel opponent-lane shift: "+shift);
            }
            await MainWindow.VerifyResultFieldsCheckpointAsync(Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N")),screen.ResultPlayers!);
            checks.Add("The live checkpoint path persists partial player fields, accepts later independent scores, and preserves user corrections");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
}

public partial class MainWindow
{
    internal static async Task VerifyResultFieldsCheckpointAsync(string directory,ResultPlayers players)
    {
        MainWindow? window=null;
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);}
        try
        {
            window=new MainWindow();window.Loaded-=window.OnLoaded;
            window._matchStorageDirectoryOverride=directory;
            window._matchIdentity=InstallationIdentity.LoadOrCreate(Path.Combine(directory,"installation.json"));
            window.BeginMatchAcquisition();window._gameState.Reset("result-fields-smoke");
            var at=DateTimeOffset.UtcNow;
            var board=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,ScreenHeader:"ROUND 1",MatchHudVisible:true,UserHandCount:8,OpponentHandCount:8);
            window.ApplyVisionResult(new(at,board,[],[],false));window.ApplyVisionResult(new(at.AddSeconds(1),board,[],[],false));
            var result=board with{ScreenHeader="VICTORY",MatchHudVisible=false,ResultPlayers=players};
            window.ApplyVisionResult(new(at.AddSeconds(2),result,[],[],false));await window.FlushMatchAcquisitionAsync();
            var path=Directory.GetFiles(directory,"*.gvm",SearchOption.AllDirectories).Single();var source=LocalMatchStore.Read(path);
            var profiles=new LocalMatchProfileStore(directory);var profile=profiles.Get(source);
            Check(profile?.User?.Rank==players.User.Rank && profile?.Opponent?.Rank==players.Opponent.Rank && profile?.Opponent?.Name==players.Opponent.Name && profile?.ResultRoundScores is null,"Partial player fields never reached the live saved checkpoint.");
            profiles.Set(source,profile! with{Opponent=profile!.Opponent! with{Rank=4,RankSource="Manual"},CountForLadder=false});
            window.ApplyVisionResult(new(at.AddSeconds(3),result with{ResultPlayers=null,PostMatchRoundScores=[new(1,45,63),new(2,3,0),new(3,41,40)]},[],[],false));
            await window.FlushMatchAcquisitionAsync();
            Check(profiles.Get(source) is {Opponent.Rank:4,CountForLadder:false,ResultRoundScores.Length:3} settled && settled.Opponent.Name==players.Opponent.Name,"Later scores lost independent fields or overwrote a user correction.");
        }
        finally{if(window is not null){await window._matchStorageWrite;window._projectionQueue?.Dispose();window.Close();}}
    }
}
