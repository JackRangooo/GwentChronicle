namespace GwentCompanion.Core.Data;

public sealed class LocalMatchStore(string directory)
{
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public string Save(CompactMatch match)
    {
        if (ManualMatchStore.IsManual(match)) throw new ArgumentException("手动异常记录必须单独保存，不能写成自动采集记录。");
        var folder = Path.Combine(DirectoryPath, match.GameDateUtc.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture));
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, match.MatchId.ToString("N") + ".gvm");
        var bytes = CompactMatchCodec.Encode(match);
        if (File.Exists(path))
        {
            var existingBytes = File.ReadAllBytes(path);
            var existing = CompactMatchCodec.Decode(existingBytes); // Corruption must not be silently overwritten.
            if (existing.Revision > match.Revision || existing.Revision == match.Revision && existing.CaptureStopped && !match.CaptureStopped) return path;
            if (existingBytes.AsSpan().SequenceEqual(bytes)) return path;
        }
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return path;
    }
    public static CompactMatch Read(string path) => path.EndsWith(ManualMatchStore.Extension, StringComparison.OrdinalIgnoreCase)
        ? ManualMatchStore.Read(path) : CompactMatchCodec.Decode(File.ReadAllBytes(path));
}
