using System.Text.Json;

namespace GwentCompanion.Core.Data;

/// <summary>A local journal entry, never a detector record or an upload candidate.</summary>
public sealed record ManualMatchRecord(Guid InstallationId, Guid MatchId, DateTimeOffset OccurredAtUtc,
    string Patch, string Reason = "网络异常／未正常进入对局", DateTimeOffset UpdatedAtUtc = default,
    bool Abnormal = true, string Result = "DEFEAT", MatchPlayer? User = null, MatchPlayer? Opponent = null,
    MatchRound[]? Rounds = null, bool ExactTimeKnown = true);

public sealed class ManualMatchStore(string directory)
{
    public const string Extension = ".manual-match.json";
    public const string DetectorVersion = "local-manual-abnormal";
    public const string ConfirmedDetectorVersion = "local-manual-confirmed";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static bool IsManual(CompactMatch match) => match.DetectorVersion is DetectorVersion or ConfirmedDetectorVersion;
    public static bool IsAbnormal(CompactMatch match) => match.DetectorVersion == DetectorVersion;
    public string Save(ManualMatchRecord record)
    {
        if (record.InstallationId == Guid.Empty || record.MatchId == Guid.Empty || record.OccurredAtUtc == default ||
            record.OccurredAtUtc > DateTimeOffset.UtcNow.AddMinutes(1) || string.IsNullOrWhiteSpace(record.Patch) ||
            string.IsNullOrWhiteSpace(record.Reason) || record.Reason.Length > 500 || record.Result is not ("VICTORY" or "DEFEAT" or "DRAW"))
            throw new ArgumentException("异常对局的时间、版本或说明无效，请勿填写未来时间。");
        var folder = Path.Combine(Path.GetFullPath(directory), "manual-matches");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, record.MatchId.ToString("N") + Extension);
        using var gate = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        if (File.Exists(path) && ReadRecord(path).InstallationId != record.InstallationId)
            throw new InvalidDataException("手动记录标识不一致，原记录已保留。");
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(stream, record with { OccurredAtUtc = record.OccurredAtUtc.ToUniversalTime(), UpdatedAtUtc = DateTimeOffset.UtcNow }, Json); stream.Flush(true); }
            if (File.Exists(path)) File.Copy(path, path + ".bak", true);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    public static ManualMatchRecord ReadRecord(string path)
    {
        try
        {
            var record = JsonSerializer.Deserialize<ManualMatchRecord>(File.ReadAllText(path), Json)
                ?? throw new InvalidDataException("手动对局记录为空。");
            if (record.InstallationId == Guid.Empty || record.MatchId == Guid.Empty || record.OccurredAtUtc == default ||
                string.IsNullOrWhiteSpace(record.Patch) || string.IsNullOrWhiteSpace(record.Reason))
                throw new InvalidDataException("手动对局记录不完整。");
            return record;
        }
        catch (JsonException error) { throw new InvalidDataException("无法读取手动对局记录。", error); }
    }
    public static CompactMatch Read(string path)
    {
        var record = ReadRecord(path); var unknown = new MatchPlayer(null, null, null, [], [], []);
        return new(record.InstallationId, record.MatchId, DateOnly.FromDateTime(record.OccurredAtUtc.UtcDateTime),
            record.Abnormal ? DetectorVersion : ConfirmedDetectorVersion, "manual-journal-v1", record.Patch, true, 1, true, false, record.Result, null, null, null,
            false, null, record.User ?? unknown, record.Opponent ?? unknown, record.Rounds ?? [], [], StartedAtUtc: record.OccurredAtUtc);
    }
}
