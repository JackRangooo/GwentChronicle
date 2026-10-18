using System.IO;
using System.Text.Json;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App.Localization;

internal static class DeckDisplayNames
{
    private static string? _path;
    private static Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> Suggestions = new(StringComparer.Ordinal);
    private static Dictionary<string,(string Id,string Name,int Count,int Position,bool ExactName)> _groups = new(StringComparer.Ordinal);
    public static void ConfigureGroups(DeckLibrary library)
    {
        var records=library.Records.ToDictionary(r=>r.Fingerprint);
        _groups=library.VariationGroups.SelectMany(g=>g.Members.Select((fp,index)=>(Record:records[fp],Group:g,Index:index)))
            .ToDictionary(p=>p.Record.Deck.Id,p=>(p.Group.Id,p.Group.Name,p.Group.Members.Length,p.Index+1,p.Record.CustomName));
    }
    public static string DisplayCandidate(string id,string original)
    {
        if (!_groups.TryGetValue(id,out var group) || group.Count<2 || group.ExactName || _aliases.ContainsKey(id)) return Display(id,original);
        return Display(group.Id,group.Name)+$" · 变种 {group.Position}/{group.Count}";
    }
    public static string GroupSearchLabel(string deckId) => _groups.TryGetValue(deckId,out var group) && group.Count>1 ? Display(group.Id,group.Name) : "";
    public static void Configure(string path, bool reload = false)
    {
        if (_path == path && !reload) return;
        _path = path;
        try { _aliases = File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new() : new(); }
        catch (Exception error) when (error is IOException or JsonException) { _aliases = new(); }
    }
    public static string? Alias(string id, string? previousKey = null) =>
        _aliases.GetValueOrDefault(id) ?? (previousKey is null ? null : _aliases.GetValueOrDefault(previousKey));
    public static string Suggest(string original)
    {
        if (!UiTranslation.Enabled) return original;
        if (Suggestions.TryGetValue(original, out var result)) return result;
        if (Suggestions.Count > 4096) Suggestions.Clear();
        return Suggestions[original] = UiTranslation.DeckTitle(original);
    }
    public static string Display(string id, string original, string? previousKey = null)
    {
        if (!UiTranslation.Enabled) return original;
        if (!_aliases.TryGetValue(id, out var alias) && (previousKey is null || !_aliases.TryGetValue(previousKey, out alias))) return Suggest(original);
        var suffix = System.Text.RegularExpressions.Regex.Match(original, @" · \d+ variations$");
        return alias + (suffix.Success ? UiTranslation.Translate(suffix.Value) : "");
    }
    public static void Set(string id, string alias)
    {
        if (string.IsNullOrWhiteSpace(alias)) _aliases.Remove(id); else _aliases[id] = alias.Trim();
        if (_path is null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, _path, overwrite: true);
    }
}
