using System.Text.Json;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.Core.Data;

/// <summary>User corrections and reversible deletion live beside immutable detector records.</summary>
public sealed record MatchReview(string? Result = null, string? UserFaction = null, string? UserLeader = null,
    string? OpponentFaction = null, string? OpponentLeader = null, MatchPlayer? UserReference = null,
    string? UserDeckName = null, bool? IncludeInStatistics = null, bool Deleted = false,
    DateTimeOffset UpdatedAtUtc = default, string? Note = null,
    IReadOnlyDictionary<string, int>? OpponentCardCopies = null,
    IReadOnlyDictionary<string, int>? UserCardCopies = null, string? CaptureNote = null,
    bool Abnormal = false, string? AbnormalReason = null);

public sealed class MatchReviewStore(string directory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private string PathName => Path.Combine(Path.GetFullPath(directory), "match-reviews.json");
    public static string Key(Guid installation, Guid match) => installation.ToString("N") + ":" + match.ToString("N");
    public static string Key(CompactMatch match) => Key(match.InstallationId, match.MatchId);
    public static MatchPlayer Reference(DeckDefinition deck) => new(deck.Faction, deck.Leader, deck.Stratagem?.Id, [],
        deck.Cards.Select(c => new MatchCard(c.Card.Id, c.Count, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, byte.MaxValue)).ToArray(), []);
    public static bool IsEmptyCapture(CompactMatch match) => MatchAnalysisEntry.ParseOutcome(match.Result) == MatchOutcome.Unknown &&
        match.MmrAfter is null && match.MmrChange is null && match.MmrPeak is null && match.Rank is null &&
        match.Actions.Length == 0 && !match.Rounds.Any(r => r.UserScore.HasValue || r.OpponentScore.HasValue) &&
        new[] { match.User, match.Opponent }.All(p => p.Observations.Length == 0 && p.Reference.Length == 0 && p.Hypothesis.Length == 0);
    public Dictionary<string, MatchReview> Load()
    {
        if (!File.Exists(PathName)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, MatchReview>>(File.ReadAllText(PathName), Json) ?? throw new InvalidDataException("Match corrections are empty."); }
        catch (JsonException error) { throw new InvalidDataException("Could not read match corrections. The original records are preserved.", error); }
    }
    public MatchReview? Get(CompactMatch match) => Load().GetValueOrDefault(Key(match));
    public void SetOpponentCardCopies(string recordKey, string cardId, int copies)
        => SetCardCopies(recordKey,cardId,copies,false);
    public void SetUserCardCopies(string recordKey, string cardId, int copies)
        => SetCardCopies(recordKey,cardId,copies,true);
    public void SetCardCopiesBatch(string recordKey,IReadOnlyDictionary<string,int> values,bool user,bool replace=false)
    {
        if(string.IsNullOrWhiteSpace(recordKey) || values.Any(p=>string.IsNullOrWhiteSpace(p.Key) || p.Value is <0 or >2))throw new ArgumentException("Invalid remembered card counts.");
        Update(entries=>{
            var previous=entries.GetValueOrDefault(recordKey)??new();
            var counts=replace?new Dictionary<string,int>():((user?previous.UserCardCopies:previous.OpponentCardCopies)?.ToDictionary(p=>p.Key,p=>p.Value)??new());
            foreach(var value in values)counts[value.Key]=value.Value;
            entries[recordKey]=user?previous with{UserCardCopies=counts,UpdatedAtUtc=DateTimeOffset.UtcNow}:previous with{OpponentCardCopies=counts,UpdatedAtUtc=DateTimeOffset.UtcNow};
        });
    }
    private void SetCardCopies(string recordKey, string cardId, int copies,bool user)
    {
        if (string.IsNullOrWhiteSpace(recordKey) || string.IsNullOrWhiteSpace(cardId) || copies is < 0 or > 2)
            throw new ArgumentException("Invalid remembered card count.");
        Update(entries => {
            var previous = entries.GetValueOrDefault(recordKey) ?? new();
            var counts = (user ? previous.UserCardCopies : previous.OpponentCardCopies)?.ToDictionary(p => p.Key, p => p.Value) ?? new();
            counts[cardId] = copies;
            entries[recordKey] = user ? previous with { UserCardCopies=counts,UpdatedAtUtc=DateTimeOffset.UtcNow }
                : previous with { OpponentCardCopies = counts, UpdatedAtUtc = DateTimeOffset.UtcNow };
        });
    }
    public static MatchPlayer ApplyOpponentCards(MatchPlayer player, IReadOnlyDictionary<string, int>? counts)
    {
        if (counts is null || counts.Count == 0) return player;
        return player with {
            Observations = player.Observations.Where(c => !counts.ContainsKey(c.CardId)).Concat(counts
                .Where(p => p.Value is > 0 and <= 2).Select(p => new MatchCard(p.Key, p.Value,
                    MatchCardEvidence.Remembered, CardProvenance.ConfirmedStartingDeck, byte.MaxValue))).ToArray(),
            Hypothesis = player.Hypothesis.Where(c => !counts.ContainsKey(c.CardId)).ToArray()
        };
    }
    public void Set(CompactMatch source, MatchReview review)
    {
        Validate(source, review);
        Update(entries => entries[Key(source)] = review with { Deleted = entries.GetValueOrDefault(Key(source))?.Deleted ?? review.Deleted,
            IncludeInStatistics = review.Abnormal || ManualMatchStore.IsAbnormal(source) ? false : review.IncludeInStatistics,
            OpponentCardCopies = entries.GetValueOrDefault(Key(source))?.OpponentCardCopies ?? review.OpponentCardCopies,
            UserCardCopies = entries.GetValueOrDefault(Key(source))?.UserCardCopies ?? review.UserCardCopies,
            CaptureNote = review.CaptureNote ?? entries.GetValueOrDefault(Key(source))?.CaptureNote,
            UpdatedAtUtc = DateTimeOffset.UtcNow });
    }
    public void Delete(CompactMatch source) => Delete([source]);
    public void Delete(IEnumerable<CompactMatch> sources) => Update(entries =>
    {
        foreach (var source in sources) entries[Key(source)] = (entries.GetValueOrDefault(Key(source)) ?? new()) with
            { Deleted = true, UpdatedAtUtc = DateTimeOffset.UtcNow };
    });
    public void Restore(CompactMatch source) => Update(entries => entries[Key(source)] = (entries.GetValueOrDefault(Key(source)) ?? new()) with
        { Deleted = false, UpdatedAtUtc = DateTimeOffset.UtcNow });
    public void Reset(CompactMatch source) => Update(entries =>
    {
        var previous=entries.GetValueOrDefault(Key(source));
        if (previous is not null && (previous.Deleted || previous.OpponentCardCopies?.Count>0 || previous.UserCardCopies?.Count>0 || previous.CaptureNote is not null))
            entries[Key(source)] = new(Deleted:previous.Deleted,UpdatedAtUtc:DateTimeOffset.UtcNow,
                OpponentCardCopies:previous.OpponentCardCopies,UserCardCopies:previous.UserCardCopies,CaptureNote:previous.CaptureNote);
        else entries.Remove(Key(source));
    });
    private void Update(Action<Dictionary<string, MatchReview>> mutate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        // Refuse concurrent writers rather than overwrite another window's edits.
        using var gate = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = Load();
        mutate(entries);
        var temporary = PathName + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, entries, Json); stream.Flush(flushToDisk: true); }
            if (File.Exists(PathName)) File.Copy(PathName, PathName + ".bak", overwrite: true);
            File.Move(temporary, PathName, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void Validate(CompactMatch source, MatchReview review)
    {
        if (review.Result is not (null or "VICTORY" or "DEFEAT" or "DRAW" or "UNKNOWN")) throw new ArgumentException("Choose a valid match result.");
        foreach (var faction in new[] { review.UserFaction, review.OpponentFaction })
            if (faction is not null && faction != "Unknown faction" && !MatchAnalysis.Factions.Contains(faction, StringComparer.Ordinal))
                throw new ArgumentException("Choose a valid faction.");
        if (review.UserReference is { } reference && (reference.Reference.Sum(c => c.Copies) < 25 ||
            !MatchAnalysis.Factions.Contains(reference.Faction, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(reference.Leader)))
            throw new ArgumentException("Select a complete player deck from the library.");
        if (review.UserReference is { } selected && ((review.UserFaction is not null && review.UserFaction != selected.Faction) ||
            (review.UserLeader is not null && review.UserLeader != selected.Leader)))
            throw new ArgumentException("The selected player deck must match its faction and leader.");
        if (!review.Abnormal && review.IncludeInStatistics == true && MatchAnalysisEntry.ParseOutcome(Apply(source, review).Result) == MatchOutcome.Unknown)
            throw new ArgumentException("Choose a known result before including this record in statistics.");
    }
    public static CompactMatch Apply(CompactMatch source, MatchReview? review)
    {
        if (review is null) return source;
        var user = ApplyOpponentCards(source.User,review.UserCardCopies);
        if (review.UserReference is { } reference)
            user = user with { Faction = reference.Faction, Leader = reference.Leader, Stratagem = reference.Stratagem, Reference = reference.Reference.ToArray() };
        else if ((review.UserFaction is not null && review.UserFaction != user.Faction) ||
                 (review.UserLeader is not null && review.UserLeader != user.Leader)) user = user with { Reference = [] };
        user = user with { Faction = review.UserFaction ?? user.Faction, Leader = review.UserLeader ?? user.Leader };
        var opponent = ApplyOpponentCards(source.Opponent, review.OpponentCardCopies) with { Faction = review.OpponentFaction ?? source.Opponent.Faction, Leader = review.OpponentLeader ?? source.Opponent.Leader };
        return source with { User = user, Opponent = opponent, Result = review.Result is "UNKNOWN" ? null : review.Result ?? source.Result };
    }
}
