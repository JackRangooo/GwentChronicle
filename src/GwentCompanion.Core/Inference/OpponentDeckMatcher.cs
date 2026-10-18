using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Inference;

public sealed record OpponentDeckEvidence(CardDefinition Card, int Copies, int WeightedProvisions);

public sealed record OpponentDeckMissingCard(CardDefinition Card, int Copies);

public sealed record OpponentDeckMatch(
    DeckDefinition Deck,
    double ScorePercent,
    int MatchedCopies,
    int TotalObservedCopies,
    IReadOnlyList<OpponentDeckMissingCard> MissingObservedCards,
    int WeightedProvisionsMatched,
    int WeightedProvisionsObserved)
{
    /// <summary>Unweighted share of normalized observed starting-card copies present in this deck.</summary>
    public double ObservedCoveragePercent => TotalObservedCopies > 0 ? 100d * MatchedCopies / TotalObservedCopies : 0;
}

public sealed record OpponentDeckMatchReport(
    IReadOnlyList<OpponentDeckEvidence> Evidence,
    int TotalObservedCopies,
    int WeightedProvisionsObserved,
    int CompatibleDistinctDeckCount,
    int DuplicateDecksCollapsed,
    int TopTieCount,
    IReadOnlyList<OpponentDeckMatch> Matches,
    string? NoMatchReason)
{
    public bool HasEvidence => Evidence.Count > 0;
    public bool HasMatches => Matches.Count > 0;
}

/// <summary>
/// Ranks cached, complete deck compositions by how much of their public starting list
/// is supported by observed starting-card copies. Recommendations require at least
/// twelve normalized observed copies and 80% copy coverage. The result is a candidate list,
/// never a claim that a complete deck has been identified.
/// </summary>
public static class OpponentDeckMatcher
{
    private const int MinimumCompleteDeckSize = 25;
    public const int MinimumObservedCopies = 12;
    public const int MinimumObservedCoveragePercent = 80;

    /// <param name="requireRecommendationThresholds">Keep true for recommendations. False is intended only for isolated ranking regression tests.</param>
    public static OpponentDeckMatchReport Match(
        IEnumerable<DeckDefinition> decks,
        IEnumerable<ObservedCard> observations,
        string? faction = null,
        string? startingLeader = null,
        string? currentPatch = null,
        int limit = 2,
        IEnumerable<CardDefinition>? catalog = null,
        bool requireRecommendationThresholds = true)
    {
        ArgumentNullException.ThrowIfNull(decks);
        ArgumentNullException.ThrowIfNull(observations);
        if (limit < 0) throw new ArgumentOutOfRangeException(nameof(limit));

        var catalogCards = catalog?.ToArray() ?? [];
        var leadersById = GwentOneCardCatalog.StartingLeaders(catalogCards)
            .ToDictionary(card => card.Id, StringComparer.OrdinalIgnoreCase);
        string IdentityLeader(string? value, string? deckFaction = null)
        {
            if (string.IsNullOrWhiteSpace(value) || IsUnknown(value.Trim())) return "";
            if (leadersById.TryGetValue(value.Trim(), out var byId) &&
                (string.IsNullOrWhiteSpace(deckFaction) || Same(byId.Faction, deckFaction))) return Normalize(byId.Name);
            var byName = GwentOneCardCatalog.StartingLeaders(catalogCards).FirstOrDefault(card =>
                Same(card.Name, value) && (string.IsNullOrWhiteSpace(deckFaction) || Same(card.Faction, deckFaction)));
            return Normalize(byName?.Name ?? value);
        }

        faction = faction is not null && IsUnknown(faction.Trim()) ? null : faction;
        var normalizedFaction = Normalize(faction);
        var normalizedLeader = IdentityLeader(startingLeader, faction);
        var evidence = NormalizeEvidence(observations, faction);
        var evidenceTotal = evidence.Sum(item => item.Copies);
        var weightedTotal = evidence.Sum(item => item.WeightedProvisions);
        if (evidence.Count == 0 || weightedTotal <= 0)
            return new(evidence, 0, 0, 0, 0, 0, [], normalizedFaction.Length > 0 || normalizedLeader.Length > 0
                ? "No eligible observed starting-card copies are available to compare after applying the supplied faction and starting leader as strict identity filters."
                : "No eligible observed starting-card copies are available to compare.");

        var eligible = new List<(DeckDefinition Deck, Dictionary<string, int> Copies, string Composition)>();
        foreach (var deck in decks)
        {
            if (!IsComplete(deck)) continue;
            if (normalizedFaction.Length > 0 && Normalize(deck.Faction) != normalizedFaction) continue;
            if (normalizedLeader.Length > 0 && IdentityLeader(deck.Leader, deck.Faction) != normalizedLeader) continue;
            var copies = DeckCopies(deck);
            if (copies.Values.Sum() < MinimumCompleteDeckSize) continue;
            eligible.Add((deck, copies, CompositionKey(deck, copies, IdentityLeader(deck.Leader, deck.Faction))));
        }

        // One representative per exact faction + starting leader + card-copy composition.
        var distinct = eligible.GroupBy(item => item.Composition, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(item => IsCurrentPatch(item.Deck, currentPatch))
                .ThenByDescending(item => PatchRecency(item.Deck))
                .ThenBy(item => item.Deck.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Deck.Name, StringComparer.OrdinalIgnoreCase).First())
            .ToArray();

        var identityExplanation = normalizedFaction.Length > 0 || normalizedLeader.Length > 0
            ? " The supplied faction and starting leader were applied as strict filters." : "";
        if (requireRecommendationThresholds && evidenceTotal < MinimumObservedCopies)
            return new(evidence, evidenceTotal, weightedTotal, distinct.Length,
                Math.Max(0, eligible.Count - distinct.Length), 0, [],
                $"No recommendation: at least {MinimumObservedCopies} observed starting-card copies are required; {evidenceTotal} eligible copies are available." + identityExplanation);

        var ranked = distinct.Select(item => Score(item.Deck, item.Copies, evidence, evidenceTotal, weightedTotal))
            .Where(item => item.MatchedCopies > 0)
            // Compare integer copy counts, independently of the provision-weighted ranking score.
            .Where(item => !requireRecommendationThresholds ||
                (long)item.MatchedCopies * 100 >= (long)evidenceTotal * MinimumObservedCoveragePercent)
            .OrderByDescending(item => item.ScorePercent)
            .ThenByDescending(item => item.MatchedCopies)
            .ThenByDescending(item => IsCurrentPatch(item.Deck, currentPatch))
            .ThenByDescending(item => PatchRecency(item.Deck))
            .ThenBy(item => item.Deck.RecencyRank)
            .ThenBy(item => item.Deck.Id, StringComparer.Ordinal)
            .ThenBy(item => item.Deck.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var ties = ranked.Length == 0 ? 0 : ranked.Count(item =>
            item.ScorePercent == ranked[0].ScorePercent && item.MatchedCopies == ranked[0].MatchedCopies);
        var reason = ranked.Length > 0 ? null : requireRecommendationThresholds
            ? $"No recommendation: no compatible complete cached deck contains at least {MinimumObservedCoveragePercent}% of the observed starting-card copies." + identityExplanation
            : normalizedFaction.Length > 0 || normalizedLeader.Length > 0
                ? "No compatible complete cached deck overlaps the observed cards; the supplied faction and starting leader were applied as strict filters."
                : "No complete cached deck overlaps the observed starting cards.";

        return new(evidence, evidenceTotal, weightedTotal, distinct.Length,
            Math.Max(0, eligible.Count - distinct.Length), ties, ranked.Take(Math.Min(limit, 2)).ToArray(), reason);
    }

    /// <summary>Historical-match adapter. Only explicit observed starting-card evidence is used.</summary>
    /// <param name="requireRecommendationThresholds">Keep true for recommendations. False is intended only for isolated ranking regression tests.</param>
    public static OpponentDeckMatchReport Match(
        IEnumerable<DeckDefinition> decks,
        MatchPlayer player,
        IEnumerable<CardDefinition> catalog,
        string? currentPatch = null,
        int limit = 2,
        bool requireRecommendationThresholds = true)
    {
        ArgumentNullException.ThrowIfNull(player);
        ArgumentNullException.ThrowIfNull(catalog);
        var cards = catalog.GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var observed = player.Observations
            .Where(item => item.Evidence is MatchCardEvidence.Observed or MatchCardEvidence.Remembered && StartingDeckRules.CountsAgainstStartingDeck(item.Origin))
            .Where(item => item.Copies > 0 && cards.ContainsKey(item.CardId))
            .Select(item => new ObservedCard(cards[item.CardId], item.Origin, 1, DateTimeOffset.MinValue,
                ObservedCopies: item.Copies));
        return Match(decks, observed, player.Faction, player.Leader, currentPatch, limit, cards.Values, requireRecommendationThresholds);
    }

    private static IReadOnlyList<OpponentDeckEvidence> NormalizeEvidence(IEnumerable<ObservedCard> observations, string? faction)
    {
        // Repeated sighting rows are lower bounds, so repeated rows do not add together.
        return observations
            .Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) &&
                item.ObservedCopies > 0 && StartingDeckRules.IsLegalStartingCard(item.Card, faction))
            .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var card = group.First().Card;
                var observed = group.Max(item => item.ObservedCopies);
                var legalCopies = card.IsGold ? 1 : 2;
                var copies = Math.Min(observed, legalCopies);
                return new OpponentDeckEvidence(card, copies, copies * Math.Max(1, card.Provision));
            })
            .Where(item => item.Copies > 0)
            .OrderBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static OpponentDeckMatch Score(DeckDefinition deck, IReadOnlyDictionary<string, int> deckCopies,
        IReadOnlyList<OpponentDeckEvidence> evidence, int totalCopies, int weightedTotal)
    {
        var missing = new List<OpponentDeckMissingCard>();
        var matchedCopies = 0;
        var weightedMatched = 0;
        foreach (var item in evidence)
        {
            var available = deckCopies.GetValueOrDefault(item.Card.Id);
            var matched = Math.Min(item.Copies, available);
            matchedCopies += matched;
            weightedMatched += matched * Math.Max(1, item.Card.Provision);
            if (matched < item.Copies) missing.Add(new(item.Card, item.Copies - matched));
        }
        return new(deck, 100d * weightedMatched / weightedTotal, matchedCopies, totalCopies,
            missing.ToArray(), weightedMatched, weightedTotal);
    }

    private static bool IsComplete(DeckDefinition deck) =>
        deck.Cards is not null && deck.CardCount >= MinimumCompleteDeckSize &&
        !string.IsNullOrWhiteSpace(deck.Faction) && !IsUnknown(deck.Faction) &&
        !string.IsNullOrWhiteSpace(deck.Leader) && !IsUnknown(deck.Leader) &&
        deck.Cards.All(item => item.Count > 0);

    private static Dictionary<string, int> DeckCopies(DeckDefinition deck) => deck.Cards
        .Where(item => StartingDeckRules.IsLegalStartingCard(item.Card, deck.Faction))
        .GroupBy(item => item.Card.Id, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key,
            group => Math.Min(group.Sum(item => item.Count), group.First().Card.IsGold ? 1 : 2),
            StringComparer.OrdinalIgnoreCase);

    private static string CompositionKey(DeckDefinition deck, IReadOnlyDictionary<string, int> copies, string normalizedLeader) =>
        Normalize(deck.Faction) + "|" + normalizedLeader + "|" +
        string.Join(';', copies.OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase)
            .Select(item => item.Key.ToLowerInvariant() + ":" + item.Value));

    private static bool IsCurrentPatch(DeckDefinition deck, string? currentPatch) =>
        !string.IsNullOrWhiteSpace(currentPatch) &&
        (deck.Patches?.Any(patch => Same(patch.Label, currentPatch)) == true ||
         string.Join(' ', deck.Occurrences?.Select(item => item.Patch) ?? []).Contains(currentPatch, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset PatchRecency(DeckDefinition deck) =>
        deck.Patches?.Select(patch => ParsePatchDate(patch.Label)).Where(date => date.HasValue).Max() ??
        deck.SourceUpdatedAt ?? deck.CachedAt ?? deck.LastEdited ?? DateTimeOffset.MinValue;

    private static DateTimeOffset? ParsePatchDate(string label)
    {
        var digits = new string(label.Where(char.IsDigit).ToArray());
        return digits.Length >= 6 && int.TryParse(digits[..6], out var value) &&
               DateTimeOffset.TryParseExact(value.ToString("D6"), "yyMMdd", null,
                   System.Globalization.DateTimeStyles.AssumeUniversal, out var date) ? date : null;
    }

    private static bool IsUnknown(string value) => value.Equals("Unknown faction", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("Unknown leader", StringComparison.OrdinalIgnoreCase) || value.Equals("Unknown", StringComparison.OrdinalIgnoreCase);

    private static bool Same(string? left, string? right) => Normalize(left) == Normalize(right);
    private static string Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? "" : DeckSearchCatalog.Normalize(value.Trim());
}
