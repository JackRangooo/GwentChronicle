using GwentCompanion.Core.Vision;

internal sealed class PostMatchLifecycleValidationCase : IContributorValidationCase
{
    public string Id => "post-match-mmr-linger";
    public string Kind => "integration";
    public string Summary => "Keep capture alive through the result-overlay fade so the menu MMR can still be recovered.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool condition, string message) => ContributorValidationContext.Check(condition, message);
        var at = DateTimeOffset.UnixEpoch.AddDays(1);
        var session = "result-fade";
        var board = new GwentVisualObservation(GwentViewKind.Board, false, 0, 0, null,
            MatchHudVisible: true);
        var victory = board with { MatchHudVisible = false, ScreenHeader = "VICTORY",
            IsCardSelectionOverlay = true };
        var fadedBoard = board with { ScreenHeader = null, IsCardSelectionOverlay = false };
        var candidate = new PostMatchMmr(2434, null, true, "Standard Mode fallback", 2441,
            Confirmed: false, ReadCount: 1);
        var menu = board with { MatchHudVisible = false, ScreenHeader = "STANDARD MODE",
            PostMatchExitCue = true, PostMatchMmrCandidate = candidate };

        var lifecycle = new MatchCaptureLifecycle();
        var gate = new PostMatchAutoStopGate();
        lifecycle.Observe(board, at);
        Check(!gate.TryRequest(session, board, true, true, false), "An active match requested automatic stop.");
        var directGate = new PostMatchAutoStopGate();
        var directSession = session + "-direct";
        directGate.TryRequest(directSession, board, true, true, false);
        var directRating = new PostMatchMmr(2434, -7, true, "result panel", Confirmed: true, ReadCount: 2);
        Check(!directGate.TryRequest(directSession, victory with { PostMatchMmr = directRating }, true, true, false),
            "A confirmed result-panel MMR stopped before the main-menu cross-check.");
        Check(directGate.TryRequest(directSession, menu with { PostMatchMmr = directRating, PostMatchMmrCandidate = null },
                true, true, false),
            "The confirmed main-menu MMR did not stop the armed match immediately.");
        lifecycle.Observe(victory, at.AddSeconds(1));
        lifecycle.Observe(victory, at.AddSeconds(1.2));
        Check(!gate.TryRequest(session, victory, true, true, false),
            "A result without a rating stopped before the menu fallback.");

        // Reproduces the latest live session: the HUD detector briefly sees the
        // completed board 0.3 seconds after VICTORY disappears.
        lifecycle.Observe(fadedBoard, at.AddSeconds(1.3));
        Check(!lifecycle.NewGameStarted,
            "The fading completed board was mistaken for the next game before MMR recovery.");
        Check(!gate.TryRequest(session, fadedBoard with { PostMatchCaptureEnded = lifecycle.NewGameStarted },
                true, true, false), "The result-overlay fade stopped capture.");

        lifecycle.Observe(menu, at.AddSeconds(2));
        Check(lifecycle.BestRating == candidate, "The menu fallback rating was not retained.");
        Check(!gate.TryRequest(session, menu, true, true, false),
            "An unconfirmed fallback stopped before it could gather another vote.");

        lifecycle.Observe(board, at.AddSeconds(3));
        Check(!lifecycle.NewGameStarted && lifecycle.BestRating == candidate,
            "A board returning after an overlay was treated as a new game without a real opening.");
        lifecycle.Observe(board with {ScreenHeader="ROUND 1"}, at.AddSeconds(4));
        Check(lifecycle.OpeningPending && !lifecycle.NewGameStarted,"One opening frame split the match.");
        lifecycle.Observe(board with {ScreenHeader="ROUND 1"}, at.AddSeconds(4.4));
        Check(lifecycle.NewGameStarted,"Two real opening frames after a confirmed result did not separate the next game.");
        var boundary = victory with { ScreenHeader = "PROGRESSION", PostMatchMmr = lifecycle.BestRating,
            PostMatchMmrCandidate = null, PostMatchCaptureEnded = true };
        Check(gate.TryRequest(session, boundary, false, true, false),
            "The next game did not close the previous capture with its retained rating.");
        var uninterrupted=new MatchCaptureLifecycle(); uninterrupted.Observe(board,at);
        for(var n=1;n<=3;n++)
        {
            uninterrupted.Observe(menu,at.AddSeconds(n*3));
            uninterrupted.Observe(board,at.AddSeconds(n*3+1));
            Check(!uninterrupted.NewGameStarted && !uninterrupted.OpeningPending,"A popup/menu interrupted an active match.");
        }
        return Task.CompletedTask;
    }
}
