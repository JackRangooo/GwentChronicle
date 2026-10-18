using System.Text.RegularExpressions;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using Original = GwentCompanion.Core.Data.DeckSearchCatalog;

namespace GwentCompanion.App.Localization;

/// <summary>UI-only Chinese search; English queries keep the original matching behavior.</summary>
public static class ChineseDeckSearchCatalog
{
    public static IReadOnlyList<SearchableDeck> Build(IEnumerable<DeckIndexEntry> index, IEnumerable<DeckDefinition> cached, DeckLibrary? library = null) =>
        Original.Build(index, cached, library);
    public static string Normalize(string value) => Original.Normalize(value);
    public static string[] Terms(string? query) => Original.Terms(query);
    public static bool Matches(string normalizedText, IReadOnlyList<string> terms)
    {
        if (!terms.Any(term => term.Any(character => character is >= '\u3400' and <= '\u9fff')))
            return Original.Matches(normalizedText, terms);
        return terms.All(term => term.Any(character => character is >= '\u3400' and <= '\u9fff')
            ? UiTranslation.MatchesChineseSearchTerm(normalizedText, term)
            : normalizedText.Contains(term, StringComparison.Ordinal));
    }
    public static bool ContainsCards(DeckDefinition? deck, string? query) =>
        string.IsNullOrWhiteSpace(query) || deck is not null && Split(query).All(phrase => MatchesCard(deck, phrase));
    public static bool ExcludesCards(DeckDefinition? deck, string? query) =>
        string.IsNullOrWhiteSpace(query) || deck is not null && deck.CardCount >= 25 && !Split(query).Any(phrase => MatchesCard(deck, phrase));
    private static string[] Split(string query) => query.Replace('，', ',')
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    private static bool MatchesCard(DeckDefinition deck, string phrase)
    {
        var count = Regex.Match(phrase, @"^\s*(\d+)\s*[x×]\s*(.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        var copies = 1;
        if (count.Success && (!int.TryParse(count.Groups[1].Value, out copies) || copies < 1)) return false;
        var terms = Terms(count.Success ? count.Groups[2].Value : phrase);
        return terms.Length > 0 && deck.Cards.GroupBy(card => card.Card.Id).Any(group =>
            group.Sum(card => card.Count) >= copies && Matches(Normalize(group.First().Card.Name), terms));
    }
}
