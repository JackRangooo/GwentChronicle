using System.Text.RegularExpressions;
using System.Text.Json;
using System.IO;
using GwentCompanion.Core.Vision;

namespace GwentCompanion.Platform.Windows.Vision;

/// <summary>The settled score table has two rank badges and larger name lanes. Badge ranks describe this match, not a later promotion animation.</summary>
public sealed class ResultPlayerRecognizer : IDisposable
{
    public static readonly NormalizedRegion UserRankLane = new(.389,.160,.405,.199);
    public static readonly NormalizedRegion OpponentRankLane = new(.893,.160,.909,.199);
    public static readonly NormalizedRegion UserNameLane = new(.150,.137,.375,.184);
    public static readonly NormalizedRegion OpponentNameLane = new(.643,.137,.868,.184);
    private readonly BoardPlayerNameRecognizer _names = new();
    private static readonly Lazy<HudDigitReader> RankDigits=new(()=>
    {
        HudGlyph[] Load(string file)=>File.Exists(file)?JsonSerializer.Deserialize<HudGlyph[]>(File.ReadAllText(file))??[]:[];
        using var resource=typeof(ResultPlayerRecognizer).Assembly.GetManifestResourceStream("GwentCompanion.Platform.Windows.Vision.Assets.result-rank-digits.json")!;
        return new(Load(Path.Combine(AppContext.BaseDirectory,"vision-assets","score-digits.json"))
            .Concat(Load(Path.Combine(AppContext.BaseDirectory,"vision-assets","rank-digits.json")))
            .Concat(JsonSerializer.Deserialize<HudGlyph[]>(resource)??[]).ToArray());
    });
    private DateTimeOffset _lastRead;
    private readonly FieldVote<int> _userRank=new(),_opponentRank=new();
    private readonly FieldVote<string> _userName=new(),_opponentName=new();
    private NormalizedRegion? _userBadge,_opponentBadge;
    private sealed class FieldVote<T> where T:notnull
    {
        private T? _candidate;private int _votes;
        public T Value {get;private set;}=default!;
        public bool Confirmed {get;private set;}
        public void Observe(T value){_votes=EqualityComparer<T>.Default.Equals(value,_candidate)?_votes+1:1;_candidate=value;if(_votes>=2){Value=value;Confirmed=true;}}
        public void Reset(){_candidate=default;_votes=0;Confirmed=false;Value=default!;}
    }
    private ResultPlayers? ConfirmedPlayers()=>_userRank.Confirmed || _opponentRank.Confirmed || _userName.Confirmed || _opponentName.Confirmed
        ?new(new(_userName.Confirmed?_userName.Value:null,_userRank.Confirmed?_userRank.Value:null),
            new(_opponentName.Confirmed?_opponentName.Value:null,_opponentRank.Confirmed?_opponentRank.Value:null)):null;
    public async Task<GwentVisualObservation> ReadAsync(PixelFrame frame,GwentVisualObservation screen,
        DateTimeOffset at,ScreenStateRecognizer reader)
    {
        if(!PostMatchMmr.IsResultHeader(screen.ScreenHeader) && screen.MatchHudVisible==true && !screen.IsCardSelectionOverlay)Reset();
        if(!screen.FrameGeometrySupported || !PostMatchMmr.IsResultHeader(screen.ScreenHeader))return screen;
        // The settled table can contain an unreadable score. Its NEXT control is an
        // independent layout check: names/ranks must not depend on all six scores.
        if(screen.PostMatchRoundScores is not {Length:>=2} &&
            !PostMatchScoreRecognizer.IsNextControl(await reader.ReadAsync(frame,new(.465,.925,.535,.975)).ConfigureAwait(false)))return screen;
        if(at<=_lastRead || at-_lastRead<TimeSpan.FromMilliseconds(150))return screen with{ResultPlayers=ConfirmedPlayers()};
        _lastRead=at;
        if(!_userRank.Confirmed && await Badge(new(.37,.145,.43,.205),UserRankLane).ConfigureAwait(false) is {} userBadge){_userRank.Observe(userBadge.Value);_userBadge=userBadge.Region;}
        if(!_opponentRank.Confirmed && await Badge(new(.80,.145,.95,.205),OpponentRankLane).ConfigureAwait(false) is {} opponentBadge){_opponentRank.Observe(opponentBadge.Value);_opponentBadge=opponentBadge.Region;}
        // The right badge follows the width of the name/title block. It moves
        // left with short titles and must never be included in the name OCR crop.
        NormalizedRegion NameLane(NormalizedRegion lane,NormalizedRegion? badge)=>new(lane.Left,lane.Top,badge is {} b?Math.Max(lane.Left+.04,b.Left-.012):lane.Right,lane.Bottom);
        if(!_userName.Confirmed && await _names.ReadNameLaneAsync(frame,NameLane(UserNameLane,_userBadge),reader,contextual:true).ConfigureAwait(false) is {} userName)_userName.Observe(userName);
        if(!_opponentName.Confirmed && await _names.ReadNameLaneAsync(frame,NameLane(OpponentNameLane,_opponentBadge),reader,contextual:true).ConfigureAwait(false) is {} opponentName)_opponentName.Observe(opponentName);
        return screen with{ResultPlayers=ConfirmedPlayers()};

        async Task<LocatedHudNumber?> Badge(NormalizedRegion search,NormalizedRegion fallback)
        {
            var found=RankDigits.Value.ReadLocated(frame,search);
            if(found is {Value:>=0 and <=30})return found;
            var lines=await reader.ReadLinesAsync(frame,search,scale:4,enhance:false,whiteLetterMask:true,smooth:true).ConfigureAwait(false);
            var digits=lines.Where(l=>Regex.IsMatch(l.Text.Trim(),@"^[0-9]{1,2}$") && int.TryParse(l.Text.Trim(),out var rank) && rank<=30).ToArray();
            if(digits.Length==1)
            {
                var value=await Rank(digits[0].Region).ConfigureAwait(false);
                if(value is not null)return new(value.Value,digits[0].Region);
            }
            var prior=await Rank(fallback).ConfigureAwait(false);
            return prior is null?null:new(prior.Value,fallback);
        }

        async Task<int?> Rank(NormalizedRegion lane)
        {
            var primary=await ReadRank(lane).ConfigureAwait(false);
            if(primary is not null)return primary;

            // Small layout/capture shifts can clip a shield digit at the edge of the
            // original crop. Retry around this badge only; require both expanded
            // crops to produce the same bounded rank before accepting the fallback.
            var first=await ReadRank(Expand(lane,.004,.006)).ConfigureAwait(false);
            var second=await ReadRank(Expand(lane,.008,.012)).ConfigureAwait(false);
            return first is not null && first==second?first:null;

            async Task<int?> ReadRank(NormalizedRegion crop)
            {
                var glyph=RankDigits.Value.Read(frame,crop);
                if(glyph is <0 or >30)glyph=null;
                var lines=await reader.ReadLinesAsync(frame,crop,scale:4,enhance:false,whiteLetterMask:true,smooth:true).ConfigureAwait(false);
                var values=lines.Select(l=>l.Text.Trim()).Where(t=>Regex.IsMatch(t,@"^[0-9]{1,2}$"))
                    .Select(int.Parse).Where(n=>n is >=0 and <=30).Distinct().ToArray();
                var ocr=values.Length==1?(int?)values[0]:null;
                if(glyph is null && ocr is null)ocr=await reader.ReadRepeatedRankAsync(frame,crop).ConfigureAwait(false);
                return glyph is not null && ocr is not null && glyph!=ocr?null:ocr??glyph;
            }

            static NormalizedRegion Expand(NormalizedRegion region,double horizontal,double vertical)=>new(
                Math.Max(0,region.Left-horizontal),Math.Max(0,region.Top-vertical),
                Math.Min(1,region.Right+horizontal),Math.Min(1,region.Bottom+vertical));
        }
    }
    public void Reset(){_lastRead=default;_userRank.Reset();_opponentRank.Reset();_userName.Reset();_opponentName.Reset();_userBadge=_opponentBadge=null;}
    public void Dispose()=>_names.Dispose();
}
