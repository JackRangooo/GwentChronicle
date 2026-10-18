using GwentCompanion.Core.Vision;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using System.IO;
using OpenCvSharp;

namespace GwentCompanion.Platform.Windows.Vision;

public sealed record VisibleTextLine(string Text, NormalizedRegion Region);

/// <summary>Only reads the visible screen header. No game process or private state is accessed.</summary>
public sealed class ScreenStateRecognizer : IDisposable
{
    private readonly GwentVisualStateDetector _detector = new();
    public static string DefaultOcrLanguage { get; set; } = "en-US";
    private readonly OcrEngine _ocr;
    public string OcrLanguage => _ocr.RecognizerLanguage.LanguageTag;
    public ScreenStateRecognizer(string? language = null)
    {
        var requested = language ?? DefaultOcrLanguage;
        _ocr = OcrEngine.TryCreateFromLanguage(new Language(requested)) ?? throw new InvalidOperationException(
            requested.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "Windows 简体中文 OCR 不可用，请在 Windows 语言选项中安装中文文字识别，或在设置中选择英文游戏界面。"
                : "Windows English OCR is unavailable. Install the English language OCR feature before running recognition.");
    }
    private readonly Mat? _graveyardHeader = LoadHeader();
    private sealed record OcrSnapshot(byte[] Pixels, IReadOnlyList<VisibleTextLine> Lines);
    private readonly Dictionary<string, OcrSnapshot> _textCache = [];
    private readonly Queue<string> _textKeys = new();
    private string? _endVote;
    public bool UseTextCache { get; set; } = true;
    public int OcrCalls { get; private set; }
    public int OcrCacheHits { get; private set; }
    public int CachedTextBytes { get; private set; }

    public async Task<GwentVisualObservation> AnalyzeAsync(PixelFrame frame)
    {
        var observation = _detector.Analyze(frame);
        // Top-center headings distinguish redraw, tutor/Create choices, deck and graveyard
        // from actual play. Their backgrounds vary with the selected battlefield.
        if (HasGraveyardHeader(frame)) return observation with
        {
            IsCardSelectionOverlay = true, CardSelectionConfidence = 1, ScreenHeader = "GRAVEYARD (header shape)",
        };
        var header = await ReadAsync(frame, new NormalizedRegion(0.27, 0.0, 0.75, 0.125)).ConfigureAwait(false);
        // The forfeit/disconnect result is a centered modal, not a top heading.
        // Require both its fixed layout and repeated complete result text.
        if (observation.MatchHudVisible == false && observation.TooltipRegion is { Left: >= .30, Right: <= .70, Top: >= .30, Bottom: <= .75 })
        {
            var modal = string.Join('\n', (await ReadLinesAsync(frame, new(.35, .39, .66, .54), enhance: false).ConfigureAwait(false))
                .Select(line => line.Text)).ToUpperInvariant();
            var compactModal = GwentCompanion.Core.Data.CardTextLocalization.Compact(modal);
            var ended = ((modal.Contains("GAME OVER", StringComparison.Ordinal) || modal.Contains("GAME OUER", StringComparison.Ordinal)) &&
                modal.Contains("FORFEIT", StringComparison.Ordinal)) ||
                ((compactModal.Contains("游戏结束", StringComparison.Ordinal) || compactModal.Contains("对局结束", StringComparison.Ordinal)) &&
                 compactModal.Contains("投降", StringComparison.Ordinal));
            if (ended && _endVote == "GAME OVER") header = "GAME OVER";
            _endVote = ended ? "GAME OVER" : null;
        }
        else _endVote = null;
        // Result ribbons are centered over the board; they are not top headings.
        // Retry only after the fixed match HUD has disappeared so ordinary play
        // pays no extra OCR cost and card/stream overlays cannot become outcomes.
        if (string.IsNullOrWhiteSpace(header) && observation.MatchHudVisible == false)
        {
            var centered = await ReadLinesAsync(frame, new(.30, .35, .70, .61),
                scale: 4, enhance: true, whiteLetterMask: true).ConfigureAwait(false);
            header = centered.Select(line => NormalizeResult(line.Text)).FirstOrDefault(value => value is not null) ?? "";
        }
        // Verified against the latest result screenshot: the decorative V is read as U.
        // Normalize only this exact heading, never arbitrary OCR letters or numeric ratings.
        header = NormalizeHeader(header);
        var overlay = IsOverlayHeader(header);
        return observation with
        {
            IsCardSelectionOverlay = observation.IsCardSelectionOverlay || overlay,
            CardSelectionConfidence = overlay ? 1 : observation.CardSelectionConfidence,
            ScreenHeader = string.IsNullOrWhiteSpace(header) ? null : header,
        };
    }

    private static bool IsOverlayHeader(string header)
    {
        var compact = string.Concat(header.Where(char.IsLetterOrDigit)).ToUpperInvariant();
        return new[] { "CHOOSE", "SELECT", "PICK", "FICK", "CARDTOPLAY", "CARDTODISCARD", "GRAVEYARD", "GRAUEYARD", "DECK", "ROUND", "ROIJND", "REDRAW", "VICTORY", "DEFEAT", "DRAW", "GAMEOVER" }
            .Any(word => compact.Contains(word, StringComparison.Ordinal));
    }

    public static string? NormalizeResult(string? value)
    {
        var compact = string.Concat((value ?? "").Where(char.IsLetter)).ToUpperInvariant();
        return compact switch
        {
            "VICTORY" or "UICTORY" or "胜利" or "获胜" => "VICTORY",
            "DEFEAT" or "失败" or "战败" => "DEFEAT",
            "DRAW" or "平局" => "DRAW",
            _ => null,
        };
    }

    public static string NormalizeHeader(string? value)
    {
        if (value?.Contains('\n') == true)
        {
            foreach (var line in value.Split('\n').Where(line => !string.IsNullOrWhiteSpace(line)).Take(5))
            {
                var normalized = NormalizeHeader(line);
                if (!normalized.Equals(line.Trim(), StringComparison.Ordinal)) return normalized;
            }
        }
        var result = NormalizeResult(value);
        if (result is not null) return result;
        var compact = GwentCompanion.Core.Data.CardTextLocalization.Compact(value ?? "");
        var round = System.Text.RegularExpressions.Regex.Match(compact, @"^(?:第)?([123一二三])(?:小局|回合|轮)$|^(?:小局|回合)([123一二三])$");
        if (round.Success)
        {
            var number = (round.Groups[1].Success ? round.Groups[1].Value : round.Groups[2].Value) switch
            { "一" => "1", "二" => "2", "三" => "3", var digit => digit };
            return "ROUND " + number;
        }
        if (System.Text.RegularExpressions.Regex.IsMatch(compact, @"^选择1张卡牌重抽[0-9]+$")) return "REDRAW";
        return compact switch
        {
            "最终小局" or "最终回合" or "决胜局" or "决胜局开始" or "最后一局" => "FINAL ROUND",
            "重新抽牌" or "替换卡牌" or "更换卡牌" or "调度" => "REDRAW",
            "蓝方正在重抽卡牌" or "红方正在重抽卡牌" or "敌方正在调度手牌" => "REDRAW",
            "墓场" or "墓地" or "敌方墓场" => "GRAVEYARD", "牌库" or "卡组" or "牌组" or "敌方牌组" => "DECK",
            "选择要打出的卡牌" or "选择1张要打出的牌" or "选择一张要打出的牌" or "选择打出一张牌" => "PICK A CARD TO PLAY",
            "选择要弃置的卡牌" or "选择1张要弃置的牌" or "选择一张要弃置的牌" or "选择丢弃一张牌" => "CHOOSE A CARD TO DISCARD",
            "选择卡牌" or "选择1张牌" or "选择一张牌" => "PICK A CARD",
            "选择移动一张牌" => "PICK A CARD TO MOVE",
            "选择将一张牌放回牌组" => "PICK A CARD TO SHUFFLE INTO YOUR DECK",
            "选择抽取一张牌" => "PICK A CARD TO DRAW",
            "选择生成一张牌" => "PICK A CARD TO SPAWN",
            "选择复制一张牌" => "PICK A CARD TO COPY",
            "选择揭示一张牌" => "PICK A CARD TO REVEAL",
            "游戏结束" or "对局结束" => "GAME OVER",
            _ => value?.Trim() ?? "",
        };
    }

    public async Task<string> ReadAsync(PixelFrame frame, NormalizedRegion region)
        => string.Join('\n', (await ReadLinesAsync(frame, region).ConfigureAwait(false)).Select(line => line.Text));

    /// <summary>Windows OCR often ignores an isolated rank digit. Three copies make a text line, and must all agree.</summary>
    public async Task<int?> ReadRepeatedRankAsync(PixelFrame frame,NormalizedRegion region)
    {
        const int scale=4,padding=24,copies=3;
        var sourceWidth=(region.PixelRight(frame.Width)-region.PixelLeft(frame.Width))*scale;
        var sourceHeight=(region.PixelBottom(frame.Height)-region.PixelTop(frame.Height))*scale;
        var width=copies*(sourceWidth+padding)+padding;var height=sourceHeight+padding*2;
        if(sourceWidth<8 || sourceHeight<8 || width>OcrEngine.MaxImageDimension || height>OcrEngine.MaxImageDimension)return null;
        var source=OcrPixels.Build(frame,region,scale,false,true);var pixels=new byte[width*height*4];Array.Fill(pixels,(byte)255);
        for(var copy=0;copy<copies;copy++)for(var y=0;y<sourceHeight;y++)
            source.AsSpan(y*sourceWidth*4,sourceWidth*4).CopyTo(pixels.AsSpan(((y+padding)*width+padding+copy*(sourceWidth+padding))*4));
        using var writer=new DataWriter();writer.WriteBytes(pixels);
        using var bitmap=SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),BitmapPixelFormat.Bgra8,width,height,BitmapAlphaMode.Ignore);
        OcrCalls++;var recognized=await _ocr.RecognizeAsync(bitmap);
        var text=string.Join(" ",recognized.Lines.Select(l=>l.Text));
        var parts=System.Text.RegularExpressions.Regex.Matches(text,@"(?<![0-9])[0-9]{1,2}(?![0-9])").Select(m=>m.Value).ToArray();
        if(parts.Length==copies && parts.Distinct(StringComparer.Ordinal).Count()==1 && int.TryParse(parts[0],out var number) && number<=30)return number;
        var compact=string.Concat(text.Where(char.IsDigit));
        if(compact.Length is 3 or 6)
        {
            var length=compact.Length/copies;var value=compact[..length];
            if(compact==string.Concat(Enumerable.Repeat(value,copies)) && int.TryParse(value,out var repeated) && repeated<=30)return repeated;
        }
        return null;
    }

    public async Task<IReadOnlyList<VisibleTextLine>> ReadLinesAsync(PixelFrame frame, NormalizedRegion region,
        int scale = 3, bool enhance = true, bool whiteLetterMask = false, bool smooth = false)
    {
        if (scale is < 1 or > 4) throw new ArgumentOutOfRangeException(nameof(scale));
        var pixels = OcrPixels.Build(frame, region, scale, enhance, whiteLetterMask);
        return await ReadPixelsAsync(frame, region, scale, pixels, smooth).ConfigureAwait(false);
    }

    /// <summary>One optional style-aware retry, sharing the bounded OCR cache and exact source coordinates.</summary>
    public async Task<IReadOnlyList<VisibleTextLine>> ReadTitleLinesAsync(PixelFrame frame, NormalizedRegion region)
    {
        var width = region.PixelRight(frame.Width) - region.PixelLeft(frame.Width);
        var height = region.PixelBottom(frame.Height) - region.PixelTop(frame.Height);
        if (width < 8 || height < 8) return [];
        var scale = 4;
        // Keep supplemental OCR inside both the engine's dimension limit and the
        // existing 4 MiB cache budget, including on high-resolution captures.
        while (scale >= 2 && (width * scale > OcrEngine.MaxImageDimension || height * scale > OcrEngine.MaxImageDimension ||
            4L * width * height * scale * scale > 4 * 1024 * 1024)) scale--;
        if (scale < 2) return [];
        var pixels = TitleTextStyle.TryBuild(frame, region, scale);
        return pixels is null ? [] : await ReadPixelsAsync(frame, region, scale, pixels, smooth: true).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<VisibleTextLine>> ReadPixelsAsync(PixelFrame frame, NormalizedRegion region,
        int scale, byte[] pixels, bool smooth)
    {
        var left = region.PixelLeft(frame.Width); var top = region.PixelTop(frame.Height);
        var width = (region.PixelRight(frame.Width) - left) * scale;
        var height = (region.PixelBottom(frame.Height) - top) * scale;
        if (smooth)
        {
            using var nearest = Mat.FromPixelData(height, width, MatType.CV_8UC4, pixels);
            using var original = new Mat(); using var enlarged = new Mat();
            Cv2.Resize(nearest, original, new Size(width / scale, height / scale), 0, 0, InterpolationFlags.Area);
            Cv2.Resize(original, enlarged, new Size(width, height), 0, 0, InterpolationFlags.Cubic);
            System.Runtime.InteropServices.Marshal.Copy(enlarged.Data, pixels, 0, pixels.Length);
        }
        // The hash selects an entry; byte equality proves identical OCR input.
        // Geometry is part of the key so cached bounding boxes remain exact.
        var key = UseTextCache ? $"{OcrLanguage}:{frame.Width},{frame.Height},{left},{top},{width},{height},{scale}:" +
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(pixels)) : null;
        if (key is not null && _textCache.TryGetValue(key, out var cached) && pixels.AsSpan().SequenceEqual(cached.Pixels))
        { OcrCacheHits++; return cached.Lines; }
        if (key is not null && _textCache.ContainsKey(key))
        { _textCache.Clear(); _textKeys.Clear(); CachedTextBytes = 0; }
        using var writer = new DataWriter();
        writer.WriteBytes(pixels);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(), BitmapPixelFormat.Bgra8,
            width, height, BitmapAlphaMode.Ignore);
        OcrCalls++;
        var result = await _ocr.RecognizeAsync(bitmap);
        var lines = Array.AsReadOnly(result.Lines.Where(line => line.Words.Count > 0).Select(line => new VisibleTextLine(line.Text,
            new NormalizedRegion(
                (left + line.Words.Min(word => word.BoundingRect.Left) / scale) / frame.Width,
                (top + line.Words.Min(word => word.BoundingRect.Top) / scale) / frame.Height,
                (left + line.Words.Max(word => word.BoundingRect.Right) / scale) / frame.Width,
                (top + line.Words.Max(word => word.BoundingRect.Bottom) / scale) / frame.Height))).ToArray());
        if (key is not null && pixels.Length <= 4 * 1024 * 1024)
        {
            while (_textKeys.Count >= 48 || CachedTextBytes + pixels.Length > 4 * 1024 * 1024)
            {
                var oldest = _textKeys.Dequeue(); CachedTextBytes -= _textCache[oldest].Pixels.Length; _textCache.Remove(oldest);
            }
            _textCache[key] = new(pixels, lines); _textKeys.Enqueue(key); CachedTextBytes += pixels.Length;
        }
        return lines;
    }

    private bool HasGraveyardHeader(PixelFrame frame)
    {
        if (_graveyardHeader is null) return false;
        using var image = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC4, frame.BgraPixels);
        using var crop = new Mat(image, new Rect((int)(frame.Width * 0.38), (int)(frame.Height * 0.02),
            (int)(frame.Width * 0.25), (int)(frame.Height * 0.08)));
        using var gray = new Mat(); using var normalized = new Mat(); using var scores = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGRA2GRAY);
        Cv2.Resize(gray, normalized, new Size(240, 43), 0, 0, InterpolationFlags.Area);
        Cv2.MatchTemplate(normalized, _graveyardHeader, scores, TemplateMatchModes.CCoeffNormed);
        Cv2.MinMaxLoc(scores, out double _, out double maximum);
        return maximum >= 0.80;
    }

    private static Mat? LoadHeader()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "vision-assets", "graveyard-header.png");
        return File.Exists(path) ? Cv2.ImRead(path, ImreadModes.Grayscale) : null;
    }

    public void Dispose() => _graveyardHeader?.Dispose();
}
