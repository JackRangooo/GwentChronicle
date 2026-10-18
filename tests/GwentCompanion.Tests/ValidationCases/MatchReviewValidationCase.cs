using System.Security.Cryptography;
using System.IO;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

internal sealed class MatchReviewValidationCase : IContributorValidationCase
{
    public string Id => "match-review-management";
    public string Kind => "data-integrity";
    public string Summary => "Separate incomplete captures, preserve raw records during edits and deletion, and infer factions only from direct starting-card evidence.";
    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool ok, string message) => ContributorValidationContext.Check(ok,message);
        var directory = context.PathFromRoot("diagnostics", "match-review-tests", Guid.NewGuid().ToString("N"));
        var raw = new LocalMatchStore(directory); var reviews = new MatchReviewStore(directory);
        var unknown = new MatchPlayer(null,null,null,[],[],[]);
        var loss = new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,14),"fixture","rules","14.10",true,1,true,true,
            "DEFEAT",null,null,null,false,null,unknown,unknown with { Faction="Northern Realms",Leader="Mobilization" },[],[]);
        var path = raw.Save(loss); var hash = SHA256.HashData(File.ReadAllBytes(path));
        raw.Save(loss with { MatchId=Guid.NewGuid(), Result=null, ResultObserved=false });
        var initial = MatchAnalysis.Load(directory).Entries;
        Check(initial.Length==2 && MatchAnalysis.Completed(initial).Length==1, "Unknown captures entered completed-game statistics.");
        Check(MatchReviewStore.IsEmptyCapture(loss with { Result=null }) && !MatchReviewStore.IsEmptyCapture(loss), "Completed result was treated as an empty capture.");
        var reference = unknown with { Faction="Syndicate",Leader="Lined Pockets",Reference=[new("card",25,MatchCardEvidence.SelectedReference,CardProvenance.Unknown,255)] };
        Check(MatchAnalysis.DeckKey(unknown) is null && MatchAnalysis.DeckKey(reference) is not null &&
            MatchAnalysis.DeckKey(reference) != MatchAnalysis.DeckKey(reference with { Leader="Jackpot" }),
            "Partial card evidence or a different leader incorrectly identified the saved deck.");
        reviews.Set(loss,new(UserReference:reference,UserDeckName:"控制罪行",IncludeInStatistics:true));
        var edited = MatchAnalysis.Load(directory).Entries.Single(m=>m.MatchId==loss.MatchId);
        Check(edited is { UserFaction:"Syndicate",UserLeader:"Lined Pockets",UserDeckName:"控制罪行",ManuallyEdited:true }, "Confirmed deck identity was not applied locally.");
        var effective = MatchReviewStore.Apply(loss,reviews.Get(loss));
        Check(effective.User.Reference.Sum(c=>c.Copies)==25 && effective.Opponent.Leader=="Mobilization" && effective.Result=="DEFEAT", "A player-deck correction altered unrelated match facts.");
        reviews.Delete(loss); reviews.Set(loss,(reviews.Get(loss)!) with { Note="Changed while deleted" });
        Check(MatchAnalysis.Completed(MatchAnalysis.Load(directory).Entries).Length==0 && reviews.Get(loss)?.Deleted==true, "Editing resurrected a deleted record or left it in statistics.");
        reviews.Restore(loss);
        Check(MatchAnalysis.Completed(MatchAnalysis.Load(directory).Entries).Single().UserFaction=="Syndicate", "Restore lost the saved correction.");
        reviews.Delete(loss); reviews.Reset(loss);
        Check(reviews.Get(loss)?.Deleted==true && reviews.Get(loss)?.UserReference is null, "Resetting corrections unexpectedly restored a deleted record.");
        reviews.Restore(loss); reviews.Reset(loss);
        Check(MatchAnalysis.Load(directory).Entries.Single(m=>m.MatchId==loss.MatchId).UserFaction=="Unknown faction", "Reset did not restore the original metadata.");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))), "Review or deletion mutated the original detector record.");
        reviews.SetOpponentCardCopies(MatchReviewStore.Key(loss),"remembered-bronze",2);
        var memory = MatchReviewStore.Apply(loss,new MatchReviewStore(directory).Get(loss));
        Check(memory.Opponent.Observations.Any(c => c.CardId=="remembered-bronze" && c.Copies==2 && c.Evidence==MatchCardEvidence.Remembered),
            "Remembered copies were lost on reopening or presented as automatic recognition.");
        reviews.Set(loss,new(Note:"Independent identity edit"));
        Check(reviews.Get(loss)?.OpponentCardCopies?.GetValueOrDefault("remembered-bronze")==2,"An identity edit overwrote remembered cards.");
        reviews.SetOpponentCardCopies(MatchReviewStore.Key(loss),"remembered-bronze",1);
        Check(MatchReviewStore.Apply(loss,reviews.Get(loss)).Opponent.Observations.Single(c=>c.CardId=="remembered-bronze").Copies==1,"Removing one bronze copy removed both.");
        reviews.SetOpponentCardCopies(MatchReviewStore.Key(loss),"remembered-bronze",0);
        Check(!MatchReviewStore.Apply(loss,reviews.Get(loss)).Opponent.Observations.Any(c=>c.CardId=="remembered-bronze"),"A remembered card could not be unmarked.");
        Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"Memory toggles altered the original detector record.");
        var rejected = false;
        try { reviews.Set(loss,new(Result:"UNKNOWN",IncludeInStatistics:true)); } catch(ArgumentException) { rejected=true; }
        Check(rejected, "A resultless record could be manually counted as a completed game.");
        var cards = new[] { new CardDefinition("a","A","Syndicate",CardKind.Unit,4), new CardDefinition("b","B","Syndicate",CardKind.Unit,4),
            new CardDefinition("c","C","Syndicate",CardKind.Unit,4), new CardDefinition("n","N","Nilfgaard",CardKind.Unit,4) };
        MatchCard Seen(string id, CardProvenance origin=CardProvenance.ProbableStartingDeck, MatchCardEvidence evidence=MatchCardEvidence.Observed) => new(id,1,evidence,origin,254);
        var player = unknown with { Observations=[Seen("a"),Seen("b"),Seen("c")] };
        Check(MatchIdentityInference.Faction(player,cards)=="Syndicate", "Three agreeing direct starting-card identities failed faction inference.");
        Check(MatchIdentityInference.Faction(player with { Observations=[Seen("a"),Seen("b")] },cards) is null, "Insufficient identity evidence became a faction fact.");
        Check(MatchIdentityInference.Faction(player with { Observations=[Seen("a"),Seen("b"),Seen("c"),Seen("n")] },cards) is null, "Conflicting faction evidence was resolved by majority vote.");
        Check(MatchIdentityInference.Faction(player with { Observations=[Seen("a",evidence:MatchCardEvidence.Inferred),Seen("b"),Seen("c")] },cards) is null,
            "A model hypothesis manufactured a player faction.");
        Check(MatchIdentityInference.Apply(loss with { User=player },cards).User.Reference.Length==0,
            "Faction inference invented a complete player deck.");
        Check(MatchIdentityInference.Faction(player with { Faction="Unknown faction" },cards) is null,
            "Automatic faction inference overruled an explicit unknown identity.");
        Check(MatchIdentityInference.Faction(player,cards.Select(c=>c.Id=="c" ? c with { Kind=CardKind.Leader } : c)) is null,
            "A leader card contributed to the starting-card faction threshold.");
        return Task.CompletedTask;
    }
}
