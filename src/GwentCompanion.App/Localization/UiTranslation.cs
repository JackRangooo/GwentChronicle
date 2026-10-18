using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;

namespace GwentCompanion.App.Localization;

public static class UiTranslation
{
    private static readonly Lazy<TranslationData> Data = new(Load);
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string[]> SearchCache = new(StringComparer.Ordinal);
    private static readonly Regex Parts = new(@"(\r?\n| · | \| |, )", RegexOptions.CultureInvariant);
    private static readonly Regex Lines = new(@"(\r?\n)", RegexOptions.CultureInvariant);
    private static readonly Regex Placeholder = new(@"\{([^{}]+)\}", RegexOptions.CultureInvariant);
    public static bool Enabled { get; set; } = true;
    public static int CardCount => Data.Value.Cards.Count;
    public static int UiCount => Data.Value.Ui.Count;

    public static string CardName(string id, string fallback) =>
        Enabled && Data.Value.Cards.TryGetValue(id, out var name) ? name : fallback;

    private static readonly Lazy<(Regex Pattern, Dictionary<string, string> Names)> TitleNames = new(() =>
    {
        var names = new Dictionary<string, string>(Data.Value.Names, StringComparer.OrdinalIgnoreCase)
        {
            ["Yago"] = "亚戈船长", ["Ludovicus"] = "鲁多维西斯", ["Roderick"] = "罗德烈克",
            ["Foltest"] = "弗尔泰斯特", ["Coen"] = "柯恩", ["Commandos"] = "蓝衣突击队",
            ["GN"] = "金色孽鬼", ["NG"] = "尼弗迦德", ["NR"] = "北方领域", ["SK"] = "史凯利格",
            ["ST"] = "松鼠党", ["SY"] = "辛迪加", ["MO"] = "怪兽",
            ["Shupe"] = "店店", ["Thrive"] = "繁盛", ["Frost"] = "白霜", ["Wild Hunt"] = "狂猎",
            ["Dwarves"] = "矮人", ["Pirates"] = "海盗", ["Symbiosis"] = "共生", ["Assimilate"] = "同化"
        };
        foreach (var (alias, full) in new Dictionary<string, string>
        {
            ["Striga"] = "Adda: Striga", ["Justice"] = "Novigradian Justice", ["Hen Gaidth"] = "Hen Gaidth Sword",
            ["Illusionists"] = "Illusionist", ["Casimir"] = "Casimir Bassi", ["Scoundrel"] = "The Scoundrel",
            ["Simlas"] = "Simlas Finn aep Dabair", ["Oneiro"] = "Oneiromancy", ["Sentinels"] = "Dryad Sentinel",
            ["Invo"] = "Yennefer's Invocation", ["Letho"] = "Letho of Gulet",
            ["Henry"] = "Henry var Attre", ["Roche"] = "Vernon Roche", ["Bran"] = "Bran Tuirseach",
            ["Brouver"] = "Brouver Hoog", ["Munro"] = "Munro Bruys", ["Fran"] = "Francesca Findabair",
            ["Filavandrel"] = "Filavandrel aen Fidhail", ["Muta"] = "Mutagenerator", ["Ale"] = "Ancient Ale"
        }) if (Data.Value.Names.TryGetValue(full, out var chinese)) names.TryAdd(alias, chinese);
        foreach (var (plural, singular) in new Dictionary<string, string>
        {
            ["Soldiers"] = "Soldier", ["Constructs"] = "Construct", ["Crimes"] = "Crime", ["Echoes"] = "Echo",
            ["Thrive"] = "Thrive"
        }) if (Data.Value.Ui.TryGetValue(singular, out var chinese)) names.TryAdd(plural, chinese);
        names.TryAdd("no", "不带"); names.TryAdd("thin", "压缩");
        foreach (var (english, chinese) in names.ToArray())
        {
            var plain = string.Concat(english.Normalize(System.Text.NormalizationForm.FormD).Where(c =>
                System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark));
            if (plain != english) names.TryAdd(plain, chinese);
        }
        var pattern = @"(?<![\p{L}\p{N}])(?:" + string.Join("|", names.Keys.Where(n => n.Length >= 2)
            .OrderByDescending(n => n.Length).Select(Regex.Escape)) + @")(?![\p{L}\p{N}])";
        return (new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), names);
    });

    public static string DeckTitle(string value)
    {
        if (!Enabled) return value;
        // Known card titles and explicit GWENT shorthand are suggestions for
        // display only. The author's exact original title remains available.
        return TitleNames.Value.Pattern.Replace(Translate(value), match => TitleNames.Value.Names[match.Value]);
    }

    public static string Translate(string? value)
    {
        if (string.IsNullOrEmpty(value) || !Enabled) return value ?? string.Empty;
        if (value.Length > 12000) return value;
        if (Cache.TryGetValue(value, out var translated)) return translated;
        translated = TranslateCore(value, 0);
        if (value.Length <= 1500)
        {
            if (Cache.Count > 2048) Cache.Clear();
            Cache.TryAdd(value, translated);
        }
        return translated;
    }

    private static string TranslateCore(string value, int depth)
    {
        var data = Data.Value;
        if (data.Ui.TryGetValue(value, out var translated) || data.Names.TryGetValue(value, out translated)) return translated;
        if (data.Abilities.TryGetValue(NormalizeAbility(value), out translated)) return translated;
        var trimmed = value.Trim();
        if (trimmed != value && (data.Ui.TryGetValue(trimmed, out translated) || data.Names.TryGetValue(trimmed, out translated)))
            return value[..(value.Length - value.TrimStart().Length)] + translated + value[value.TrimEnd().Length..];
        foreach (var template in data.Templates)
        {
            if (value.Contains('\n') && !template.Multiline) continue;
            // A suffix-only numeric template ("{count} analyzed") must not
            // swallow an entire status line and move all its parts around.
            if (template.Prefix.Length == 0 && Parts.IsMatch(value) && !template.HasPartSeparator) continue;
            if (template.Prefix.Length > 0 && !value.StartsWith(template.Prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var match = template.Pattern.Match(value);
            if (!match.Success) continue;
            return Placeholder.Replace(template.Translation, token =>
            {
                var index = Array.IndexOf(template.Parameters, token.Groups[1].Value);
                if (index < 0) return token.Value;
                var parameter = match.Groups["p" + index].Value;
                return depth < 3 ? TranslateCore(parameter, depth + 1) : parameter;
            });
        }
        if (depth < 3 && value.Contains('\n'))
            return string.Concat(Lines.Split(value).Select(part => part is "\n" or "\r\n" ? part : TranslateCore(part, depth + 1)));
        if (depth < 3 && Parts.IsMatch(value))
            return string.Concat(Parts.Split(value).Select(part => Parts.IsMatch(part) ? part : TranslateCore(part, depth + 1)));
        return value;
    }

    internal static string NormalizeAbility(string value) => Regex.Replace(value.Trim(), @"\s+", " ");

    // Search enrichment is applied only by the App's search adapter; the Core's
    // canonical names, deck fingerprints, effect text and OCR references stay English.
    internal static bool MatchesChineseSearchTerm(string normalizedEnglish, string term)
    {
        if (normalizedEnglish.Contains(term, StringComparison.Ordinal)) return true;
        if (SearchCache.Count > 256) SearchCache.Clear();
        var candidates = SearchCache.GetOrAdd(term, query => Data.Value.SearchAliases
            .Where(item => GwentCompanion.Core.Data.DeckSearchCatalog.Normalize(item.Chinese).Contains(query, StringComparison.Ordinal))
            .Select(item => item.English).Distinct(StringComparer.Ordinal).ToArray());
        return candidates.Any(candidate => ContainsPhrase(normalizedEnglish, candidate));
    }

    private static bool ContainsPhrase(string text, string phrase)
    {
        for (var at = text.IndexOf(phrase, StringComparison.Ordinal); at >= 0;
            at = text.IndexOf(phrase, at + 1, StringComparison.Ordinal))
            if ((at == 0 || text[at - 1] == ' ') &&
                (at + phrase.Length == text.Length || text[at + phrase.Length] == ' ')) return true;
        return false;
    }

    public static void ConfigureWindow(Window window)
    {
        if (!Enabled) return;
        if (window.Title.StartsWith("Gwent Vision 0.3", StringComparison.Ordinal))
            window.Title = window.Title.Contains("中文版", StringComparison.Ordinal) ? window.Title : window.Title + " · 中文版";
        else window.Title = Translate(window.Title);
    }

    private static TranslationData Load()
    {
        var ui = ReadDictionary("zh-CN-terms.json");
        foreach (var (key, value) in ReadDictionary("zh-CN-ui.json")) ui[key] = value;
        foreach (var (key, value) in ReadDictionary("zh-CN-extra.json")) ui[key] = value;
        var cards = new Dictionary<string, string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var abilities = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new List<SearchAlias>();
        using var document = JsonDocument.Parse(ReadResource("zh-CN-cards.json"));
        foreach (var entry in document.RootElement.GetProperty("terms").EnumerateObject()) ui[entry.Name] = entry.Value.GetString()!;
        foreach (var entry in document.RootElement.GetProperty("cards").EnumerateObject())
        {
            var english = entry.Value.GetProperty("english_name").GetString()!;
            var chinese = entry.Value.GetProperty("chinese_name").GetString()!;
            cards[entry.Name] = chinese;
            names.TryAdd(english, chinese);
            aliases.Add(new(GwentCompanion.Core.Data.DeckSearchCatalog.Normalize(english), chinese));
        }
        foreach (var entry in document.RootElement.GetProperty("abilities").EnumerateObject())
            abilities[NormalizeAbility(entry.Name)] = entry.Value.GetString()!;
        foreach (var (english, chinese) in ui.Where(item => !Placeholder.IsMatch(item.Key) && item.Key.Length < 35))
            aliases.Add(new(GwentCompanion.Core.Data.DeckSearchCatalog.Normalize(english), chinese));
        var templates = ui.Where(item => Placeholder.IsMatch(item.Key) && Placeholder.Replace(item.Key, "").Trim().Length >= 2)
            .OrderByDescending(item => Placeholder.Replace(item.Key, "").Length)
            .Select(item => CreateTemplate(item.Key, item.Value)).ToArray();
        return new(ui, cards, names, abilities, templates, aliases.Where(item => item.English.Length > 1).ToArray());
    }

    private static Dictionary<string, string> ReadDictionary(string file)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in JsonSerializer.Deserialize<Dictionary<string, string>>(ReadResource(file))!) result[key] = value;
        return result;
    }

    private static string ReadResource(string file)
    {
        var assembly = typeof(UiTranslation).Assembly;
        var name = assembly.GetManifestResourceNames().Single(value => value.EndsWith("Localization." + file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static TranslationTemplate CreateTemplate(string source, string translation)
    {
        var tokens = Placeholder.Matches(source).ToArray();
        var parameters = tokens.Select(token => token.Groups[1].Value).Distinct(StringComparer.Ordinal).ToArray();
        var pattern = "^";
        var position = 0;
        foreach (var token in tokens)
        {
            pattern += Regex.Escape(source[position..token.Index]);
            var index = Array.IndexOf(parameters, token.Groups[1].Value);
            pattern += "(?<p" + index + ">.*?)";
            position = token.Index + token.Length;
        }
        pattern += Regex.Escape(source[position..]) + "$";
        return new(source[..tokens[0].Index], new Regex(pattern,
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.NonBacktracking), translation, parameters, source.Contains('\n'), Parts.IsMatch(source));
    }

    private sealed record TranslationData(Dictionary<string, string> Ui, Dictionary<string, string> Cards,
        Dictionary<string, string> Names, Dictionary<string, string> Abilities, TranslationTemplate[] Templates, SearchAlias[] SearchAliases);
    private sealed record TranslationTemplate(string Prefix, Regex Pattern, string Translation, string[] Parameters, bool Multiline, bool HasPartSeparator);
    private sealed record SearchAlias(string English, string Chinese);
}
