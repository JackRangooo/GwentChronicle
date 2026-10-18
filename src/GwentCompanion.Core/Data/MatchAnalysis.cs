namespace GwentCompanion.Core.Data;

public enum MatchOutcome { Unknown, Win, Loss, Draw }

/// <summary>Small analysis index; full decks and action arrays are loaded only when a match is expanded.</summary>
public sealed record MatchAnalysisEntry(string Path, Guid InstallationId, Guid MatchId, DateOnly Date,
    string Patch, bool PatchInferred, long Revision, bool CaptureStopped, MatchOutcome Outcome,
    string UserFaction, string UserLeader, string OpponentFaction, string OpponentLeader,
    int? FactionMmr, int? MmrChange, int ObservedOpponentIdentities, int HypothesizedOpponentCopies,
    int PlayPreviews, bool SequenceTruncated, int? Rank = null, int? UnqualifiedMmr = null, DateTimeOffset? StartedAtUtc = null,
    bool MmrUnconfirmed = false, bool? IncludeInStatistics = null, bool Deleted = false, bool ManuallyEdited = false,
    string? UserDeckName = null, bool UserFactionInferred = false, bool EmptyCapture = false, string? UserDeckKey = null,
    LocalMatchProfile? LocalProfile = null, MatchRound[]? RoundScores = null, string? CaptureNote = null,
    bool Abnormal = false, bool ManualRecord = false, string? AbnormalReason = null, bool ExactTimeKnown = true)
{
    public static MatchAnalysisEntry From(string path, CompactMatch match) => new(path, match.InstallationId,
        match.MatchId, match.GameDateUtc, Clean(match.Patch, "Unknown patch"), match.PatchInferred,
        match.Revision, match.CaptureStopped, ParseOutcome(match.Result), Clean(match.User.Faction),
        Clean(match.User.Leader, "Unknown leader"), Clean(match.Opponent.Faction),
        Clean(match.Opponent.Leader, "Unknown leader"), match.FactionMmr ? match.MmrAfter : null,
        match.FactionMmr ? match.MmrChange : null,
        match.Opponent.Observations.Select(c => c.CardId).Distinct(StringComparer.Ordinal).Count(),
        match.Opponent.Hypothesis.Sum(c => c.Copies), match.Actions.Count(a => a.Kind == "PlayPreview"), match.SequenceTruncated,
        match.Rank, match.FactionMmr ? null : match.MmrAfter, match.StartedAtUtc, match.MmrUnconfirmed,
        EmptyCapture: MatchReviewStore.IsEmptyCapture(match), UserDeckKey: MatchAnalysis.DeckKey(match.User), RoundScores: match.Rounds,
        Abnormal: ManualMatchStore.IsAbnormal(match), ManualRecord: ManualMatchStore.IsManual(match));

    public string RatingLabel => FactionMmr is { } mmr ? $"{mmr} fMMR" + (MmrUnconfirmed ? " (unconfirmed)" : "") : Rank is { } rank ? $"Rank {rank}"
        : UnqualifiedMmr is { } other ? $"{other} MMR*" : "Not recorded";
    public bool IsCompleted => !Deleted && !Abnormal && Outcome != MatchOutcome.Unknown && IncludeInStatistics != false;
    public bool CountsForLadder => !Deleted && Outcome != MatchOutcome.Unknown &&
        (LocalProfile?.CountForLadder ?? (Abnormal || IncludeInStatistics != false));
    public int? EffectiveRank => LocalProfile?.User?.Rank ?? Rank;

    public static string Clean(string? value, string fallback = "Unknown faction") =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    public static MatchOutcome ParseOutcome(string? result) => result?.Trim().ToUpperInvariant() switch
    { "VICTORY" => MatchOutcome.Win, "DEFEAT" => MatchOutcome.Loss, "DRAW" => MatchOutcome.Draw, _ => MatchOutcome.Unknown };
}

public sealed record MatchAnalysisLoad(MatchAnalysisEntry[] Entries, int UnreadableFiles, int DuplicateFiles);
public sealed record MatchRatingPoint(int Game, int? Rating, DateOnly Date, string Patch,string? Detail=null);
public enum MatchProgressMetric { FactionMmr, Rank, WinBalance, LadderPieces }
public sealed record MatchRatingSeries(string Faction, MatchRatingPoint[] Points, int UnorderedGames,
    MatchProgressMetric Metric = MatchProgressMetric.FactionMmr, string? Caption = null);
public sealed record MatchBreakdown(string Key, int Games, int Wins, int Losses, int Draws, int Unknown,
    double Share, double? WinRate);
public sealed record MatchAnalysisTotals(int Games, int Wins, int Losses, int Draws, int Unknown,
    double? WinRate, int ActiveDays, double? AverageFactionMmr, long? NetMmrChange, int MmrSamples,
    int MmrChangeSamples, double? AverageOpponentIdentities, double? AverageInferredCopies,
    double? AveragePlayPreviews, int InferredPatchGames, int TruncatedSequences);

public static class MatchAnalysis
{
    public static readonly string[] Factions = ["Monsters", "Nilfgaard", "Northern Realms", "Scoia'tael", "Skellige", "Syndicate"];

    // Use only full frozen reference lists to label a library deck. Observed cards
    // and inferred lists cannot identify an entire deck or its chosen name.
    public static string? DeckKey(MatchPlayer player) => player.Reference.Sum(c => c.Copies) < 25 ? null :
        string.Join("|", player.Faction, player.Leader, player.Stratagem,
            string.Join(",", player.Reference.OrderBy(c => c.CardId, StringComparer.Ordinal).Select(c => c.CardId + ":" + c.Copies)));

    public static MatchAnalysisLoad Load(string directory, CancellationToken cancellation = default, IReadOnlyList<GwentCompanion.Core.Domain.CardDefinition>? catalog = null)
    {
        if (!Directory.Exists(directory)) return new([], 0, 0);
        var entries = new Dictionary<(Guid, Guid), MatchAnalysisEntry>();
        var unreadable = 0; var duplicates = 0;
        var reviews = new MatchReviewStore(directory).Load();
        var profiles = new LocalMatchProfileStore(directory).Load();
        foreach (var path in Directory.EnumerateFiles(directory, "*.gvm", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(directory, "*" + ManualMatchStore.Extension, SearchOption.AllDirectories)))
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var source = LocalMatchStore.Read(path);
                var manual = ManualMatchStore.IsManual(source) ? ManualMatchStore.ReadRecord(path) : null;
                var review = reviews.GetValueOrDefault(MatchReviewStore.Key(source));
                var effective = MatchReviewStore.Apply(source, review);
                var factionInferred = false;
                if (catalog is not null)
                {
                    var inferred = MatchIdentityInference.Apply(effective, catalog);
                    factionInferred = inferred.User.Faction != effective.User.Faction;
                    effective = inferred;
                }
                var record = MatchAnalysisEntry.From(path, effective) with { IncludeInStatistics = review?.IncludeInStatistics,
                    Deleted = review?.Deleted ?? false, ManuallyEdited = review is not null || manual is not null, UserDeckName = review?.UserDeckName,
                    UserFactionInferred = factionInferred, LocalProfile = profiles.GetValueOrDefault(MatchReviewStore.Key(source)),
                    CaptureNote=review?.CaptureNote ?? (manual is {Abnormal:false} ? manual.Reason : null),
                    Abnormal = ManualMatchStore.IsAbnormal(source) || review?.Abnormal == true,
                    AbnormalReason = manual?.Reason ?? review?.AbnormalReason, ExactTimeKnown=manual?.ExactTimeKnown ?? true };
                var key = (record.InstallationId, record.MatchId);
                if (entries.TryGetValue(key, out var previous))
                {
                    duplicates++;
                    if (previous.Revision > record.Revision || previous.Revision == record.Revision && previous.CaptureStopped) continue;
                }
                entries[key] = record;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or OverflowException)
            { unreadable++; }
        }
        return new(entries.Values.OrderByDescending(m => m.Date).ThenByDescending(m => m.StartedAtUtc).ThenBy(m => m.MatchId).ToArray(), unreadable, duplicates);
    }

    public static MatchAnalysisEntry[] Filter(IEnumerable<MatchAnalysisEntry> records, string? patch) =>
        records.Where(m => patch is null || string.Equals(m.Patch, patch, StringComparison.Ordinal)).ToArray();
    public static MatchAnalysisEntry[] Completed(IEnumerable<MatchAnalysisEntry> records) => records.Where(m => m.IsCompleted).ToArray();

    public static string[] Patches(IEnumerable<MatchAnalysisEntry> records) => records.Select(m => m.Patch)
        .Distinct(StringComparer.Ordinal).OrderByDescending(p => Version.TryParse(p, out var v) ? v : new Version(0, 0))
        .ThenBy(p => p, StringComparer.Ordinal).ToArray();

    public static MatchBreakdown[] Breakdown(IReadOnlyCollection<MatchAnalysisEntry> entries,
        Func<MatchAnalysisEntry, string> key, IEnumerable<string>? includeEmpty = null)
    {
        entries = entries.Where(m => !m.Abnormal).ToArray();
        var groups = entries.GroupBy(key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.Ordinal);
        return groups.Keys.Concat(includeEmpty ?? []).Distinct(StringComparer.Ordinal).Select(name =>
        {
            var matches = groups.GetValueOrDefault(name) ?? [];
            var totals = Totals(matches);
            return new MatchBreakdown(name, totals.Games, totals.Wins, totals.Losses, totals.Draws, totals.Unknown,
                entries.Count == 0 ? 0 : 100d * totals.Games / entries.Count, totals.WinRate);
        }).OrderByDescending(g => g.Games).ThenBy(g => g.Key, StringComparer.Ordinal).ToArray();
    }

    public static MatchBreakdown? MatchupToReview(IEnumerable<MatchBreakdown> groups) => groups
        .Where(g => g.Key != "Unknown faction" && g.Losses > 0 && g.Wins + g.Losses >= 3)
        .OrderBy(g => g.WinRate)
        .ThenByDescending(g => g.Losses)
        .ThenByDescending(g => g.Games)
        .ThenBy(g => g.Key, StringComparer.Ordinal)
        .FirstOrDefault();

    public static MatchAnalysisTotals Totals(IReadOnlyCollection<MatchAnalysisEntry> entries)
    {
        entries = entries.Where(m => !m.Abnormal).ToArray();
        var wins = entries.Count(m => m.Outcome == MatchOutcome.Win);
        var losses = entries.Count(m => m.Outcome == MatchOutcome.Loss);
        var draws = entries.Count(m => m.Outcome == MatchOutcome.Draw);
        var mmrs = entries.Where(m => !m.MmrUnconfirmed && m.FactionMmr.HasValue).Select(m => m.FactionMmr!.Value).ToArray();
        var deltas = entries.Where(m => !m.MmrUnconfirmed && m.MmrChange.HasValue).Select(m => m.MmrChange!.Value).ToArray();
        return new(entries.Count, wins, losses, draws, entries.Count - wins - losses - draws,
            wins + losses == 0 ? null : 100d * wins / (wins + losses), entries.Select(m => m.Date).Distinct().Count(),
            mmrs.Length == 0 ? null : mmrs.Average(), deltas.Length == 0 ? null : deltas.Sum(x => (long)x), mmrs.Length, deltas.Length,
            entries.Count == 0 ? null : entries.Average(m => m.ObservedOpponentIdentities),
            entries.Count == 0 ? null : entries.Average(m => m.HypothesizedOpponentCopies),
            entries.Count == 0 ? null : entries.Average(m => m.PlayPreviews), entries.Count(m => m.PatchInferred),
            entries.Count(m => m.SequenceTruncated));
    }

    public static MatchRatingSeries RatingProgress(IEnumerable<MatchAnalysisEntry> entries, string faction)
        => Progress(entries.Where(m => m.UserFaction == faction), faction, MatchProgressMetric.FactionMmr);

    public static MatchRatingSeries RankProgress(IEnumerable<MatchAnalysisEntry> entries)
        => Progress(entries, "Ladder rank", MatchProgressMetric.Rank);
    public static MatchRatingSeries ResultProgress(IEnumerable<MatchAnalysisEntry> entries)
        => Progress(entries.Where(m => m.IsCompleted), "Win / loss progression", MatchProgressMetric.WinBalance);

    private static MatchRatingSeries Progress(IEnumerable<MatchAnalysisEntry> entries, string title, MatchProgressMetric metric)
    {
        var points = new List<MatchRatingPoint>(); var game = 0; var unordered = 0; var balance = 0;
        foreach (var day in entries.GroupBy(m => m.Date).OrderBy(g => g.Key))
        {
            // A date alone cannot order multiple games. Keep their x-axis slots,
            // but leave a gap rather than draw an invented trajectory.
            var ordered = day.Count() == 1 || day.All(m => m.StartedAtUtc.HasValue) &&
                day.Select(m => m.StartedAtUtc).Distinct().Count() == day.Count();
            if (!ordered) unordered += day.Count();
            foreach (var match in day.OrderBy(m => m.StartedAtUtc))
            {
                balance += match.Outcome == MatchOutcome.Win ? 1 : match.Outcome == MatchOutcome.Loss ? -1 : 0;
                int? value = metric switch
                {
                    MatchProgressMetric.Rank => match.EffectiveRank,
                    MatchProgressMetric.WinBalance => balance,
                    _ => match.MmrUnconfirmed ? null : match.FactionMmr
                };
                points.Add(new(++game, ordered ? value : null, day.Key, match.Patch));
            }
        }
        return new(title, points.ToArray(), unordered, metric);
    }
}
