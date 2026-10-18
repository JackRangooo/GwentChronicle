using System.IO;
using System.Text.Json;
using System.Security.Cryptography;
using GwentCompanion.Core.Data;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;
public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunResultReplaySmokeAsync(string imageDirectory)
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","result-replay-smoke");Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks.Add(why);}
        try
        {
            foreach(var name in new[]{"preserved-tool-image-9.jpg","preserved-tool-image-6.jpg"})
            {
                var screen=await ReadProvidedResultAsync(Path.Combine(imageDirectory,name));
                Check(screen.ResultPlayers is {User.Name:"FixtureUser",User.Rank:5,Opponent.Name:"星悦橙狐",Opponent.Rank:6},"Real recorded result confirms distinct names, player rank 5 and opponent rank 6: "+name);
                Check(screen.PostMatchRoundScores is [{UserScore:58,OpponentScore:15},{UserScore:0,OpponentScore:5},{UserScore:71,OpponentScore:75}],"All three settled rounds survive the Chinese numeric-OCR failure: "+name);
                await MainWindow.VerifyResultFieldsCheckpointAsync(Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N")),screen.ResultPlayers!);
            }
            var moving=await ReadProvidedResultAsync(Path.Combine(imageDirectory,"preserved-tool-image-7.jpg"));
            Check(moving.ResultPlayers is null && moving.PostMatchRoundScores is null,"A sliding transition after Next is not mistaken for the settled table");
            using var menuStream=File.OpenRead(Path.Combine(imageDirectory,"preserved-tool-image-2.jpg"));var decoder=BitmapDecoder.Create(menuStream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var menu=BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);using var reader=new ScreenStateRecognizer("zh-Hans-CN");using var names=new BoardPlayerNameRecognizer();
            var falseBoard=new GwentVisualObservation(GwentViewKind.Board,false,0,0,null,MatchHudVisible:true);
            var blocked=await names.ReadAsync(menu,falseBoard,DateTimeOffset.UtcNow,reader);
            Check(blocked.PlayerNames is null,"The real menu is rejected by its Back control even when the board HUD flag is falsely positive");
            var consensus=new BoardPlayerNameConsensus();var at=DateTimeOffset.UtcNow;
            for(var i=0;i<3;i++)consensus.Observe(new("FixtureUser","FixtureUser"),at.AddSeconds(i));
            Check(consensus.UserName=="FixtureUser" && consensus.OpponentName is null,"The user's own confirmed handle cannot fill the opponent lane");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
    }
    internal static async Task<int> RestoreResultFieldsAsync(string matchPath,string image,string report)
    {
        try
        {
            var source=LocalMatchStore.Read(matchPath);var directory=Path.GetDirectoryName(Path.GetDirectoryName(matchPath)!)!;
            var hash=SHA256.HashData(File.ReadAllBytes(matchPath));var reviews=new MatchReviewStore(directory);var beforeReview=JsonSerializer.Serialize(reviews.Get(source));
            var screen=await ReadProvidedResultAsync(image);
            if(screen.ResultPlayers is not {User.Name:not null,User.Rank:not null,Opponent.Name:not null,Opponent.Rank:not null} ||
                screen.PostMatchRoundScores is not {Length:>=2} || screen.ScreenHeader!=MatchReviewStore.Apply(source,reviews.Get(source)).Result)
                throw new InvalidDataException("Result image fields are incomplete or differ from the user's corrected outcome.");
            new LocalMatchProfileStore(directory).MergeResultPlayers(source,screen.ResultPlayers,screen.PostMatchRoundScores);
            var preserved=hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(matchPath))) && beforeReview==JsonSerializer.Serialize(reviews.Get(source));
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=preserved,source.MatchId,RawAndReviewsPreserved=preserved,Players=screen.ResultPlayers,Scores=screen.PostMatchRoundScores},new JsonSerializerOptions{WriteIndented=true}));return preserved?0:1;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
    }
    internal static async Task<int> ProbeResultNumbersAsync(string image,string report)
    {
        try
        {
            using var input=File.OpenRead(image);var bitmap=BitmapDecoder.Create(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var frame=VisionFrameNormalizer.Prepare(BitmapFrameAdapter.ToPixelFrame(bitmap.Frames[0]),false).Frame;
            var glyphs=JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"vision-assets","result-score-digits.json")))!;
            var digits=new HudDigitReader(glyphs);var rows=new List<object>();
            foreach(var language in new[]{"zh-Hans-CN","en-US"})
            {
                using var reader=new ScreenStateRecognizer(language);
                foreach(var nameLane in new[]{new NormalizedRegion(.643,.137,.844,.184),new(.637,.12,.84,.23),new(.64,.13,.73,.182)})
                foreach(var scale in new[]{1,2,3,4})foreach(var enhance in new[]{false,true})foreach(var mask in new[]{false,true})
                    rows.Add(new{language,NameLane=nameLane,Scale=scale,Enhance=enhance,Mask=mask,Normal=await reader.ReadLinesAsync(frame,nameLane,scale:scale,enhance:enhance,whiteLetterMask:mask,smooth:true)});
                foreach(var top in new[]{.347,.514,.680})foreach(var left in new[]{.405,.535})
                {
                    var lane=new NormalizedRegion(left,top,left+.057,top+.069);var trace=new List<string>();HudDigitReader.Trace=trace.Add;
                    var glyph=digits.Read(frame,new(left+.009,top,left+.048,top+.069));HudDigitReader.Trace=null;
                    rows.Add(new{language,Lane=lane,Glyph=glyph,Trace=trace,Normal=await reader.ReadLinesAsync(frame,lane,scale:3,enhance:false,smooth:true),
                        White=await reader.ReadLinesAsync(frame,lane,scale:4,enhance:false,whiteLetterMask:true,smooth:true)});
                }
            }
            File.WriteAllText(report,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
        finally{HudDigitReader.Trace=null;}
    }
}
