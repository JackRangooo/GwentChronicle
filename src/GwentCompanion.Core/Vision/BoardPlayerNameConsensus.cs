namespace GwentCompanion.Core.Vision;

/// <summary>Names require three agreeing, separately captured board samples. They remain editable OCR, not verified identity.</summary>
public sealed class BoardPlayerNameConsensus
{
    private string? _userCandidate, _opponentCandidate;
    private int _userVotes, _opponentVotes;
    private DateTimeOffset _last;
    public string? UserName { get; private set; }
    public string? OpponentName { get; private set; }
    public void Observe(BoardPlayerNames? names, DateTimeOffset at)
    {
        if (names is null || at <= _last || at - _last < TimeSpan.FromMilliseconds(500)) return;
        _last = at;
        static string? Vote(string? name,ref string? candidate,ref int votes,string? confirmed)
        {
            if (confirmed is not null) return confirmed;
            if (string.IsNullOrWhiteSpace(name)) { candidate = null; votes = 0; return null; }
            if (name == candidate) votes++; else { candidate = name; votes = 1; }
            return votes >= 3 ? name : null;
        }
        UserName = Vote(names.User,ref _userCandidate,ref _userVotes,UserName);
        var opponent=names.Opponent;
        if(opponent is not null && opponent==UserName)opponent=null;
        if(OpponentName is not null && OpponentName==UserName){OpponentName=null;_opponentCandidate=null;_opponentVotes=0;}
        OpponentName = Vote(opponent,ref _opponentCandidate,ref _opponentVotes,OpponentName);
    }
}
