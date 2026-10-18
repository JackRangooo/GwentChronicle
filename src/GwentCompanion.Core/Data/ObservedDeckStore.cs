using System.Text.Json;
using System.Text.Json.Serialization;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.Core.Data;

public sealed record StoredObservedCard(
    string Id,
    string Name,
    string Faction,
    int Provision,
    double Confidence,
    CardProvenance Provenance = CardProvenance.Unknown,
    bool FactionException = false,
    int ObservedCopies = 1);

public sealed record StoredDeckAssumption(string CardId, int Copy);

public sealed record ObservedDeckRecord(
    string SessionId,
    PlayerSide Side,
    DateTimeOffset RecordedAt,
    string? Faction,
    int CompatibleCachedDecks,
    string? BestCachedDeckId,
    IReadOnlyList<StoredObservedCard> Cards,
    double FactionConfidence = 0,
    int ProvisionLowerBound = 0,
    int RecognitionVersion = 1,
    ConstraintState Devotion = ConstraintState.Unknown,
    string? DevotionEvidence = null,
    string? PinnedDeckId = null,
    string? PinnedDevotionAssumption = null,
    bool TrainingOnly = false,
    IReadOnlyList<StoredDeckAssumption>? ManualPicks = null,
    IReadOnlyList<StoredDeckAssumption>? DismissedSuggestions = null,
    IReadOnlyList<StoredDeckAssumption>? DismissedObservations = null,
    LearnedOpponentEncounter? LearnedEvidence = null,
    string? ReviewMatchKey = null)
{
    public bool IsUnlistedOrVariant => CompatibleCachedDecks == 0;
}

public sealed class ObservedDeckStore
{
    // Legacy observations predate explicit match UUIDs. Never guess through an
    // interval containing multiple captures (including empty/pending captures).
    public static ObservedDeckRecord[] LinkReviews(IEnumerable<ObservedDeckRecord> records,
        IReadOnlyList<MatchAnalysisEntry> entries, string sessionDirectory) => records.Select(record =>
    {
        if (record.ReviewMatchKey is not null) return record;
        try
        {
            // Session IDs are filenames, not paths supplied by the journal.
            if (record.SessionId.Length == 0 || record.SessionId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_')) return record;
            var path = Path.Combine(sessionDirectory, record.SessionId, "session.json");
            if (!File.Exists(path)) return record;
            using var session = JsonDocument.Parse(File.ReadAllText(path));
            if (!session.RootElement.TryGetProperty("Started", out var startValue) || !startValue.TryGetDateTimeOffset(out var start)) return record;
            var end = session.RootElement.TryGetProperty("Ended", out var endValue) && endValue.TryGetDateTimeOffset(out var finish) ? finish : record.RecordedAt;
            var matches = entries.Where(e => e.StartedAtUtc >= start && e.StartedAtUtc <= end).Take(2).ToArray();
            return matches.Length == 1 ? record with { ReviewMatchKey = MatchReviewStore.Key(matches[0].InstallationId, matches[0].MatchId) } : record;
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidOperationException or FormatException or UnauthorizedAccessException) { return record; }
    }).ToArray();

    /// <summary>Starting-list view of a sighting journal. Raw spawned/created sightings remain stored.</summary>
    public static StoredObservedCard[] StartingCards(IEnumerable<StoredObservedCard> observations, IEnumerable<CardDefinition> catalog,
        string? faction = null)
    {
        var definitions = catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        return observations.Where(item => StartingDeckRules.CountsAgainstStartingDeck(item.Provenance) &&
                definitions.TryGetValue(item.Id, out var card) && StartingDeckRules.IsLegalStartingCard(card, faction))
            .GroupBy(item => item.Id).Select(g => g.MaxBy(item => item.ObservedCopies)!)
            .Select(item => item with { Name = definitions[item.Id].Name, Provision = definitions[item.Id].Provision }).ToArray();
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(), new CardSetJsonConverter() },
    };

    public void Save(string rootDirectory, ObservedDeckRecord record)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentNullException.ThrowIfNull(record);
        record = record with { RecordedAt = record.RecordedAt.ToUniversalTime() };
        var sideDirectory = Path.Combine(rootDirectory, record.Side.ToString().ToLowerInvariant());
        Directory.CreateDirectory(sideDirectory);
        var safeSession = string.Concat(record.SessionId.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        if (safeSession.Length == 0)
        {
            throw new ArgumentException("The session ID does not contain a valid filename.", nameof(record));
        }

        File.WriteAllText(
            Path.Combine(sideDirectory, $"{safeSession}.json"),
            JsonSerializer.Serialize(record, JsonOptions));
    }

    public IReadOnlyList<ObservedDeckRecord> Load(string rootDirectory)
    {
        if (!Directory.Exists(rootDirectory))
        {
            return Array.Empty<ObservedDeckRecord>();
        }

        var result = new List<ObservedDeckRecord>();
        foreach (var path in Directory.GetFiles(rootDirectory, "*.json", SearchOption.AllDirectories))
        {
            try
            {
                var record = JsonSerializer.Deserialize<ObservedDeckRecord>(File.ReadAllText(path), JsonOptions);
                if (record is not null)
                {
                    result.Add(record);
                }
            }
            catch (JsonException)
            {
                // A partial/corrupt observation is ignored; later sessions remain usable.
            }
        }

        return result.OrderByDescending(record => record.RecordedAt).ToArray();
    }
}
