using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class ResultSummaryPixelProbe
{
    internal static void Train(string path,string output,string label="6")
    {
        using var stream=File.OpenRead(path);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var frame=VisionFrameNormalizer.Prepare(BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]),false).Frame;
        File.WriteAllText(output,JsonSerializer.Serialize(HudDigitReader.Train(frame,ResultPlayerRecognizer.UserRankLane,label)
            .Concat(HudDigitReader.Train(frame,ResultPlayerRecognizer.OpponentRankLane,label)),new JsonSerializerOptions{WriteIndented=true}));
    }
    internal static async Task Run(string path,string report)
    {
        using var stream=File.OpenRead(path);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var original=BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
        using var reader=new ScreenStateRecognizer("zh-Hans-CN");
        var frame=VisionFrameNormalizer.Prepare(original,false).Frame;
        var screen=await reader.AnalyzeAsync(frame);var initial=screen;
        var scores=new PostMatchScoreRecognizer();using var players=new ResultPlayerRecognizer();var at=DateTimeOffset.UtcNow;
        var samples=new List<object>();
        for(var i=0;i<4;i++)
        {
            screen=await scores.ReadAsync(frame,initial,at.AddMilliseconds(i*250),reader);
            screen=await players.ReadAsync(frame,screen,at.AddMilliseconds(i*250),reader);
            samples.Add(new{screen.ScreenHeader,screen.PostMatchRoundScores,screen.ResultPlayers});
        }
        var diagnostics=new List<object>();
        foreach(var language in new[]{"zh-Hans-CN","en-US"})
        {
            using var numeric=new ScreenStateRecognizer(language);
            foreach(var lane in new[]{ResultPlayerRecognizer.UserRankLane,ResultPlayerRecognizer.OpponentRankLane})
            foreach(var mask in new[]{false,true})
            foreach(var scale in new[]{2,3,4})
                diagnostics.Add(new{Language=language,Lane=lane,Scale=scale,Mask=mask,Glyph=HudDigitReader.ReadRank(frame,lane),
                    Lines=await numeric.ReadLinesAsync(frame,lane,scale:scale,enhance:false,whiteLetterMask:mask,smooth:true)});
        }
        File.WriteAllText(report,JsonSerializer.Serialize(new{Initial=initial,Samples=samples,Diagnostics=diagnostics},new JsonSerializerOptions{WriteIndented=true}));
        if(screen.ResultPlayers is not {User.Rank:6,Opponent.Rank:6,Opponent.Name:"FixtureOpponentA"} ||
            screen.PostMatchRoundScores is not [{UserScore:25,OpponentScore:37},{UserScore:4,OpponentScore:11}])
            throw new InvalidOperationException("The supplied real result screenshot did not recover both ranks, opponent name and two played round scores.");
        Console.WriteLine("PASS real result screenshot: both rank 6, FixtureOpponentA, 25–37 / 4–11; unused 0–0 third round excluded.");
    }
}
