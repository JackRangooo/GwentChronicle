using GwentCompanion.Core.Data;
using GwentCompanion.Platform.Windows.Vision;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private BoardPlayerNameConsensus _playerNameConsensus = new();
    private ResultPlayers? _resultPlayerReading;
    private PostMatchRoundScore[]? _resultRoundScores;
    private void ResetPlayerNames()
    {
        _playerNameConsensus = new(); _visionPipeline?.SetNameLanesEnabled(true,true);
        _resultPlayerReading=null;_resultRoundScores=null;
    }
    private void ObserveLocalPlayerNames(CardVisionResult result)
    {
        if (_matchAcquisition?.GameDateUtc is null) return;
        _playerNameConsensus.Observe(result.Screen.PlayerNames,result.SampledAt);
        if(result.Screen.ResultPlayers is { } players)
        {
            ResultPlayer Merge(ResultPlayer? old,ResultPlayer seen)=>new(seen.Name??old?.Name,seen.Rank??old?.Rank);
            _resultPlayerReading=new(Merge(_resultPlayerReading?.User,players.User),Merge(_resultPlayerReading?.Opponent,players.Opponent));
        }
        if(result.Screen.PostMatchRoundScores is { } scores)_resultRoundScores=scores.ToArray();
        _visionPipeline?.SetNameLanesEnabled(_playerNameConsensus.UserName is null,_playerNameConsensus.OpponentName is null);
    }
    private static void SaveLocalPlayerNames(string directory, CompactMatch snapshot, string? userName, string? opponentName)
    {
        if (userName is null && opponentName is null) return;
        new LocalMatchProfileStore(directory).MergeRecognizedNames(snapshot,userName,opponentName);
    }
}
