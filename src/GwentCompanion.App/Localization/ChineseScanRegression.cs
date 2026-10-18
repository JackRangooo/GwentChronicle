using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Capture;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App.Localization;

// Replays retained deck-list crops only; never captures the game or writes its library.
internal static class ChineseScanRegression
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var i = Array.IndexOf(args, "--chinese-scan-regression");
            var scans = args[i + 1]; var cache = args[i + 2]; var output = args[i + 3];
            var catalog = GwentOneCardCatalog.Load(cache);
            using var scanner = new DeckBuilderScanner(catalog, "zh-Hans-CN");
            var pages = new List<object>(); var draft = new DeckScanDraft();
            foreach (var path in Directory.GetFiles(scans, "page-*.json").Order())
            {
                using var metadata = JsonDocument.Parse(File.ReadAllText(path));
                var root = metadata.RootElement;
                var width = root.GetProperty("SourceWidth").GetInt32();
                var height = root.GetProperty("SourceHeight").GetInt32();
                var region = root.GetProperty("Region").Deserialize<NormalizedRegion>();
                var crop = new BitmapImage(); crop.BeginInit(); crop.CacheOption = BitmapCacheOption.OnLoad;
                crop.UriSource = new Uri(Path.ChangeExtension(path, ".jpg")); crop.EndInit(); crop.Freeze();
                var visual = new DrawingVisual();
                using (var context = visual.RenderOpen())
                {
                    context.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
                    context.DrawImage(crop, new Rect(region.PixelLeft(width), region.PixelTop(height), crop.PixelWidth, crop.PixelHeight));
                }
                var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(visual); bitmap.Freeze();
                var page = await scanner.ReadAsync(BitmapFrameAdapter.ToPixelFrame(bitmap), region);
                draft.Observe(page.Cards);
                draft.Observe(page.Cards); // Test bounded consensus and overlapping-page maxima.
                draft.ObserveHeader(page.Leader, page.Stratagem); draft.ObserveHeader(page.Leader, page.Stratagem);
                pages.Add(new { File = Path.GetFileName(path), Cards = page.Cards.Select(c => new { c.Card.Id, c.Card.Name,
                    Chinese = CardTextLocalization.ChineseName(c.Card.Id), c.Count }), page.Lines, page.Quantities, page.UnresolvedRows,
                    Leader = page.Leader?.Name, Stratagem = page.Stratagem?.Name });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
            File.WriteAllText(output, JsonSerializer.Serialize(new { Pages = pages, ReplayDraftCount = draft.CardCount,
                Draft = draft.Cards.Select(c => new { c.Card.Id, c.Card.Name, c.Count }), Leader = draft.Leader?.Name, Stratagem = draft.Stratagem?.Name,
                Evidence = "Retained real Chinese crops; each page replayed twice to test consensus. No game capture or library writes." },
                new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            return pages.Count > 0 && draft.CardCount > 0 ? 0 : 1;
        }
        catch (Exception error) { File.WriteAllText(args.Last(), error.ToString()); return 1; }
    }
}
