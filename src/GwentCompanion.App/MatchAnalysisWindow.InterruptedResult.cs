using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> ProbeProvidedResultAsync(string image,string report)
    {
        try
        {
            var reading=await ReadProvidedResultAsync(image);
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=reading.PostMatchRoundScores is {Length:>=2},reading.ScreenHeader,
                Players=reading.ResultPlayers,Scores=reading.PostMatchRoundScores},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
    }

    private static void ApplyInterruptedResult(string directory,CompactMatch source,MatchRound[] rounds,string userName,string opponentName,int userRank,int opponentRank)
    {
        var reviews=new MatchReviewStore(directory);var previous=reviews.Get(source)??new();
        var profiles=new LocalMatchProfileStore(directory);var old=profiles.Get(source)??new();
        LocalPlayerProfile Confirm(LocalPlayerProfile? player,string name,int rank)=>(player??new()) with
            {Name=name,Rank=rank,Source="UserConfirmed",NameSource="Manual",RankSource="UserConfirmed"};
        var profile=old with{User=Confirm(old.User,userName,userRank),Opponent=Confirm(old.Opponent,opponentName,opponentRank),
            ResultRoundScores=rounds,UpdatedAtUtc=DateTimeOffset.UtcNow};
        LocalMatchProfileStore.Validate(profile);
        profiles.Set(source,profile);
        reviews.Set(source,previous with{Result="DEFEAT",IncludeInStatistics=true,
            CaptureNote="采集于首局中断；结算由用户截图补录，卡牌实录不完整。"});
    }

}
