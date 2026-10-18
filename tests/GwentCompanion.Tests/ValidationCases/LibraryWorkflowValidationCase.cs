using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

internal sealed class LibraryWorkflowValidationCase : IContributorValidationCase
{
    public string Id=>"library-bulk-review-and-history";
    public string Kind=>"data-integrity";
    public string Summary=>"Bulk remembered-card edits preserve raw evidence and metadata; historical ladder charts retain earlier checkpoints without changing the latest calibrated position.";
    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool ok,string why)=>ContributorValidationContext.Check(ok,why);
        var directory=context.PathFromRoot("diagnostics","library-workflow",Guid.NewGuid().ToString("N"));
        var player=new MatchPlayer(null,null,null,[],[],[]);var at=new DateTimeOffset(2026,10,17,0,0,0,TimeSpan.Zero);var id=Guid.NewGuid();
        CompactMatch Game(int minute,string result)=>new(id,Guid.NewGuid(),new(2026,10,17),"fixture","fixture","14.10",false,1,true,true,result,null,null,null,false,null,player,player,[],[],StartedAtUtc:at.AddMinutes(minute));
        var source=Game(1,"VICTORY");var path=new LocalMatchStore(directory).Save(source);var hash=SHA256.HashData(File.ReadAllBytes(path));
        var store=new MatchReviewStore(directory);store.Set(source,new(Result:"DEFEAT",IncludeInStatistics:false,Note:"User officially verified result"));
        store.SetCardCopiesBatch(MatchReviewStore.Key(source),new Dictionary<string,int>{{"a",1},{"b",2}},false);
        Check(store.Get(source) is {Result:"DEFEAT",IncludeInStatistics:false,Note:"User officially verified result"},"Bulk card corrections overwrote verified outcome metadata.");
        store.SetCardCopiesBatch(MatchReviewStore.Key(source),new Dictionary<string,int>{{"b",0},{"c",1}},false);
        Check(store.Get(source)!.OpponentCardCopies!.GetValueOrDefault("a")==1 && store.Get(source)!.OpponentCardCopies!.GetValueOrDefault("b")==0,"A batch lost unrelated corrections or could not remove a card.");
        store.SetCardCopiesBatch(MatchReviewStore.Key(source),new Dictionary<string,int>(),false,replace:true);
        Check(store.Get(source)!.OpponentCardCopies!.Count==0 && store.Get(source)!.Result=="DEFEAT","Undo did not restore precisely the previous card corrections.");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"Bulk card edits altered the original capture.");
        var one=MatchAnalysisEntry.From("one",Game(1,"VICTORY"));var two=MatchAnalysisEntry.From("two",Game(2,"DEFEAT"));var four=MatchAnalysisEntry.From("four",Game(4,"VICTORY"));
        var old=new LadderBaseline(new(6,2),at,"14.10",[]);
        var latest=new LadderBaseline(new(5,1),at.AddMinutes(3),"14.10",[MatchReviewStore.Key(one.InstallationId,one.MatchId),MatchReviewStore.Key(two.InstallationId,two.MatchId)]);
        var settings=new LadderProgressSettings(latest,[old]);var frozen=JsonSerializer.Serialize(settings);
        var now=LadderProgress.Build(latest,[one,two,four]);var history=LadderProgress.History(settings,[one,two,four]);
        Check(history.Points.Length==4 && history.Points[1].Rating==new LadderPosition(6,3).Progress && history.Points[^1].Rating==new LadderPosition(6,3).Progress,"History did not replay each recorded outcome exactly once from the earliest known position.");
        Check(history.Points.All(p=>p.Rating.HasValue),"A later calibration split the outcome-based historical line.");
        Check(now.Current==new LadderPosition(5,2) && LadderProgress.Build(latest,[one,two,four]).Current==now.Current,"Plotting historical checkpoints changed the current calibrated position.");
        Check(JsonSerializer.Serialize(settings)==frozen,"Rendering history mutated checkpoint settings.");
        Check(history.Caption?.Contains("按已记录胜负逐局计算")==true,"Full history did not explain its outcome-based calculation.");
        Check(LadderProgress.History(settings,[one,two,four with{ExactTimeKnown=false,LocalProfile=new(User:new(Rank:3))}]).Points.Length==4,"An approximate timestamp or unrelated badge blocked the user-verified outcome history.");
        return Task.CompletedTask;
    }
}
