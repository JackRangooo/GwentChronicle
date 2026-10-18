using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Data;

/// <summary>Members are exact composition fingerprints, never aliases or positional variation numbers.</summary>
public sealed record DeckVariationGroup(string Id, string Name, string[] Members, bool CustomName = false);
public sealed record DeckVariationPolicy(string Metric, double Threshold, IReadOnlyDictionary<string, int> Prices)
{
    public const string CurrentMetric = "shared-copy-provisions / max-listed-provisions; construction-gated; complete-link-v1";
    public const string CoreMetric = "gold-provision-overlap>=threshold OR gold-replacements<=3; shared-gold>=3; construction-gated; complete-link-v2";
}

public sealed partial class DeckLibrary
{
    private readonly List<DeckVariationGroup> _variationGroups = [];
    private bool _variationGroupsDirty = true;
    public IReadOnlyList<DeckVariationGroup> VariationGroups => _variationGroups;
    public DeckVariationPolicy? VariationPolicy { get; private set; }
    public bool UpgradeCoreVariationGroups(IEnumerable<CardDefinition>? catalog=null)
    {
        EnsureVariationGroups(catalog);
        if (VariationPolicy!.Metric==DeckVariationPolicy.CoreMetric) return false;
        var oldGroups=_variationGroups.ToArray();
        var metric=new DeckVariationSimilarity(Decks,catalog);
        var records=_records.ToDictionary(r=>r.Fingerprint);
        _variationGroups.Clear();
        VariationPolicy=new(DeckVariationPolicy.CoreMetric,.85,metric.Prices);
        // Gold-focused rules may separate an old bronze-dominated group. Preserve
        // its title on every resulting part; IDs retain the original first part.
        foreach(var old in oldGroups.OrderBy(g=>g.Id,StringComparer.Ordinal)) {
            var parts=new List<DeckVariationGroup>();
            foreach(var fp in old.Members) {
                var part=parts.FirstOrDefault(g=>g.Members.All(other=>metric.IsCoreVariation(records[fp].Deck,records[other].Deck)));
                if(part is null) parts.Add(new(parts.Count==0 ? old.Id : "core-"+fp,old.Name,[fp],old.CustomName));
                else parts[parts.IndexOf(part)]=part with { Members=part.Members.Append(fp).ToArray() };
            }
            foreach(var part in parts) {
                var target=_variationGroups.Where(g=>!(g.CustomName && part.CustomName && g.Name!=part.Name))
                    .Where(g=>g.Members.All(a=>part.Members.All(b=>metric.IsCoreVariation(records[a].Deck,records[b].Deck))))
                    .OrderByDescending(g=>g.Members.Min(a=>part.Members.Min(b=>metric.CompareCore(records[a].Deck,records[b].Deck).Gold.Ratio)))
                    .ThenBy(g=>g.Id,StringComparer.Ordinal).FirstOrDefault();
                if(target is null) _variationGroups.Add(part);
                else {
                    var preferred=part.CustomName && !target.CustomName ? part : target;
                    _variationGroups[_variationGroups.IndexOf(target)]=preferred with { Members=target.Members.Concat(part.Members).ToArray() };
                }
            }
        }
        _variationGroupsDirty=false;
        return true;
    }

    /// <summary>Merge whole stable groups only when every cross-pair agrees. Retain exact versions and custom names.</summary>
    public bool ConsolidateVariationGroups(double threshold, IEnumerable<CardDefinition>? catalog = null)
    {
        if (threshold is < .8 or > 1) throw new ArgumentOutOfRangeException(nameof(threshold));
        EnsureVariationGroups(catalog);
        if (VariationPolicy!.Metric==DeckVariationPolicy.CoreMetric || VariationPolicy.Threshold <= threshold) return false;
        var metric = new DeckVariationSimilarity(Decks,catalog);
        var records = _records.ToDictionary(r => r.Fingerprint);
        var consolidated = new List<DeckVariationGroup>();
        foreach (var group in _variationGroups.OrderBy(g => g.Id,StringComparer.Ordinal))
        {
            var match = consolidated.Where(other => !(other.CustomName && group.CustomName && other.Name != group.Name))
                .Where(other => other.Members.All(a => group.Members.All(b =>
                    metric.IsVariation(records[a].Deck,records[b].Deck,threshold))))
                .OrderByDescending(other => other.Members.Min(a => group.Members.Min(b => metric.Compare(records[a].Deck,records[b].Deck).Ratio)))
                .ThenBy(other => other.Id,StringComparer.Ordinal).FirstOrDefault();
            if (match is null) consolidated.Add(group);
            else {
                var preferred = group.CustomName && !match.CustomName ? group : match;
                consolidated[consolidated.IndexOf(match)] = preferred with { Members = match.Members.Concat(group.Members).ToArray() };
            }
        }
        _variationGroups.Clear(); _variationGroups.AddRange(consolidated);
        VariationPolicy = new(DeckVariationPolicy.CurrentMetric,threshold,metric.Prices);
        return true;
    }
    public void RenameVariationGroup(string groupId, string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 160) throw new ArgumentException("Deck names can contain 1–160 characters.");
        EnsureVariationGroups();
        var index = _variationGroups.FindIndex(g => g.Id == groupId);
        if (index < 0) throw new ArgumentException("Unknown deck group.");
        _variationGroups[index] = _variationGroups[index] with { Name = name, CustomName = true };
    }

    /// <summary>Existing memberships stay stable. A new variant must fit every member, preventing similarity chains.</summary>
    public void EnsureVariationGroups(IEnumerable<CardDefinition>? catalog = null)
    {
        if (!_variationGroupsDirty && VariationPolicy is not null) return;
        if (VariationPolicy is null)
        {
            var metric = new DeckVariationSimilarity(Decks, catalog);
            VariationPolicy = new(_useLegacyVariationPolicy ? DeckVariationPolicy.CurrentMetric : DeckVariationPolicy.CoreMetric,
                _useLegacyVariationPolicy ? DeckVariationSimilarity.Threshold : .85, metric.Prices);
        }
        else
        {
            var prices = new Dictionary<string, int>(VariationPolicy.Prices);
            foreach (var card in _records.SelectMany(r => r.Deck.Cards).Select(c => c.Card)) prices.TryAdd(card.Id, Math.Max(1, card.Provision));
            VariationPolicy = VariationPolicy with { Prices = prices };
        }
        var similarity = new DeckVariationSimilarity(VariationPolicy.Prices);
        var records = _records.ToDictionary(r => r.Fingerprint);
        var assigned = _variationGroups.SelectMany(g => g.Members).ToHashSet(StringComparer.Ordinal);
        foreach (var record in _records.OrderBy(r => r.Fingerprint, StringComparer.Ordinal))
        {
            if (assigned.Contains(record.Fingerprint)) continue;
            var match = _variationGroups.Where(g => g.Members.All(fp => similarity.MatchesPolicy(record.Deck, records[fp].Deck,VariationPolicy.Metric, VariationPolicy.Threshold)))
                .OrderByDescending(g => g.Members.Min(fp => similarity.Compare(record.Deck, records[fp].Deck).Ratio))
                .ThenBy(g => g.Id, StringComparer.Ordinal).FirstOrDefault();
            if (match is null) _variationGroups.Add(new("family-" + record.Fingerprint, record.Deck.Name, [record.Fingerprint]));
            else _variationGroups[_variationGroups.IndexOf(match)] = match with { Members = match.Members.Append(record.Fingerprint).ToArray() };
        }
        _variationGroupsDirty = false;
    }

    public DeckVariationGroup? VariationGroupFor(string deckId)
    {
        EnsureVariationGroups(); var record = Find(deckId);
        return record is null ? null : _variationGroups.FirstOrDefault(g => g.Members.Contains(record.Fingerprint, StringComparer.Ordinal));
    }
    public LibraryDeck[] GroupVariants(DeckVariationGroup group) => group.Members.Select(fp => _records.First(r => r.Fingerprint == fp)).ToArray();
    public string VariationLabel(string deckId)
    {
        var group = VariationGroupFor(deckId); var record = Find(deckId);
        if (group is null || record is null) return "";
        var position = Array.IndexOf(group.Members, record.Fingerprint) + 1;
        return $"{group.Name} · variation {position}/{group.Members.Length} · {record.Fingerprint[..8]}";
    }
    private void ValidateVariationGroups()
    {
        var members = _variationGroups.SelectMany(g => g.Members).ToArray();
        var fingerprints = _records.Select(r => r.Fingerprint).ToHashSet(StringComparer.Ordinal);
        if (_variationGroups.Select(g => g.Id).Distinct(StringComparer.Ordinal).Count() != _variationGroups.Count ||
            members.Distinct(StringComparer.Ordinal).Count() != members.Length || members.Any(fp => !fingerprints.Contains(fp)) ||
            _variationGroups.Any(g => string.IsNullOrWhiteSpace(g.Id) || string.IsNullOrWhiteSpace(g.Name) || g.Members.Length == 0))
            throw new InvalidDataException("Invalid or overlapping variation group membership.");
        if (VariationPolicy is not null && (VariationPolicy.Metric is not (DeckVariationPolicy.CurrentMetric or DeckVariationPolicy.CoreMetric) ||
            VariationPolicy.Threshold is < .8 or > 1 || !double.IsFinite(VariationPolicy.Threshold) || VariationPolicy.Prices is null || VariationPolicy.Prices.Any(p => p.Value is < 1 or > 100)))
            throw new InvalidDataException("Unsupported variation policy or provision weights.");
    }
}
