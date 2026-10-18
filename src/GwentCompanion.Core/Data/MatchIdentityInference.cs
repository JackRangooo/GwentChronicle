using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

public static class MatchIdentityInference
{
    /// <summary>Only directly observed starting cards can identify an otherwise unknown faction.</summary>
    public static string? Faction(MatchPlayer player, IEnumerable<CardDefinition> catalog)
    {
        if (!string.IsNullOrWhiteSpace(player.Faction)) return MatchAnalysis.Factions.Contains(player.Faction) ? player.Faction : null;
        var cards = catalog.DistinctBy(card => card.Id).ToDictionary(card => card.Id, StringComparer.Ordinal);
        var eligible = player.Observations.Where(c => c.Evidence == MatchCardEvidence.Observed &&
            StartingDeckRules.CountsAgainstStartingDeck(c.Origin) && cards.ContainsKey(c.CardId))
            .Select(c => cards[c.CardId]).Where(c => c.Faction != "Neutral" && StartingDeckRules.IsStartingCard(c)).DistinctBy(c => c.Id).ToArray();
        if (eligible.Length < 3) return null;
        // A single opposing/dual-faction card is a reason to abstain. No model
        // hypotheses or generated cards are allowed to manufacture an identity.
        var factions = eligible.Select(c => c.Faction).Distinct(StringComparer.Ordinal).ToArray();
        return factions.Length == 1 && MatchAnalysis.Factions.Contains(factions[0]) &&
            eligible.All(c => c.SecondaryFactions is null || c.SecondaryFactions.Count == 0) ? factions[0] : null;
    }
    public static CompactMatch Apply(CompactMatch match, IEnumerable<CardDefinition> catalog) => match with
    { User = match.User with { Faction = Faction(match.User, catalog) ?? match.User.Faction } };
}
