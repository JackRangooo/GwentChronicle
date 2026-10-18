using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Data;

namespace GwentCompanion.Core.Vision;

public sealed record RuntimeCardValue(string CardId, int Amount, string Unit);
public static class RuntimeCardValueReader
{
    public static RuntimeCardValue? Read(CardDefinition? hovered, string text)
    {
        if (hovered?.Id is not ("203102" or "202360" or "202219" or "203159")) return null;
        var normalized = Regex.Replace(text, @"\s+", " ");
        if (!normalized.Contains(hovered.Name, StringComparison.OrdinalIgnoreCase) &&
            !(CardTextLocalization.ContainsChinese(text) && CardTextLocalization.Names(hovered).Where(CardTextLocalization.ContainsChinese).Any(name =>
              CardTextLocalization.Compact(normalized).Contains(CardTextLocalization.Compact(name), StringComparison.Ordinal)))) return null;
        if (CardTextLocalization.ContainsChinese(text))
        {
            // Translate only the measured clause for the already identified card.
            // All numeric limits and ambiguity rejection stay in the shared parser.
            var chinese = Regex.Replace(text, @"\s+", "");
            var pattern = hovered.Id == "202219"
                ? @"使[1一]个友军单位获得[\[（(]?\s*(\d{1,2})[\]）)]?点增益"
                : hovered.Id == "203159"
                    ? @"之前5个敌方回合中霜造成的伤害量[（(]([0-9、,，;；]+)[）)]"
                    : @"对[1一]个敌军单位造成[\[（(]?\s*(\d{1,3})[\]）)]?点伤害";
            var clauses = Regex.Matches(chinese, pattern);
            if (clauses.Count != 1) return null;
            var clause = clauses[0];
            normalized = hovered.Name + " " + (hovered.Id == "202219" ? "Boost an allied unit by " + clause.Groups[1].Value :
                hovered.Id == "203159" ? "last 5 enemy turns (" + Regex.Replace(clause.Groups[1].Value, "[、，；]", ",") + ")" :
                "Damage an enemy unit by " + clause.Groups[1].Value);
        }
        if (hovered.Id == "202219")
        {
            var count = Regex.Match(normalized, @"Boost an allied unit by\s*[\[\(\{]?\s*(\d{1,2})\s*[\]\)\}]?", RegexOptions.IgnoreCase);
            return count.Success && int.TryParse(count.Groups[1].Value, out var nature) && nature <= 25
                ? new(hovered.Id, nature, "other Nature cards") : null;
        }
        if (hovered.Id == "203159")
        {
            var history = Regex.Match(normalized, @"last 5 enemy turns\s*[\(\[]\s*([\d\s,;]+)[\)\]]", RegexOptions.IgnoreCase);
            if (!history.Success) return null;
            var values = Regex.Matches(history.Groups[1].Value, @"\d{1,2}").Select(match => int.Parse(match.Value)).ToArray();
            return values.Length == 5 && values.All(value => value <= 20)
                ? new(hovered.Id, 1 + values.Sum(), "current power") : null;
        }
        var matches = Regex.Matches(normalized, @"Damage an enemy unit by (\d{1,3})\b", RegexOptions.IgnoreCase);
        return matches.Count == 1 && int.TryParse(matches[0].Groups[1].Value, out var amount) ? new(hovered.Id, amount, "damage") : null;
    }
}
