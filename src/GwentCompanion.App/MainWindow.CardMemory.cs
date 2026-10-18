using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private string? CurrentReviewKey => _matchIdentity is not null && _matchAcquisition is not null
        ? MatchReviewStore.Key(_matchIdentity.Id,_matchAcquisition.MatchId) : null;
    private OpponentEvidenceEditor? LiveEvidenceEditor()
    {
        if (_analysisControlTest || _reviewEvidencePath is not null || _matchIdentity is null || _matchAcquisition is null) return null;
        var matchId = _matchAcquisition.MatchId;
        return new OpponentEvidenceEditor(MatchStorageDirectory, MatchReviewStore.Key(_matchIdentity.Id, matchId),
            _opponentEdits.ApplyEvidenceCorrections(_opponentTracker.DeckBuildingObservations),
            _opponentTracker.HasStableFaction ? _opponentTracker.Faction : null,
            () => { RenderLiveInference(); PersistCurrentMatch(); },
            () => !_windowClosing && _matchAcquisition?.MatchId == matchId)
            .WithCatalog(_candidateCatalog ?? _cachedDecks.SelectMany(d => d.Cards).Select(c => c.Card));
    }

    // Old captures have no explicit UUID link. Link only when a diagnostic session
    // encloses exactly one compact match; ambiguity retains a standalone record key.
    private ObservedDeckRecord[] LinkObservationReviews(ObservedDeckRecord[] records)
    {
        if (_analysisControlTest || _reviewEvidencePath is not null) return records;
        try {
            return ObservedDeckStore.LinkReviews(records, MatchAnalysis.Load(MatchStorageDirectory).Entries, DiagnosticSessionDirectory);
        } catch (Exception error) when (error is IOException or JsonException or InvalidDataException) { return records; }
    }

    private DeckListItem[] ApplyLibraryMemories(DeckListItem[] items)
    {
        var reviews = new MatchReviewStore(MatchStorageDirectory).Load();
        var cards = (_candidateCatalog ?? []).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        return items.Select(item => {
            var counts = reviews.GetValueOrDefault(item.ReviewMatchKey ?? item.DisplayKey)?.OpponentCardCopies;
            if (item.PartialSlots is null || counts is null) return item;
            var slots = item.PartialSlots.Where(s => s.Card is null || !counts.ContainsKey(s.Card.Id)).Concat(counts
                .Where(p => p.Value > 0 && cards.ContainsKey(p.Key) && StartingDeckRules.IsLegalStartingCard(cards[p.Key],item.FilterFaction))
                .SelectMany(p => Enumerable.Range(1,Math.Min(p.Value,cards[p.Key].IsGold ? 1 : 2)).Select(copy =>
                    new ProjectedDeckSlot(0,cards[p.Key],copy,DeckSlotState.Observed,null,null,false,"记忆补录 · 用户确认")))).ToArray();
            return item with { PartialSlots=slots,ObservedNames=slots.Where(s => s.Card is not null).Select(s => s.Card!.Name).ToArray() };
        }).ToArray();
    }
}
