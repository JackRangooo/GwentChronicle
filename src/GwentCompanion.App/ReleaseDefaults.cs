using System.IO;

namespace GwentCompanion.App;

internal static class ReleaseDefaults
{
    // Release ZIPs contain seeds under defaults/, never runtime cache or user data.
    // An update can therefore be extracted over the original folder safely.
    internal static void Initialize(string root)
    {
        foreach (var name in new[] { "gwent-one-cards.json", "create-point-profiles.json", "deck-library.json", "data-service.json" })
        {
            var source = Path.Combine(root, "defaults", "cache", name);
            var target = Path.Combine(root, "cache", name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try { File.Copy(source, target, false); }
            catch (IOException) when (File.Exists(target)) { } // Another fresh-start instance initialized it first.
        }
        foreach (var folder in new[] { "portraits", "premium-frames", "observed-art" })
        {
            var sourceRoot = Path.Combine(root, "defaults", "cache", folder);
            if (!Directory.Exists(sourceRoot)) continue;
            foreach (var source in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories)
                .Where(file => Path.GetExtension(file).ToLowerInvariant() is ".jpg" or ".png"))
            {
                var target = Path.Combine(root, "cache", folder, Path.GetRelativePath(sourceRoot, source));
                if (File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                try { File.Copy(source, target, false); }
                catch (IOException) when (File.Exists(target)) { }
            }
        }
    }
}
