using System.Globalization;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private sealed record ProjectionKey(object Decks, object? Memory, object? Catalog, string State);
    private sealed record ProjectionInput(DeckDefinition[] Decks, ObservedCard[] Evidence, ObservedCard[] MatchEvidence, string? Faction,
        DeckProjectionEdits Edits, ObservedStartingDeckAssessment Rules, int MinimumSize,
        string? Leader, IReadOnlyList<CardDefinition>? Catalog, string? Stratagem,
        IReadOnlyList<LearnedOpponentDeck>? Memory, int? Mmr, string Encounter, IReadOnlyList<SummonCandidateEvidence> SummonEvidence,
        IReadOnlyList<DeckCompositionClue> CompositionClues, IReadOnlyList<OpponentSequenceEvidence> SequenceEvidence, string? ReviewKey);
    private sealed record ProjectionResult(OpponentDeckProjection Projection, OpponentDeckMatchReport Matches, string FilterLabel, string Encounter, string? ReviewKey);
    private LatestWorkQueue<ProjectionKey, ProjectionInput, ProjectionResult>? _projectionQueue;
    private string? _projectionEncounter;

    private void QueueDeckProjection()
    {
        if (_windowClosing) return;
        var evidence = _opponentTracker.DeckBuildingObservations.ToArray();
        var effectiveEvidence = EffectiveOpponentDeckEvidence();
        var rules = _opponentKnowledge.Assess(effectiveEvidence);
        var edits = _opponentEdits.Snapshot();
        var input = new ProjectionInput(_cachedDecks, evidence, effectiveEvidence,
            _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null, edits, rules,
            _opponentKnowledge.MinimumSize(effectiveEvidence), _opponentKnowledge.StartingLeader, _candidateCatalog,
            _opponentKnowledge.StartingStratagemId, _opponentMemory?.Records, MatchMmr(), CurrentEncounterId,_opponentKnowledge.SummonAbsence.Evidence,
            _deckCompositionClues.Where(item => item.Key.Side == PlayerSide.Opponent).Select(item => item.Value).ToArray(),
            _opponentKnowledge.Sequences.Evidence,CurrentReviewKey);
        // Recognition timestamps/board points/unchanged hand counts do not change
        // starting-deck inference. Actual copies, origins, confidence, choices and
        // hard-rule changes do. Catalogue/library/memory replacements invalidate it.
        var state = JsonSerializer.Serialize(new {
            input.Faction, input.Leader, input.Stratagem, input.MinimumSize, input.Mmr, input.Encounter, input.ReviewKey,
            Patch = DeckPatchMetadata.Current(DateTimeOffset.Now).Label,
            input.SummonEvidence,
            input.CompositionClues,
            input.SequenceEvidence,
            Cards = effectiveEvidence.OrderBy(o => o.Card.Id, StringComparer.Ordinal).Select(o => new {
                o.Card.Id, o.ObservedCopies, o.Provenance, o.Confidence }),
            Rules = new[] { rules.Shupe.State, rules.Radeyah.State, rules.GoldenNekker.State,
                rules.Renfri.State, rules.Devotion.State, rules.Musicians?.State },
            Picks = edits.Included.Keys.OrderBy(k => k.CardId, StringComparer.Ordinal).ThenBy(k => k.Copy),
            Exclusions = edits.Excluded.OrderBy(k => k.CardId, StringComparer.Ordinal).ThenBy(k => k.Copy),
            EvidenceCorrections = edits.CorrectedEvidence.OrderBy(k => k.CardId, StringComparer.Ordinal).ThenBy(k => k.Copy)
        });
        if (_projectionEncounter != input.Encounter)
        {
            _projectionEncounter = input.Encounter;
            _lastProjection = null;
            OpponentMatchesPanel.Report = null;
            OpponentDeckCards.Rows = null;
            CandidateCardTray.Rows = null;
            OpponentDeckSummary.Text = "Updating cards from the current match…";
        }
        _projectionQueue ??= new(
            request => {
                using var tracking = _gameplayPriority.EnterRecognition();
                var projection = new OpponentDeckProjector().Build(request.Decks, request.Evidence, request.Faction,
                null, request.Edits, request.Rules, request.MinimumSize, request.Leader, request.Catalog,
                request.Stratagem, request.Memory is null ? null : new OpponentEncounterPrior(request.Memory, request.Mmr, request.Encounter, request.Decks), request.SummonEvidence,
                request.CompositionClues, request.SequenceEvidence);
                // Decoding cached SIFT descriptors and training the candidate matcher
                // can take seconds. Keep it on this worker rather than freezing the UI
                // when a projection is applied.
                if (_visionWorker is not null) _visionPipeline?.SetLikelyOpponentCards(LikelyOpponentReferenceIds(projection, request.Faction,
                    request.Evidence.Length > 0, null, request.Catalog));
                var matches = OpponentDeckMatcher.Match(request.Decks, request.MatchEvidence,
                    request.Faction, request.Leader, DeckPatchMetadata.Current(DateTimeOffset.Now).Label, catalog: request.Catalog);
                return new ProjectionResult(projection, matches, (request.Faction ?? "Unknown faction") + " · " + (request.Leader ?? "Unknown leader"),request.Encounter,request.ReviewKey);
            },
            result => {
                if (_windowClosing) return;
                if (!ProjectionContextCurrent(result.Encounter,result.ReviewKey)) { QueueDeckProjection(); return; }
                _lastProjection = result.Projection;
                OpponentMatchesPanel.DataRoot = FindDataRoot();
                OpponentMatchesPanel.Editor = LiveEvidenceEditor();
                OpponentMatchesPanel.FilterLabel = result.FilterLabel;
                OpponentMatchesPanel.Report = result.Matches;
                QueueMatchCheckpoint(force: _matchCaptureStopped);
                RenderCompletedProjection();
                RenderDeckRuleBadges();
                if (_lastGameStateUpdate is not null) RefreshThreats(_gameState.Current.At ?? DateTimeOffset.Now);
            },
            exception => ShowAnalysisFailure("Opponent cards could not update; recording is independent.", exception));
        if (_projectionQueue.Request(new(input.Decks, input.Memory, input.Catalog, state), input))
            OpponentDeckSummary.Text = _useOpponentModel
                ? (_lastProjection?.Summary.Split(". ")[0] ?? "Deck suggestions") + " · updating…"
                : "Updating observed cards…";
    }
    private bool ProjectionContextCurrent(string encounter,string? reviewKey) => encounter==CurrentEncounterId && reviewKey==CurrentReviewKey;
}
