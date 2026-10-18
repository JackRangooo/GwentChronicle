using System.Text.Json;

namespace GwentCompanion.Core.Data;

public sealed record LadderPosition(int Rank,int Fragments)
{
    public int Progress => (7-Rank)*6+Fragments;
    public bool IsPro => Rank==0;
}
public sealed record LadderBaseline(LadderPosition Position,DateTimeOffset AtUtc,string? Patch,string[] ExcludedMatches);
public sealed record LadderProgressSettings(LadderBaseline Baseline,LadderBaseline[] PreviousCalibrations);
public sealed record LadderGameProgress(string MatchKey,LadderPosition Before,LadderPosition After,MatchOutcome Outcome);
public sealed record LadderProgressSnapshot(LadderPosition Current,LadderGameProgress[] Games,bool NeedsCalibration,
    string? Reason,MatchRatingSeries Series);

public static class LadderProgress
{
    public static void Validate(LadderPosition position)
    {
        if(position.Rank is <0 or >7 || position.Fragments is <0 or >5 || position.IsPro && position.Fragments!=0)
            throw new ArgumentException("Use rank 1–7 and pieces 0–5, or Pro Rank 0 with 0 pieces.");
    }
    public static LadderPosition Step(LadderPosition before,MatchOutcome outcome)
    {
        Validate(before);
        if(before.IsPro || outcome is MatchOutcome.Unknown or MatchOutcome.Draw)return before;
        if(outcome==MatchOutcome.Loss)return before with{Fragments=Math.Max(0,before.Fragments-1)};
        return before.Fragments==5?new(before.Rank-1,0):before with{Fragments=before.Fragments+1};
    }
    public static LadderProgressSnapshot Build(LadderBaseline baseline,IEnumerable<MatchAnalysisEntry> entries)
    {
        Validate(baseline.Position);var current=baseline.Position;var games=new List<LadderGameProgress>();
        var points=new List<MatchRatingPoint>{new(0,current.Progress,DateOnly.FromDateTime(baseline.AtUtc.UtcDateTime),baseline.Patch??"",Detail(current))};
        var excluded=baseline.ExcludedMatches.ToHashSet(StringComparer.Ordinal);var reason=(string?)null;
        // A checkpoint already includes earlier results, even if a missed game is entered later.
        var baselineDate=DateOnly.FromDateTime(baseline.AtUtc.UtcDateTime);
        var future=entries.Where(m=>m.CountsForLadder && (m.StartedAtUtc is { } started ? started > baseline.AtUtc : m.Date >= baselineDate) && !excluded.Contains(MatchReviewStore.Key(m.InstallationId,m.MatchId)))
            .OrderBy(m=>m.Date).ThenBy(m=>m.StartedAtUtc).ToArray();
        if(future.Any(m=>!m.StartedAtUtc.HasValue || !m.ExactTimeKnown) || future.GroupBy(m=>m.StartedAtUtc).Any(g=>g.Count()>1))
            reason="Some new games have no reliable order. Calibrate before continuing ladder estimates.";
        else foreach(var match in future)
        {
            if(baseline.Patch is not null && match.Patch!=baseline.Patch || match.Date.Month!=baseline.AtUtc.UtcDateTime.Month || match.Date.Year!=baseline.AtUtc.UtcDateTime.Year)
            {reason="The patch or season changed. Calibrate your ladder position in the game.";break;}
            if(current.IsPro)
            {
                if(match.EffectiveRank is >0){reason="The visible rank differs from the estimate. Check for missed or casual games and calibrate.";break;}
                continue;
            }
            var after=Step(current,match.Outcome);
            var observed=match.EffectiveRank;
            if(match.Rank.HasValue && match.LocalProfile?.User?.Rank is null && match.Rank!=after.Rank)
            {reason="The visible rank differs from the estimate. Check for missed or casual games and calibrate.";break;}
            if(observed.HasValue && observed!=current.Rank && observed!=after.Rank)
            {reason="The visible rank differs from the estimate. Check for missed or casual games and calibrate.";break;}
            if(after.IsPro && match.Rank!=0 && observed!=0)
            {reason="Pro Rank promotion needs confirmation in the game. Accept its rules and calibrate when promoted.";break;}
            games.Add(new(MatchReviewStore.Key(match.InstallationId,match.MatchId),current,after,match.Outcome));current=after;
            points.Add(new(games.Count,current.Progress,match.Date,match.Patch,Detail(current)));
        }
        return new(current,games.ToArray(),reason is not null,reason,new("Ladder rank and pieces",points.ToArray(),0,MatchProgressMetric.LadderPieces));
    }
    private static string Detail(LadderPosition value)=>value.IsPro?"Pro Rank":$"Rank {value.Rank} · {value.Fragments}/5 pieces";
    /// <summary>Replay recorded outcomes continuously from the earliest known position in this season. Later calibrations do not split or replace history.</summary>
    public static MatchRatingSeries History(LadderProgressSettings settings,IEnumerable<MatchAnalysisEntry> entries)
    {
        var latest=settings.Baseline;
        var anchor=settings.PreviousCalibrations.Append(latest).Where(b=>b.AtUtc<=latest.AtUtc && b.Patch==latest.Patch &&
            b.AtUtc.Year==latest.AtUtc.Year && b.AtUtc.Month==latest.AtUtc.Month).OrderBy(b=>b.AtUtc).First();
        var excluded=anchor.ExcludedMatches.ToHashSet(StringComparer.Ordinal);var date=DateOnly.FromDateTime(anchor.AtUtc.UtcDateTime);
        var ordered=entries.Where(e=>e.CountsForLadder && !excluded.Contains(MatchReviewStore.Key(e.InstallationId,e.MatchId)) &&
            (e.StartedAtUtc is {} at?at>anchor.AtUtc:e.Date>=date) && (anchor.Patch is null || e.Patch==anchor.Patch) &&
            e.Date.Year==date.Year && e.Date.Month==date.Month).OrderBy(e=>e.Date).ThenBy(e=>e.StartedAtUtc??new DateTimeOffset(e.Date.ToDateTime(TimeOnly.MaxValue),TimeSpan.Zero))
            .ThenBy(e=>e.MatchId).ToArray();
        var current=anchor.Position;
        var points=new List<MatchRatingPoint>{new(0,current.Progress,date,anchor.Patch??"","起点 · "+Detail(current))};
        foreach(var entry in ordered)
        {
            current=Step(current,entry.Outcome);
            var outcome=entry.Outcome switch{MatchOutcome.Win=>"胜利",MatchOutcome.Loss=>"失败",_=>"平局"};
            points.Add(new(points.Count,current.Progress,entry.Date,entry.Patch,(entry.Abnormal?"异常计分 · ":"")+outcome+" · "+Detail(current)));
        }
        return new("Ladder rank and pieces",points.ToArray(),0,MatchProgressMetric.LadderPieces,
            $"按已记录胜负逐局计算 · {ordered.Length} 场排位 · 从 {anchor.AtUtc.ToLocalTime():MM-dd HH:mm} 的已确认段位起连续记录");
    }
}

/// <summary>One explicit current-position checkpoint; match IDs make replay/refresh idempotent and preserve old records.</summary>
public sealed class LadderProgressStore(string directory)
{
    public string PathName=>Path.Combine(Path.GetFullPath(directory),"ladder-progress.json");
    private static readonly JsonSerializerOptions Json=new(){WriteIndented=true};
    public LadderProgressSettings? Load()=>File.Exists(PathName)?JsonSerializer.Deserialize<LadderProgressSettings>(File.ReadAllText(PathName),Json):null;
    public LadderProgressSettings Calibrate(LadderPosition position,IEnumerable<MatchAnalysisEntry> entries,DateTimeOffset? at=null)
    {
        LadderProgress.Validate(position);Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
        using var gate=new FileStream(PathName+".lock",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
        var records=entries.ToArray();var previous=Load();
        var baseline=new LadderBaseline(position,at??DateTimeOffset.UtcNow,records.OrderByDescending(m=>m.StartedAtUtc).Select(m=>m.Patch).FirstOrDefault(),
            records.Select(m=>MatchReviewStore.Key(m.InstallationId,m.MatchId)).Distinct().ToArray());
        var state=new LadderProgressSettings(baseline,previous is null?[]:previous.PreviousCalibrations.Append(previous.Baseline).ToArray());
        var temporary=PathName+".tmp-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var file=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {JsonSerializer.Serialize(file,state,Json);file.Flush(true);}
            if(File.Exists(PathName))File.Copy(PathName,PathName+".bak",true);File.Move(temporary,PathName,true);
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
        return state;
    }
}
