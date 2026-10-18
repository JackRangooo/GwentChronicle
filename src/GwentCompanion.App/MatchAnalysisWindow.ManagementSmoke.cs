using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunManagementSmokeAsync()
    {
        var root = AppContext.BaseDirectory; var output = Path.Combine(root,"diagnostics","match-management-smoke");
        Directory.CreateDirectory(output); var checks = new List<string>();
        void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            var catalog = GwentOneCardCatalog.Load(Path.Combine(root,"cache","gwent-one-cards.json"));
            var library = DeckLibrary.Load(Path.Combine(root,"cache","deck-library.json"));
            var deck = library.Decks.FirstOrDefault(d=>d.Name=="控制罪行") ?? library.Decks.First(d=>d.Faction=="Syndicate" && d.CardCount>=25);
            var directory = Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N")); var raw = new LocalMatchStore(directory);
            var unknown = new MatchPlayer(null,null,null,[],[],[]);
            var observations = catalog.Where(c=>c.Faction=="Syndicate" && c.CanBeInStartingDeck &&
                (c.SecondaryFactions is null || c.SecondaryFactions.Count==0)).Take(3)
                .Select(c=>new MatchCard(c.Id,1,MatchCardEvidence.Observed,CardProvenance.ProbableStartingDeck,254)).ToArray();
            var loss = new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,14),"fixture","rules","14.10",true,1,true,true,
                "DEFEAT",null,null,null,false,null,unknown with { Observations=observations },unknown with { Faction="Northern Realms",Leader="Mobilization" },
                [new(1,20,32,true),new(2,30,40,true)],[],StartedAtUtc:new DateTimeOffset(2026,10,14,4,45,0,TimeSpan.Zero));
            var path = raw.Save(loss); var originalHash = SHA256.HashData(File.ReadAllBytes(path));
            for(var n=0;n<4;n++) raw.Save(loss with { MatchId=Guid.NewGuid(),Result=null,ResultObserved=false,User=unknown,Opponent=unknown,Rounds=[],StartedAtUtc=loss.StartedAtUtc!.Value.AddMinutes(-n-1) });
            var initial = MatchAnalysis.Load(directory,catalog:catalog);
            Check(initial.Entries.Length==5 && MatchAnalysis.Completed(initial.Entries).Length==1,"Four empty captures stay outside the completed-game sample");
            Check(initial.Entries.Single(m=>m.MatchId==loss.MatchId) is { UserFaction:"Syndicate",UserFactionInferred:true },"Direct starting-card evidence fills faction without inventing a reference deck");
            var store = new MatchReviewStore(directory);
            store.Set(loss,new(UserReference:MatchReviewStore.Reference(deck),UserDeckName:deck.Name,IncludeInStatistics:true,Note:"Fixture confirmed deck"));
            var window = new MatchAnalysisWindow(directory,root); await window.RefreshAsync();
            Check(window._completed.Length==1 && window._filtered.Length==1,"History defaults to one completed game");
            Check(window._completed[0].UserFaction=="Syndicate" && window._completed[0].UserLeader==deck.Leader,"Confirmed deck corrects the player identity");
            var groups=MatchAnalysis.Breakdown(window._completed,m=>m.OpponentFaction,MatchAnalysis.Factions);
            Check(groups.Single(g=>g.Key=="Northern Realms") is { Games:1,Losses:1,Share:100 },"Northern Realms exposure is 100 percent with one recorded loss");
            var effective=MatchReviewStore.Apply(loss,store.Get(loss));
            Check(effective.User.Reference.Sum(c=>c.Copies)==deck.CardCount,"The selected full deck is present in match details");
            Check(MatchAnalysis.RatingProgress(window._completed,"Syndicate").Points.Length==1,"Faction progress counts the played Syndicate game without inventing MMR");
            void Render(string name) => DeckBuilderSmoke.Render(window,Path.Combine(output,name+".png"),1600,1050);
            Render("history");
            Check(Descendants(window.HistoryList).OfType<Button>().Any(b=>Equals(b.Content,"Edit record")) &&
                Descendants(window.HistoryList).OfType<Button>().Any(b=>Equals(b.Content,"Delete record")),"Real history rows expose edit and delete actions");
            window.RecordScopeChoice.SelectedIndex=1; Render("pending");
            Check(window._filtered.Length==4 && window._completed.Length==1,"Browsing pending captures cannot change the statistical sample");
            window.DisplayChoice.SelectedIndex=1; Render("opponent-share");
            window.SideChoice.SelectedIndex=1; Render("player-share");
            window.DisplayChoice.SelectedIndex=2; Render("statistics");
            var editor = new MatchReviewWindow(loss,effective,store.Get(loss),catalog,library.Decks);
            DeckBuilderSmoke.Render(editor,Path.Combine(output,"editor.png"),620,900);
            window.DisplayChoice.SelectedIndex=0; window.RecordScopeChoice.SelectedIndex=0; Render("history");
            var delete=Descendants(window.HistoryList).OfType<Button>().First(b=>Equals(b.Content,"Delete record"));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitForRefresh(window);
            Check(window._completed.Length==0 && store.Get(loss)?.Deleted==true,"Clicking the real delete button removes the game from statistics");
            window.RecordScopeChoice.SelectedIndex=3; Render("recycle-bin");
            var restore=Descendants(window.HistoryList).OfType<Button>().First(b=>Equals(b.Content,"Restore record"));
            restore.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await WaitForRefresh(window);
            Check(window._completed.Length==1 && store.Get(loss)?.UserDeckName==deck.Name,"Clicking restore returns the game and its saved correction");
            Check(originalHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"Actual UI deletion and restoration preserve the raw detector file");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { Passed=true,Checks=checks,Screenshots=Directory.GetFiles(output,"*.png").Select(Path.GetFileName) },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception error)
        { File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new { Passed=false,Checks=checks,Error=error.ToString() },new JsonSerializerOptions { WriteIndented=true })); return 1; }
    }
    private static async Task WaitForRefresh(MatchAnalysisWindow window)
    {
        var deadline=DateTime.UtcNow.AddSeconds(15);
        while(!window.RefreshButton.IsEnabled && DateTime.UtcNow<deadline) await Task.Delay(25);
        if(!window.RefreshButton.IsEnabled) throw new TimeoutException("Record refresh did not finish.");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject target)
    {
        for(var n=0;n<VisualTreeHelper.GetChildrenCount(target);n++)
        { var child=VisualTreeHelper.GetChild(target,n); yield return child; foreach(var nested in Descendants(child)) yield return nested; }
    }
    internal static int ConfirmPlayedDeck(string[] args)
    {
        var report=args.Last();
        try
        {
            var i=Array.IndexOf(args,"--confirm-match-deck"); var directory=args[i+1]; var matchId=Guid.Parse(args[i+2]); var deckId=args[i+3];
            var entries=MatchAnalysis.Load(directory).Entries.Where(m=>m.MatchId==matchId).ToArray();
            if(entries.Length!=1 || entries[0].Outcome!=MatchOutcome.Loss || entries[0].OpponentFaction!="Northern Realms")
                throw new InvalidOperationException("The expected single loss against Northern Realms was not found.");
            var deck=DeckLibrary.Load(Path.Combine(AppContext.BaseDirectory,"cache","deck-library.json")).Decks.Single(d=>d.Id==deckId);
            var source=LocalMatchStore.Read(entries[0].Path); var hash=SHA256.HashData(File.ReadAllBytes(entries[0].Path));
            var store=new MatchReviewStore(directory);
            store.Set(source,(store.Get(source)??new()) with { UserReference=MatchReviewStore.Reference(deck),UserDeckName=deck.Name,
                UserFaction=deck.Faction,UserLeader=deck.Leader,IncludeInStatistics=true,Note="User confirmed the played deck; original recognition retained." });
            File.WriteAllText(report,JsonSerializer.Serialize(new { Applied=true,Deck=deck.Name,deck.Faction,deck.Leader,Cards=deck.CardCount,
                RawRecordPreserved=hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(entries[0].Path))) },new JsonSerializerOptions { WriteIndented=true }));
            return 0;
        }
        catch(Exception error) { File.WriteAllText(report,error.ToString()); return 1; }
    }

    private sealed record DeckCorrection(Guid MatchId, string DeckId, string ExpectedResult);
    private sealed record ReviewPlan(string MatchDirectory, DeckCorrection[] Games, Guid[] DeleteMatches);
    internal static async Task<int> ApplyReviewPlanAsync(string[] args)
    {
        var report = args.Last();
        try
        {
            var index = Array.IndexOf(args, "--apply-match-review-plan");
            var plan = JsonSerializer.Deserialize<ReviewPlan>(File.ReadAllText(args[index + 1]))
                ?? throw new InvalidDataException("Missing review plan.");
            var loaded = MatchAnalysis.Load(plan.MatchDirectory);
            if (loaded.UnreadableFiles != 0) throw new InvalidDataException("Unreadable records require separate review.");
            var requested = plan.Games.Select(g => g.MatchId).Concat(plan.DeleteMatches).ToArray();
            if (requested.Distinct().Count() != requested.Length) throw new InvalidDataException("Repeated match in plan.");
            var library = DeckLibrary.Load(Path.Combine(AppContext.BaseDirectory, "cache", "deck-library.json"));
            var sources = requested.ToDictionary(id => id, id => LocalMatchStore.Read(loaded.Entries.Single(e => e.MatchId == id).Path));
            var hashes = loaded.Entries.ToDictionary(e => e.Path, e => SHA256.HashData(File.ReadAllBytes(e.Path)));
            var corrections = plan.Games.Select(game =>
            {
                var source = sources[game.MatchId];
                if (source.Result != game.ExpectedResult || MatchAnalysisEntry.ParseOutcome(source.Result) == MatchOutcome.Unknown)
                    throw new InvalidDataException("A game's result does not match its confirmation.");
                var deck = library.Decks.Single(d => d.Id == game.DeckId);
                var reference = MatchReviewStore.Reference(deck);
                if (source.User.Reference.Length > 0 && MatchAnalysis.DeckKey(source.User) != MatchAnalysis.DeckKey(reference))
                    throw new InvalidDataException("Existing frozen deck differs from the confirmed library deck.");
                return (source, deck, reference);
            }).ToArray();
            if (plan.DeleteMatches.Any(id => MatchAnalysisEntry.ParseOutcome(sources[id].Result) != MatchOutcome.Unknown))
                throw new InvalidDataException("Refusing to remove a completed game as a switching artifact.");
            var store = new MatchReviewStore(plan.MatchDirectory);
            foreach (var (source, deck, reference) in corrections)
                store.Set(source, (store.Get(source) ?? new()) with
                {
                    UserReference = source.User.Reference.Length == 0 ? reference : null,
                    UserFaction = deck.Faction, UserLeader = deck.Leader, UserDeckName = deck.Name,
                    IncludeInStatistics = true, Note = "User confirmed the played deck; original recognition and frozen references retained."
                });
            store.Delete(plan.DeleteMatches.Select(id => sources[id]));
            var catalog = GwentOneCardCatalog.Load(Path.Combine(AppContext.BaseDirectory, "cache", "gwent-one-cards.json"));
            var after = MatchAnalysis.Load(plan.MatchDirectory, catalog: catalog);
            var completed = MatchAnalysis.Completed(after.Entries);
            var preserved = hashes.All(item => item.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(item.Key))));
            if (!preserved || completed.Length != plan.Games.Length || after.Entries.Count(e => e.Deleted) != plan.DeleteMatches.Length)
                throw new InvalidDataException("Review plan verification did not match the confirmed sample.");
            var window = new MatchAnalysisWindow(plan.MatchDirectory, AppContext.BaseDirectory);
            await window.RefreshAsync();
            var imageRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(report))!, "confirmed-match-preview");
            Directory.CreateDirectory(imageRoot);
            DeckBuilderSmoke.Render(window, Path.Combine(imageRoot, "history.png"), 1600, 1050);
            window.DisplayChoice.SelectedIndex = 1;
            DeckBuilderSmoke.Render(window, Path.Combine(imageRoot, "opponent-share.png"), 1600, 1050);
            window.SideChoice.SelectedIndex = 1;
            DeckBuilderSmoke.Render(window, Path.Combine(imageRoot, "player-share.png"), 1600, 1050);
            File.WriteAllText(report, JsonSerializer.Serialize(new
            {
                Applied = true, RawRecordsPreserved = preserved, Totals = MatchAnalysis.Totals(completed),
                Deleted = after.Entries.Count(e => e.Deleted), Pending = after.Entries.Count(e => !e.Deleted && !e.IsCompleted),
                Games = completed.Select(e => new { e.MatchId, e.UserDeckName, e.UserFaction, e.UserLeader, e.OpponentFaction, e.OpponentLeader, Outcome = e.Outcome.ToString() }),
                PlayerShare = MatchAnalysis.Breakdown(completed, e => e.UserFaction),
                OpponentShare = MatchAnalysis.Breakdown(completed, e => e.OpponentFaction), PreviewDirectory = imageRoot
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error) { File.WriteAllText(report, error.ToString()); return 1; }
    }
}
