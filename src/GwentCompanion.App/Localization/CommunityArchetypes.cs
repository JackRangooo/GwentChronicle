using System.Reflection;
using System.Text.Json;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App.Localization;

internal sealed record CommunityNameSource(string Url, string Title, string? Date);
internal sealed record CommunityNameEligibility(string[]? RequiredCardIds, string[]? RequiredLeaderIds, int? MinimumTacticCount, bool RequiresNoNeutral = false);
internal sealed record CommunityArchetype(string Id, string Name, string Faction, string[] Aliases,
    string[] KeyCardIds, string[] LeaderIds, string[] LeaderNames, string Description, CommunityNameSource[] Sources,
    CommunityNameEligibility? EligibilityHints);
internal sealed record CommunityNameSuggestion(CommunityArchetype Archetype, int Score, bool Qualified, string Reason)
{
    public string Name => Archetype.Name;
    public string Summary => UiTranslation.Translate(Archetype.Faction) + " · " + Reason;
}
internal static class CommunityArchetypes
{
    private sealed record Catalog(CommunityArchetype[] Archetypes);
    private static readonly Lazy<CommunityArchetype[]> Entries = new(() => {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(assembly.GetManifestResourceNames().Single(n => n.EndsWith("community-archetypes.json")))!;
        return JsonSerializer.Deserialize<Catalog>(stream,new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!.Archetypes;
    });
    public static IReadOnlyList<CommunityArchetype> All => Entries.Value;
    public static CommunityNameSuggestion[] Suggest(string? faction, string? leader, IEnumerable<DeckCard> cards, bool allFactions = false, string query = "")
    {
        var entries = cards.ToArray();
        var definitions = entries.Select(c => c.Card).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        return All.Where(a => allFactions || string.IsNullOrWhiteSpace(faction) || a.Faction == faction)
            .Where(a => string.Join(' ',a.Name,string.Join(' ',a.Aliases),a.Description).Contains(query.Trim(),StringComparison.OrdinalIgnoreCase))
            .Select(a => {
                var matched = a.KeyCardIds.Where(definitions.ContainsKey).ToArray();
                var leaderMatch = a.LeaderNames.Contains(leader,StringComparer.OrdinalIgnoreCase) || a.LeaderIds.Contains(leader);
                var requirements = a.EligibilityHints;
                var qualified = (requirements?.RequiredCardIds?.All(definitions.ContainsKey) ?? true) &&
                    (requirements?.RequiredLeaderIds is not { Length: > 0 } required || a.LeaderNames.Contains(leader,StringComparer.OrdinalIgnoreCase) || required.Contains(leader)) &&
                    (requirements?.RequiresNoNeutral != true || entries.Length > 0 && entries.All(c => c.Card.Faction != "Neutral")) &&
                    (requirements?.MinimumTacticCount is not { } minimum || entries.Where(c => c.Card.Categories.Any(t => t.Equals("Tactic",StringComparison.OrdinalIgnoreCase))).Sum(c => c.Count) >= minimum);
                var reason = matched.Length > 0 ? "关键卡：" + string.Join("、",matched.Select(id => UiTranslation.CardName(id,definitions[id].Name))) : "同阵营名称，关键卡尚未命中";
                if (leaderMatch) reason += " · 领袖相符";
                if (!qualified) reason += " · 关键组件未齐";
                return new CommunityNameSuggestion(a,(qualified ? 100 : 0) + matched.Length * 12 + (leaderMatch ? 3 : 0),qualified,reason);
            }).OrderByDescending(s => s.Score).ThenBy(s => s.Name,StringComparer.CurrentCulture).ToArray();
    }
}
