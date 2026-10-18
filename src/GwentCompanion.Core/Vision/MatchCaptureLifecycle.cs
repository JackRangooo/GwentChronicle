namespace GwentCompanion.Core.Vision;

/// <summary>Keep menu readings out of a new match and close the previous match before accepting the next board.</summary>
public sealed class MatchCaptureLifecycle
{
    private bool _started, _resultSeen;
    private DateTimeOffset _lastAt;
    private string? _resultCandidate;
    private int _resultVotes, _openingVotes;
    private DateTimeOffset _lastResultAt, _lastOpeningAt;
    public bool WaitingForGame => !_started;
    public bool NewGameStarted { get; private set; }
    public bool OpeningPending => _openingVotes > 0 && !NewGameStarted;
    public bool ResultConfirmed => _resultSeen;
    public PostMatchMmr? BestRating { get; private set; }

    public void Observe(GwentVisualObservation screen, DateTimeOffset at)
    {
        if (!screen.FrameGeometrySupported || at <= _lastAt || NewGameStarted) return;
        _lastAt = at;
        var header = screen.ScreenHeader?.Trim().ToUpperInvariant();
        // Generic progression/menu/pop-up text cannot authenticate the end of a match.
        var result = header is "VICTORY" or "DEFEAT" or "DRAW" or "GAME OVER";
        var menu = header == "STANDARD MODE" || screen.PostMatchExitCue;
        var explicitOpening = header == "ROUND 1" && screen.MatchHudVisible == true ||
            header == "REDRAW" && screen.IsCardSelectionOverlay;
        var game = !result && !menu && (screen.MatchHudVisible == true || explicitOpening);
        // The completed board can reappear briefly while the result overlay fades.
        // It is the same match, so keep reading until a real menu transition or an
        // explicit next-game opening has authenticated the boundary.
        if (result)
        {
            _resultVotes = _resultCandidate == header && at - _lastResultAt <= TimeSpan.FromSeconds(8) ? _resultVotes + 1 : 1;
            _resultCandidate = header; _lastResultAt = at;
            if (_resultVotes >= 2 || screen.PostMatchRoundScores is {Length:>=2 and <=3}) _resultSeen = true;
        }
        else if (game && !_resultSeen) { _resultVotes = 0; _resultCandidate = null; }
        if (_resultSeen && explicitOpening)
        {
            _openingVotes = at - _lastOpeningAt <= TimeSpan.FromSeconds(5) ? _openingVotes + 1 : 1;
            _lastOpeningAt = at;
            if (_openingVotes >= 2) NewGameStarted = true;
            return;
        }
        _openingVotes = 0;
        if (game || result) _started = true;
        if (!_started) return;
        // A menu or temporary overlay during play is a gap, not a completed match.
        if (!_resultSeen) return;
        var reading = screen.PostMatchMmr ?? screen.PostMatchMmrCandidate;
        if (reading is not { IsFactionRating: true } ||
            reading.RatingAfter is < 0 or > 10000 || reading.Change is < -1000 or > 1000 ||
            reading.SeasonPeak is < 0 or > 10000 ||
            reading.RatingAfter is null && reading.Change is null && reading.SeasonPeak is null) return;
        if (BestRating is null || reading.Confirmed && !BestRating.Confirmed ||
            reading.Confirmed == BestRating.Confirmed && (reading.ReadCount > BestRating.ReadCount ||
                reading.ReadCount == BestRating.ReadCount && Completeness(reading) >= Completeness(BestRating)))
            BestRating = reading;
    }

    private static int Completeness(PostMatchMmr reading) =>
        (reading.RatingAfter.HasValue ? 1 : 0) + (reading.Change.HasValue ? 1 : 0) + (reading.SeasonPeak.HasValue ? 1 : 0);

    public void Reset()
    { _started = false; _resultSeen = false; _lastAt = default; NewGameStarted = false; BestRating = null;
        _resultCandidate = null; _resultVotes = _openingVotes = 0; _lastResultAt = _lastOpeningAt = default; }
}
