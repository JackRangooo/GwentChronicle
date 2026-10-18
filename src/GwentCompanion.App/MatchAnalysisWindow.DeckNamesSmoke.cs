using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private static async Task WaitForDeckNames(MatchAnalysisWindow window, Func<bool>? condition = null)
    {
        var deadline=DateTime.UtcNow.AddSeconds(35);
        while(DateTime.UtcNow<deadline)
        {
            if(window.RefreshButton.IsEnabled && window.HistoryList.Items.Cast<HistoryVm>().All(r=>!r.OpponentDeckLabel.Contains("正在匹配")) && (condition?.Invoke()??true))return;
            await Task.Delay(50);
        }
        throw new InvalidOperationException("Deck names did not settle before the deadline.");
    }

    internal static async Task<int> RunDeckNamesSmokeAsync()
    {
        var root=AppContext.BaseDirectory;var output=Path.Combine(root,"diagnostics","deck-names-smoke");Directory.CreateDirectory(output);
        var fixture=Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N"));var cache=Path.Combine(fixture,"cache");Directory.CreateDirectory(cache);
        var matches=Path.Combine(fixture,"match-data");var sessions=Path.Combine(fixture,"diagnostics","sessions");
        var checks=new List<string>();MatchAnalysisWindow? window=null;
        void Check(bool ok,string note){if(!ok)throw new InvalidOperationException(note);checks.Add(note);}
        try
        {
            var liveLibrary=Path.Combine(root,"cache","deck-library.json");var liveHash=SHA256.HashData(File.ReadAllBytes(liveLibrary));
            var catalogPath=Path.Combine(root,"cache","gwent-one-cards.json");var catalog=GwentOneCardCatalog.Load(catalogPath);
            File.Copy(catalogPath,Path.Combine(cache,"gwent-one-cards.json"));
            var source=DeckLibrary.Load(liveLibrary);var group=source.VariationGroups.First(g=>g.Members.Length>1 &&
                source.GroupVariants(g)[0].Deck.Cards.Sum(c=>Math.Min(c.Count,source.GroupVariants(g)[1].Deck.CountOf(c.Card.Id)))>=12);
            var variants=source.GroupVariants(group).Take(2).Select(r=>r.Deck).ToArray();var deck=variants[0];
            var library=new DeckLibrary();library.Merge(variants);library.EnsureVariationGroups(catalog);
            var fixtureGroup=library.VariationGroupFor(deck.Id)!;
            library.RenameVariationGroup(fixtureGroup.Id,"测试卡组簇");library.Save(Path.Combine(cache,"deck-library.json"));
            var unknown=new MatchPlayer(null,null,null,[],[],[]);
            var partial=unknown with {Faction=deck.Faction,Leader=deck.Leader,Observations=deck.Cards.Where(c=>variants[1].CountOf(c.Card.Id)>0).Take(12).Select(c=>new MatchCard(c.Card.Id,Math.Min(c.Count,variants[1].CountOf(c.Card.Id)),
                MatchCardEvidence.Observed,CardProvenance.ConfirmedStartingDeck,240)).ToArray()};
            var raw=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,15),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,
                MatchReviewStore.Reference(deck),partial,[],[],StartedAtUtc:new DateTimeOffset(2026,10,15,2,0,0,TimeSpan.Zero));
            var store=new LocalMatchStore(matches);var candidate=raw;store.Save(candidate);
            var manual=raw with {MatchId=Guid.NewGuid(),StartedAtUtc=raw.StartedAtUtc!.Value.AddMinutes(10)};store.Save(manual);
            var exact=raw with {MatchId=Guid.NewGuid(),StartedAtUtc=raw.StartedAtUtc!.Value.AddMinutes(20),Opponent=MatchReviewStore.Reference(deck)};store.Save(exact);
            var empty=raw with {MatchId=Guid.NewGuid(),StartedAtUtc=raw.StartedAtUtc!.Value.AddMinutes(30),Opponent=unknown};store.Save(empty);
            var hashes=Directory.GetFiles(matches,"*.gvm",SearchOption.AllDirectories).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            var entries=MatchAnalysis.Load(matches,catalog:catalog).Entries;
            void Session(string id,DateTimeOffset start,DateTimeOffset end)
            {
                var folder=Path.Combine(sessions,id);Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder,"session.json"),JsonSerializer.Serialize(new{Started=start,Ended=end}));
            }
            Session("legacy-manual",manual.StartedAtUtc!.Value.AddSeconds(-1),manual.StartedAtUtc.Value.AddMinutes(5));
            Session("ambiguous",candidate.StartedAtUtc!.Value.AddSeconds(-1),empty.StartedAtUtc!.Value.AddMinutes(5));
            var observation=new ObservedDeckRecord("legacy-manual",PlayerSide.Opponent,manual.StartedAtUtc!.Value.AddMinutes(5),deck.Faction,0,null,[],RecognitionVersion:2);
            var ambiguous=observation with {SessionId="ambiguous"};
            var linked=ObservedDeckStore.LinkReviews([observation,ambiguous],entries,sessions);
            Check(linked[0].ReviewMatchKey==MatchReviewStore.Key(manual),"A legacy observation links only to the unique match inside its diagnostic session.");
            Check(linked[1].ReviewMatchKey is null,"An interval containing multiple captures cannot transfer an opponent name to a guessed match.");
            var observedStore=new ObservedDeckStore();observedStore.Save(Path.Combine(cache,"observed-decks"),observation);
            DeckDisplayNames.Configure(Path.Combine(cache,"deck-display-names.json"));DeckDisplayNames.Set("observed:Opponent:legacy-manual","记忆中的控制卡组");
            window=new MatchAnalysisWindow(matches,fixture);window.Show();await window.RefreshAsync();await WaitForDeckNames(window);
            HistoryVm Row(CompactMatch m)=>window.HistoryList.Items.Cast<HistoryVm>().Single(r=>r.Entry.MatchId==m.MatchId);
            Check(Row(candidate).OpponentDeckLabel.Contains("候选①") && Row(candidate).OpponentDeckLabel.Contains("候选②") &&
                Row(candidate).OpponentDeckLabel.Contains("张数覆盖"),"Sufficient partial evidence shows two named candidates and copy coverage directly in history.");
            Check(Row(candidate).OpponentDeckTooltip.Contains("并非整套卡组确认率"),"Candidate coverage cannot be confused with confirmation of the complete deck.");
            Check(Row(manual).OpponentDeckLabel.Contains("记忆中的控制卡组") && Row(manual).OpponentDeckLabel.Contains("手动命名"),"A manually named, uniquely linked opponent observation takes precedence over inferred candidates.");
            Check(Row(exact).OpponentDeckLabel.Contains("测试卡组簇") && !Row(exact).OpponentDeckLabel.Contains("候选"),"Only a saved full reference can display a library name without a candidate label.");
            Check(Row(empty).OpponentDeckLabel=="对手卡组：证据不足，暂不推荐","Missing observations display an honest pending state rather than an arbitrary library deck.");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"history-before-rename.png"),1800,1050);
            DeckDisplayNames.Set(fixtureGroup.Id,"熟悉的社区名称");library.RenameVariationGroup(fixtureGroup.Id,"熟悉的社区名称");library.Save(Path.Combine(cache,"deck-library.json"));
            await WaitForDeckNames(window,()=>Row(candidate).OpponentDeckLabel.Contains("熟悉的社区名称"));
            Check(Row(exact).OpponentDeckLabel.Contains("熟悉的社区名称") && Row(candidate).DeckLabel.Contains("熟悉的社区名称"),"Renaming a cluster updates both opponent and own historical names while history stays open.");
            DeckDisplayNames.Set(deck.Id,"当前变种专属名称");library.Rename(deck.Id,"当前变种专属名称");library.Save(Path.Combine(cache,"deck-library.json"));
            await WaitForDeckNames(window,()=>Row(exact).OpponentDeckLabel.Contains("当前变种专属名称"));
            Check(Row(candidate).OpponentDeckLabel.Contains("当前变种专属名称"),"A variant-specific name overrides its cluster label in current candidates.");
            DeckDisplayNames.Set("observed:Opponent:legacy-manual","补录后改名");
            await WaitForDeckNames(window,()=>Row(manual).OpponentDeckLabel.Contains("补录后改名"));
            Check(Row(manual).OpponentDeckLabel.Contains("补录后改名"),"Observation aliases reload even when the configured alias-file path has not changed.");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"history-after-rename.png"),1800,1050);
            Check(hashes.All(p=>SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)) &&
                SHA256.HashData(File.ReadAllBytes(liveLibrary)).SequenceEqual(liveHash),"Naming and matching preserve all raw captures and the existing installed library.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{window?.Close();DeckDisplayNames.Configure(Path.Combine(root,"cache","deck-display-names.json"));}
    }

    internal static async Task<int> AuditDeckNamesAsync(string report)
    {
        MatchAnalysisWindow? window=null;
        try
        {
            var root=AppContext.BaseDirectory;var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GwentVision","match-data");
            window=new MatchAnalysisWindow(directory,root);await window.RefreshAsync();window.PatchChoice.SelectedIndex=0;await WaitForDeckNames(window);
            DeckBuilderSmoke.Render(window,Path.ChangeExtension(report,"png"),1920,1080);
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=true,Completed=window._completed.Length,
                Records=window.HistoryList.Items.Cast<HistoryVm>().Select(r=>new{r.Entry.MatchId,r.Entry.OpponentFaction,r.Entry.OpponentLeader,OpponentDeck=r.OpponentDeckLabel,Detail=r.OpponentDeckTooltip})},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
        finally{window?.Close();}
    }
}
