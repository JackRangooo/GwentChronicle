using System.Text.Json;

namespace GwentCompanion.Core.Data;

/// <summary>Identifying player details stay local and are never part of CompactMatch or its export/upload.</summary>
public sealed record LocalPlayerProfile(string? Name = null, int? AccountLevel = null, int? Prestige = null,
    int? Rank = null, string Source = "Manual", string? NameSource = null, string? RankSource = null);
public sealed record LocalMatchProfile(LocalPlayerProfile? User = null, LocalPlayerProfile? Opponent = null,
    DateTimeOffset UpdatedAtUtc = default, MatchRound[]? ResultRoundScores = null, bool? CountForLadder = null);

public sealed class LocalMatchProfileStore(string directory)
{
    private static bool IsManualName(LocalPlayerProfile? player)=>player?.NameSource=="Manual" || player is {Name:not null,NameSource:null,Source:"Manual" or "UserConfirmed"};
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public string PathName => Path.Combine(Path.GetFullPath(directory), "local-player-profiles.json");
    public Dictionary<string, LocalMatchProfile> Load()
    {
        if (!File.Exists(PathName)) return new(StringComparer.Ordinal);
        try { return JsonSerializer.Deserialize<Dictionary<string, LocalMatchProfile>>(File.ReadAllText(PathName), Json)
            ?? throw new InvalidDataException("Local player details are empty."); }
        catch (JsonException error) { throw new InvalidDataException("Could not read local player details.", error); }
    }
    public LocalMatchProfile? Get(CompactMatch match) => Load().GetValueOrDefault(MatchReviewStore.Key(match));
    public void Set(CompactMatch match, LocalMatchProfile profile)
    {
        Validate(profile);
        Update(MatchReviewStore.Key(match),_ => profile);
    }
    public void MergeRecognizedNames(CompactMatch match,string? userName,string? opponentName)
    {
        if(userName is null && opponentName is null)return;
        Update(MatchReviewStore.Key(match),previous =>
        {
            LocalPlayerProfile? WithName(LocalPlayerProfile? player,string? name) => name is null || IsManualName(player) || !string.IsNullOrWhiteSpace(player?.Name)
                ? player : player is null ? new(Name:name,Source:"ScreenOcr",NameSource:"ScreenOcr") : player with { Name = name, NameSource = "ScreenOcr" };
            var user=WithName(previous.User,userName);var opponent=WithName(previous.Opponent,opponentName==user?.Name?null:opponentName);
            if(opponent?.Name==user?.Name && opponent is {Name:not null} && !IsManualName(opponent))opponent=opponent with{Name=null,NameSource=null};
            return previous with {User=user,Opponent=opponent};
        });
    }
    public void MergeResultPlayers(CompactMatch match,GwentCompanion.Core.Vision.ResultPlayers? players,
        IReadOnlyList<GwentCompanion.Core.Vision.PostMatchRoundScore>? scores)
    {
        if(players is null && scores is null)return;
        Update(MatchReviewStore.Key(match),previous=>
        {
            LocalPlayerProfile Player(LocalPlayerProfile? old,GwentCompanion.Core.Vision.ResultPlayer seen)
            {
                var value=old??new(Source:"ResultBanner");
                if(seen.Name is not null && !IsManualName(value))value=value with{Name=seen.Name,NameSource="ResultBanner"};
                var manualRank=value.RankSource is "Manual" or "UserConfirmed" || value.RankSource is null &&
                    value.Rank is not null && value.Source is "Manual" or "UserConfirmed";
                if(seen.Rank is not null && !manualRank)value=value with{Rank=seen.Rank,RankSource="ResultBanner"};
                return value;
            }
            var settled=scores?.Select(s=>new MatchRound(s.Round,s.UserScore,s.OpponentScore,true)).ToArray();
            return previous with{User=players is null?previous.User:Player(previous.User,players.User),Opponent=players is null?previous.Opponent:Player(previous.Opponent,players.Opponent),
                ResultRoundScores=settled is null || previous.ResultRoundScores?.SequenceEqual(settled)==true?previous.ResultRoundScores:settled};
        });
    }
    private void Update(string key,Func<LocalMatchProfile,LocalMatchProfile> change)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var gate = new FileStream(PathName + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var entries = Load();
        var previous=entries.GetValueOrDefault(key)??new();var profile=change(previous);Validate(profile);
        if(entries.ContainsKey(key) && profile==previous)return;
        entries[key] = profile with { UpdatedAtUtc = DateTimeOffset.UtcNow };
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
    public static void Validate(LocalMatchProfile profile)
    {
        foreach (var player in new[] { profile.User, profile.Opponent }.OfType<LocalPlayerProfile>())
        {
            if (player.Name is { } name && (name.Length > 80 || name.Any(char.IsControl)))
                throw new ArgumentException("Player name must be at most 80 characters, without control characters.");
            if (player.AccountLevel is < 1 or > 60) throw new ArgumentException("Account level must be between 1 and 60.");
            if (player.Prestige is < 0 or > 10) throw new ArgumentException("Prestige must be between 0 and 10.");
            if (player.Rank is < 0 or > 30) throw new ArgumentException("Ladder rank must be between 0 and 30 (0 = Pro Rank).");
            if (player.Source is not ("Manual" or "UserConfirmed" or "ScreenOcr" or "ResultBanner")) throw new ArgumentException("Unknown player details source.");
            if (player.NameSource is not (null or "Manual" or "ScreenOcr" or "ResultBanner")) throw new ArgumentException("Unknown player name source.");
            if (player.RankSource is not (null or "Manual" or "UserConfirmed" or "ResultBanner")) throw new ArgumentException("Unknown rank source.");
        }
        if(profile.ResultRoundScores is { } rounds && (rounds.Length is <2 or >3 || rounds.Where((r,i)=>r.Number!=i+1 ||
            r.UserScore is null or <0 or >9999 || r.OpponentScore is null or <0 or >9999 || !r.FinalConfirmed).Any()))
            throw new ArgumentException("Invalid settled round scores.");
    }
}
