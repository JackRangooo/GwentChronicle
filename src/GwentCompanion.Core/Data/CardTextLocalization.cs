using System.Text.Json;
using System.Text.RegularExpressions;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>Recognition aliases point to canonical IDs; they never change card models or effect text.</summary>
public static class CardTextLocalization
{
    private static readonly Lazy<Dictionary<string, Entry>> Cards = new(Load);
    public static bool ContainsChinese(string value) => value.Any(c => c is >= '\u3400' and <= '\u9fff');
    public static IEnumerable<string> Names(CardDefinition card)
    {
        yield return card.Name;
        if (Cards.Value.TryGetValue(card.Id, out var entry) &&
            entry.English.Equals(card.Name, StringComparison.OrdinalIgnoreCase)) yield return entry.Chinese;
    }
    public static string? ChineseName(string id) => Cards.Value.GetValueOrDefault(id)?.Chinese;
    public static IReadOnlyList<string> ChineseCategories { get; } = LoadCategories();
    private static readonly Lazy<string[]> CategoryNames = new(() => ChineseCategories.Select(Compact).Where(name => name.Length > 0).Distinct().ToArray());
    public static bool IsChineseCategoryLine(string text)
    {
        if (!ContainsChinese(text)) return false;
        var compact = Compact(text);
        if (compact.Length is 0 or > 60) return false;
        // OCR may insert spaces inside a Chinese category. Bounded word
        // segmentation recognizes complete category lists without a large regex.
        var ends = new bool[compact.Length + 1]; ends[0] = true;
        for (var at = 0; at < compact.Length; at++)
            if (ends[at]) foreach (var category in CategoryNames.Value)
                if (compact.AsSpan(at).StartsWith(category, StringComparison.Ordinal)) ends[at + category.Length] = true;
        return ends[^1];
    }
    public static string Compact(string value) => Regex.Replace(value.ToUpperInvariant(), @"[^\p{L}\p{N}]", "");

    private static string[] LoadCategories()
    {
        var assembly = typeof(CardTextLocalization).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("Localization.zh-CN-categories.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<string[]>(stream)!;
    }

    private static Dictionary<string, Entry> Load()
    {
        using var stream = typeof(CardTextLocalization).Assembly.GetManifestResourceStream(
            "GwentCompanion.Core.Localization.zh-CN-card-names.json")!;
        using var data = JsonDocument.Parse(stream);
        return data.RootElement.EnumerateObject().ToDictionary(row => row.Name,
            row => new Entry(row.Value.GetProperty("english_name").GetString()!, row.Value.GetProperty("chinese_name").GetString()!));
    }
    private sealed record Entry(string English, string Chinese);
}
