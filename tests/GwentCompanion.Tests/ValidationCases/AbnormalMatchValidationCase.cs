using System.IO;
using System.Security.Cryptography;
using GwentCompanion.Core.Data;

internal sealed class AbnormalMatchValidationCase : IContributorValidationCase
{
    public string Id => "abnormal-match-journal";
    public string Kind => "data-integrity";
    public string Summary => "Abnormal losses affect ladder pieces without polluting win rates; manual journal entries survive edits, deletion, restoration and restart.";
    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool ok, string why) => ContributorValidationContext.Check(ok, why);
        var directory = context.PathFromRoot("diagnostics", "abnormal-tests", Guid.NewGuid().ToString("N"));
        var at = new DateTimeOffset(2026,10,16,1,0,0,TimeSpan.Zero); var installation = Guid.NewGuid();
        var player = new MatchPlayer("Syndicate","Lined Pockets",null,[],[],[]);
        CompactMatch Game(int n, string result = "VICTORY") => new(installation,Guid.NewGuid(),new(2026,10,16),"fixture","fixture","14.10",false,1,true,true,result,
            null,null,null,false,null,player,player,[],[],StartedAtUtc:at.AddMinutes(n));
        var raw = new LocalMatchStore(directory); var win = Game(1); var winPath = raw.Save(win);
        var rawHash = SHA256.HashData(File.ReadAllBytes(winPath)); var manual = new ManualMatchStore(directory);
        var journal = new ManualMatchRecord(installation,Guid.NewGuid(),at.AddMinutes(2),"14.10");
        var manualPath = manual.Save(journal);
        var baseline = new LadderBaseline(new(6,2),at,"14.10",[]);
        MatchAnalysisEntry[] Load() => MatchAnalysis.Load(directory).Entries;
        var entries = Load(); var abnormal = entries.Single(m => m.ManualRecord);
        Check(abnormal.Abnormal && !abnormal.IsCompleted && abnormal.CountsForLadder && abnormal.Outcome == MatchOutcome.Loss,
            "A manual loss did not separate ladder eligibility from win-rate eligibility.");
        Check(!manualPath.EndsWith(".gvm") && ManualMatchStore.IsManual(LocalMatchStore.Read(manualPath)), "Manual provenance was lost.");
        Check(MatchAnalysis.Totals(entries) is { Games:1,Wins:1,Losses:0,WinRate:100 }, "Abnormal loss changed the global win rate.");
        Check(MatchAnalysis.Breakdown(entries,m => m.UserFaction).Single().Share == 100 &&
            MatchAnalysis.Breakdown(entries,m => m.UserFaction).Single() is { Games:1,Losses:0,WinRate:100 }, "Abnormal record polluted faction/deck sample denominators.");
        var progress = LadderProgress.Build(baseline,entries);
        Check(progress is { Current.Rank:6,Current.Fragments:2,Games.Length:2,NeedsCalibration:false }, "Win plus abnormal loss did not replay as +1 then -1.");
        var lossOnly = LadderProgress.Build(baseline,[abnormal]);
        Check(lossOnly is { Current.Fragments:1,Games.Length:1 }, "Abnormal loss did not remove exactly one fragment.");
        Check(LadderProgress.Build(baseline with {Position=new(6,0)},[abnormal]).Current == new LadderPosition(6,0), "Abnormal loss dropped below zero pieces.");
        Check(LadderProgress.Build(baseline,Load()).Current == progress.Current, "Restart or refresh applied a result twice.");
        Check(LadderProgress.Build(baseline,[abnormal with { StartedAtUtc=null,Date=new(2026,10,15) }]) is {Games.Length:0,NeedsCalibration:false},
            "An old record with a date before calibration but no exact timestamp blocked current progress.");
        Check(LadderProgress.Build(baseline,[abnormal with { StartedAtUtc=null }]).NeedsCalibration,
            "A same-day undated match was silently assigned an invented order.");
        var reviews = new MatchReviewStore(directory); var source = LocalMatchStore.Read(manualPath);
        reviews.Delete(source);
        Check(LadderProgress.Build(baseline,Load()).Current == new LadderPosition(6,3), "Deleting the abnormal entry did not undo its ladder contribution.");
        reviews.Restore(source);
        Check(LadderProgress.Build(baseline,Load()).Current == progress.Current, "Restoring an abnormal entry did not restore exactly one ladder loss.");
        manual.Save(journal with { OccurredAtUtc=at.AddMinutes(-1) });
        Check(Load().Length==2 && LadderProgress.Build(baseline,Load()).Current==new LadderPosition(6,3), "Editing time duplicated the entry or counted a pre-calibration loss.");
        manual.Save(journal);
        reviews.Set(source,new(IncludeInStatistics:true));
        Check(!Load().Single(m => m.ManualRecord).IsCompleted && reviews.Get(source)?.IncludeInStatistics==false, "An edit converted manual data into a statistical match.");
        var normalLoss=Game(3,"DEFEAT"); var lossPath=raw.Save(normalLoss); var lossHash=SHA256.HashData(File.ReadAllBytes(lossPath));
        reviews.Set(normalLoss,new(Abnormal:true,AbnormalReason:"网络掉线",IncludeInStatistics:true));
        var classified=Load().Single(m=>m.MatchId==normalLoss.MatchId);
        Check(classified is {Abnormal:true,IsCompleted:false,CountsForLadder:true}, "An existing record could not be classified as abnormal independently of statistics.");
        reviews.Set(normalLoss,new(Abnormal:false,IncludeInStatistics:true));
        Check(MatchAnalysis.Completed(Load()).Length==2, "Reclassifying a normal game did not restore statistical eligibility.");
        reviews.Set(normalLoss,new(IncludeInStatistics:false));
        Check(!Load().Single(m=>m.MatchId==normalLoss.MatchId).CountsForLadder, "Legacy excluded records were unexpectedly added to the ladder.");
        new LocalMatchProfileStore(directory).Set(source,new(CountForLadder:false));
        Check(!Load().Single(m=>m.ManualRecord).CountsForLadder, "Explicit ladder exclusion was ignored.");
        var rejected=false; try { raw.Save(source); } catch(ArgumentException) { rejected=true; }
        Check(rejected && Directory.GetFiles(directory,"*.gvm",SearchOption.AllDirectories).Length==2, "Manual data was written as a detector upload candidate.");
        Check(rawHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(winPath))) && lossHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(lossPath))), "Manual edits changed immutable detector files.");
        File.WriteAllText(Path.Combine(directory,"broken"+ManualMatchStore.Extension),"{ broken");
        Check(MatchAnalysis.Load(directory).UnreadableFiles==1 && Load().Length==3, "One damaged manual entry hid valid history.");
        var recovered = journal with { MatchId=Guid.NewGuid(), Abnormal=false, Result="VICTORY", User=player, ExactTimeKnown=false };
        var recoveredPath=manual.Save(recovered);var recoveredEntry=Load().Single(m=>m.MatchId==recovered.MatchId);
        Check(recoveredEntry is {ManualRecord:true,Abnormal:false,IsCompleted:true,ExactTimeKnown:false},
            "A user-confirmed recovered win was mislabeled as an abnormal loss or excluded from win rate.");
        Check(LadderProgress.Build(baseline,[recoveredEntry]).NeedsCalibration,
            "A recovered game with unknown exact time was given an invented ladder order.");
        rejected=false;try{raw.Save(LocalMatchStore.Read(recoveredPath));}catch(ArgumentException){rejected=true;}
        Check(rejected,"A recovered user-confirmed game was written as an automatically detected upload candidate.");
        return Task.CompletedTask;
    }
}
