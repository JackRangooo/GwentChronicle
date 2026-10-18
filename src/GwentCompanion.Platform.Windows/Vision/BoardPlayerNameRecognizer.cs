using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>Only the two name lanes on a confirmed 16:9 game board, never the title line or deck counters.</summary>
public sealed class BoardPlayerNameRecognizer : IDisposable
{
    private DateTimeOffset _lastRead;
    private volatile bool _userEnabled = true, _opponentEnabled = true;
    private ScreenStateRecognizer? _english;
    public void SetEnabled(bool user,bool opponent) { _userEnabled=user; _opponentEnabled=opponent; }
    public static readonly NormalizedRegion UserLane = new(.055,.915,.24,.957);
    public static readonly NormalizedRegion OpponentLane = new(.055,.012,.24,.057);
    public async Task<GwentVisualObservation> ReadAsync(PixelFrame frame, GwentVisualObservation screen,
        DateTimeOffset at, ScreenStateRecognizer reader)
    {
        var readUser=_userEnabled;var readOpponent=_opponentEnabled;
        if (!readUser && !readOpponent || !screen.FrameGeometrySupported || screen.View != GwentViewKind.Board || screen.MatchHudVisible != true ||
            screen.IsCardSelectionOverlay || screen.PostMatchExitCue || PostMatchMmr.IsResultHeader(screen.ScreenHeader) ||
            at - _lastRead < TimeSpan.FromSeconds(2)) return screen;
        _lastRead = at;
        // Some menu artwork resembles the small fixed HUD counters. Its BACK
        // control and upper-left account handle cannot establish an opponent.
        var footer=string.Concat((await reader.ReadAsync(frame,new(.46,.905,.54,.972)).ConfigureAwait(false)).Where(char.IsLetter)).ToUpperInvariant();
        if(footer is "BACK" or "返回")return screen;
        var user = readUser ? await ReadNameLaneAsync(frame,UserLane,reader).ConfigureAwait(false) : null;
        var opponent = readOpponent ? await ReadNameLaneAsync(frame,OpponentLane,reader).ConfigureAwait(false) : null;
        return screen with { PlayerNames = new(user,opponent) };
    }
    public async Task<string?> ReadNameLaneAsync(PixelFrame frame,NormalizedRegion lane,ScreenStateRecognizer reader,bool contextual=false)
    {
        // Preserve the title line as OCR context on result banners, but only
        // accept boxes in the name lane. A thin isolated crop loses CJK strokes.
        var crop=contextual?new NormalizedRegion(Math.Max(0,lane.Left-.006),Math.Max(0,lane.Top-.017),lane.Right,Math.Min(1,lane.Bottom+.046)):lane;
        async Task<string?> Read(ScreenStateRecognizer engine,int scale,bool mask) => Parse(await engine.ReadLinesAsync(frame,crop,
            scale:scale,enhance:false,whiteLetterMask:mask,smooth:true).ConfigureAwait(false),lane);
        // Chinese OCR mangles Latin handles; English OCR cannot establish a Chinese handle.
        // Require agreeing transforms in the suitable script before the separate capture votes.
        if(reader.OcrLanguage.StartsWith("zh",StringComparison.OrdinalIgnoreCase))
        {
            var chinese=await Read(reader,4,false).ConfigureAwait(false);
            if(chinese?.Any(c=>c is >= '\u3400' and <= '\u9fff')==true && chinese==await Read(reader,3,false).ConfigureAwait(false))return chinese;
        }
        var english=reader.OcrLanguage.StartsWith("en",StringComparison.OrdinalIgnoreCase)?reader:(_english??=new("en-US"));
        var values=new List<(string? Name,bool Masked)> {(await Read(english,4,false).ConfigureAwait(false),false),(await Read(english,4,true).ConfigureAwait(false),true)};
        string? Agreement() => values.Where(v=>v.Name is not null).GroupBy(v=>v.Name!,StringComparer.Ordinal).Where(g=>g.Count()>=2 && g.Any(v=>!v.Masked))
            .OrderByDescending(g=>g.Count()).Select(g=>g.Key).ToArray() is {Length:1} agreed?agreed[0]:null;
        if(Agreement() is { } initial)return initial;
        values.Add((await Read(english,2,false).ConfigureAwait(false),false));values.Add((await Read(english,3,false).ConfigureAwait(false),false));
        if(Agreement() is { } ordinary)return ordinary;
        values.Add((await Read(english,2,true).ConfigureAwait(false),true));values.Add((await Read(english,3,true).ConfigureAwait(false),true));
        return Agreement();
    }
    public static string? Parse(IReadOnlyList<VisibleTextLine> lines, NormalizedRegion lane)
    {
        var names = lines.Where(line => line.Region.Left >= lane.Left && line.Region.Right <= lane.Right &&
                line.Region.Top >= lane.Top && line.Region.Bottom <= lane.Bottom)
            .Select(line => System.Text.RegularExpressions.Regex.Replace(line.Text.Trim(),@"(?<=[\u3400-\u9fff])\s+(?=[\u3400-\u9fff])","")).Where(text => text.Length is >= 2 and <= 40 &&
                text.Any(char.IsLetter) && char.IsLetterOrDigit(text[0]) && char.IsLetterOrDigit(text[^1]) &&
                text.All(c=>char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or ' ')).Distinct(StringComparer.Ordinal).ToArray();
        // Multiple text rows mean a title/overlay has intruded. Never take the first one by guesswork.
        return names.Length == 1 ? names[0] : null;
    }
    public void Reset() { _lastRead = default; SetEnabled(true,true); }
    public void Dispose() => _english?.Dispose();
}
