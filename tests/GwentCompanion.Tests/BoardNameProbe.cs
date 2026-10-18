using System.Text.Json;
using System.IO;
using System.Windows.Media.Imaging;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

internal static class BoardNameProbe
{
    internal static async Task Run(string plan,string report)
    {
        var rows=new List<object>();
        foreach(var language in new[]{"en-US","zh-Hans-CN"})
        {
            using var reader=new ScreenStateRecognizer(language);
            foreach(var path in JsonSerializer.Deserialize<string[]>(File.ReadAllText(plan))!)
            {
                using var stream=File.OpenRead(path);var decoder=BitmapDecoder.Create(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
                var frame=BitmapFrameAdapter.ToPixelFrame(decoder.Frames[0]);
                foreach(var mask in new[]{false,true})
                foreach(var scale in new[]{2,3,4})
                {
                    var user=await reader.ReadLinesAsync(frame,BoardPlayerNameRecognizer.UserLane,scale:scale,enhance:false,whiteLetterMask:mask,smooth:true);
                    var opponent=await reader.ReadLinesAsync(frame,BoardPlayerNameRecognizer.OpponentLane,scale:scale,enhance:false,whiteLetterMask:mask,smooth:true);
                    rows.Add(new{language,path,mask,scale,User=user.Select(l=>l.Text),Opponent=opponent.Select(l=>l.Text)});
                }
            }
        }
        File.WriteAllText(report,JsonSerializer.Serialize(rows,new JsonSerializerOptions{WriteIndented=true}));
    }
}
