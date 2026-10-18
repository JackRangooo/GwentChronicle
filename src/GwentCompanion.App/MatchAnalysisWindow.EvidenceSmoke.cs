using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunEvidenceSmokeAsync()
    {
        var root=AppContext.BaseDirectory;var output=Path.Combine(root,"diagnostics","evidence-first-smoke");Directory.CreateDirectory(output);
        var fixture=Path.Combine(output,Guid.NewGuid().ToString("N"));var cache=Path.Combine(fixture,"cache");Directory.CreateDirectory(cache);
        var checks=new List<string>();MatchAnalysisWindow? window=null;MatchEvidenceWindow? editorWindow=null;
        void Check(bool ok,string note){if(!ok)throw new InvalidOperationException(note);checks.Add(note);}
        try
        {
            var catalogPath=Path.Combine(root,"cache","gwent-one-cards.json");File.Copy(catalogPath,Path.Combine(cache,"gwent-one-cards.json"));
            var catalog=GwentOneCardCatalog.Load(catalogPath);var librarySource=DeckLibrary.Load(Path.Combine(root,"cache","deck-library.json"));
            var deck=librarySource.Decks.First(d=>d.Faction=="Syndicate" && d.Cards.Select(c=>c.Card.Id).Distinct().Count()>=16);
            var library=new DeckLibrary();library.Merge([deck]);library.Save(Path.Combine(cache,"deck-library.json"));
            var foreign=catalog.First(c=>StartingDeckRules.IsLegalStartingCard(c,deck.Faction) && deck.CountOf(c.Id)==0);
            var observed=deck.Cards.Take(12).Select(c=>new MatchCard(c.Card.Id,1,MatchCardEvidence.Observed,CardProvenance.ConfirmedStartingDeck,240))
                .Append(new(foreign.Id,1,MatchCardEvidence.Observed,CardProvenance.ConfirmedStartingDeck,240)).ToArray();
            var guesses=deck.Cards.Skip(12).Take(2).Select(c=>new MatchCard(c.Card.Id,1,MatchCardEvidence.Inferred,CardProvenance.Unknown,180)).ToArray();
            var own=MatchReviewStore.Reference(deck) with{Observations=observed.Take(4).ToArray()};
            var opponent=new MatchPlayer(deck.Faction,deck.Leader,null,observed,[],guesses);
            var raw=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,15),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,
                own,opponent,[new(1,36,56,true),new(2,4,0,true),new(3,51,133,true)],[],StartedAtUtc:new DateTimeOffset(2026,10,15,6,0,0,TimeSpan.Zero));
            var directory=Path.Combine(fixture,"matches");var store=new LocalMatchStore(directory);var path=store.Save(raw);
            var sparse=raw with{MatchId=Guid.NewGuid(),StartedAtUtc=raw.StartedAtUtc!.Value.AddMinutes(-20),Opponent=opponent with{Observations=observed.Take(1).ToArray()},Result=null,Rounds=[new(1,12,30,false)]};
            var sparsePath=store.Save(sparse);var hashes=new[]{path,sparsePath}.ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            window=new MatchAnalysisWindow(directory,fixture){WindowState=WindowState.Normal,Width=1800,Height=1050};window.Show();await window.RefreshAsync();await WaitForDeckNames(window);
            var expanded=await ExpandFirstResult(window);
            DetailsVm Details()=>((ContentControl)expanded.Content).Content as DetailsVm??throw new InvalidOperationException("Details missing");
            var details=Details();
            Check(details.Other.Groups.SelectMany(g=>g.Cards).Count()==13 && details.Other.Groups.SelectMany(g=>g.Cards).Any(c=>c.Card?.Id==foreign.Id),"An observed card absent from every candidate remains in the primary evidence journal.");
            Check(details.Other.Groups.SelectMany(g=>g.Cards).All(c=>c.Badge=="已识别" && c.RowOpacity==1),"Every actual sighting has an explicit, unfaded evidence badge.");
            Check(details.Mine.Groups[0].Cards.Length==4 && details.Mine.Supplements.Single().Cards.Length==deck.Cards.Count,
                "A chosen full reference never replaces the four actual player sightings.");
            Check(details.Other.Supplements.Single().Cards.All(c=>c.Badge=="预测 ?") && details.Candidates.Matches.All(m=>m.ObservedCoveragePercent>=80),
                "Guesses are separate supplements and recommendations pass the copy-coverage threshold.");
            var prediction=Descendants(expanded).OfType<Expander>().Single(e=>e.Header is string s && s.StartsWith("展开卡组预测"));
            Check(!prediction.IsExpanded && Descendants(expanded).OfType<Expander>().Where(e=>e.DataContext is CardGroupVm).All(e=>!e.IsExpanded),"Both predictions and saved references are collapsed by default.");
            Check(Descendants(expanded).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.IsVisible && t.Text=="已识别"),"Source badges are rendered visibly beside card names.");
            var editButtons=Descendants(expanded).OfType<Button>().Where(b=>Equals(b.Content,"补录／校对卡牌")).ToArray();
            Check(editButtons.Length==2 && editButtons.All(b=>b.IsEnabled),"Both players have a visible evidence-edit action independent of recommendations.");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"evidence-journal.png"),1800,1050);
            var historyScroll=Descendants(window.HistoryView).OfType<ScrollViewer>().First();historyScroll.ScrollToVerticalOffset(320);window.UpdateLayout();
            DeckBuilderSmoke.Render(window,Path.Combine(output,"evidence-cards.png"),1800,1050);
            editButtons.Single(b=>b.DataContext is DeckVm d && d.Editor?.Side==PlayerSide.Opponent).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            editorWindow=window.OwnedWindows.OfType<MatchEvidenceWindow>().Single();
            var add=catalog.First(c=>StartingDeckRules.IsLegalStartingCard(c,deck.Faction) && observed.All(o=>o.CardId!=c.Id));
            editorWindow.Search.Text=UiTranslation.CardName(add.Id,add.Name);editorWindow.UpdateLayout();
            Check(Descendants(editorWindow.Rows).OfType<Button>().Any(b=>b.Tag is ValueTuple<CardDefinition,int> t && t.Item1.Id==add.Id),"Chinese card-name search includes legal cards outside candidate lists.");
            var plus=Descendants(editorWindow.Rows).OfType<Button>().Single(b=>Equals(b.Content,"＋") && b.Tag is ValueTuple<CardDefinition,int> t && t.Item1.Id==add.Id);
            plus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline=DateTime.UtcNow.AddSeconds(25);
            while((expanded.Content is not ContentControl{Content:DetailsVm d} || !d.Other.Groups.SelectMany(g=>g.Cards).Any(c=>c.Card?.Id==add.Id && c.Badge=="记忆补录")) && DateTime.UtcNow<deadline)await Task.Delay(30);
            Check(Details().Other.Groups.SelectMany(g=>g.Cards).Any(c=>c.Card?.Id==add.Id && c.Badge=="记忆补录"),"Clicking the independent editor adds a remembered card to the actual journal.");
            DeckBuilderSmoke.Render(editorWindow,Path.Combine(output,"direct-memory-editor.png"),860,800);
            var minus=Descendants(editorWindow.Rows).OfType<Button>().Single(b=>Equals(b.Content,"−") && b.Tag is ValueTuple<CardDefinition,int> t && t.Item1.Id==add.Id);
            minus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));editorWindow.Close();editorWindow=null;
            var reviews=new MatchReviewStore(directory);
            Check(reviews.Get(raw)?.OpponentCardCopies?.GetValueOrDefault(add.Id)==0,"The same editor retracts a memory without deleting the original detector capture.");
            var corrected=raw with{MatchId=Guid.NewGuid()};
            reviews.Set(corrected,new(OpponentFaction:"Monsters"));
            var correctedEffective=MatchReviewStore.Apply(corrected,reviews.Get(corrected));
            var correctedEditor=window.EvidenceEditor(MatchReviewStore.Key(corrected),corrected.Opponent,correctedEffective.Opponent,false,()=>{});
            var monster=catalog.First(c=>c.Faction=="Monsters" && StartingDeckRules.IsLegalStartingCard(c,"Monsters"));
            correctedEditor.SetCopies(monster,1);
            Check(reviews.Get(corrected)?.OpponentCardCopies?.GetValueOrDefault(monster.Id)==1,
                "A faction correction also changes evidence-editor validation while retaining the raw observation baseline.");
            var userEditor=details.Mine.Editor!;userEditor.SetCopies(add,1);
            var effective=MatchReviewStore.Apply(raw,reviews.Get(raw));
            Check(effective.User.Observations.Any(c=>c.CardId==add.Id && c.Evidence==MatchCardEvidence.Remembered) &&
                !effective.Opponent.Observations.Any(c=>c.CardId==add.Id),"Own and opponent corrections persist independently under one match UUID.");
            var rejected=false;
            try{ApplyInterruptedResult(directory,sparse,[new(1,36,56,true)],"Fixture","Opponent",6,6);}
            catch(ArgumentException){rejected=true;}
            Check(rejected && reviews.Get(sparse)?.Result is null && new LocalMatchProfileStore(directory).Get(sparse) is null,
                "Invalid settlement data cannot mark an interrupted record complete or save a partial profile.");
            ApplyInterruptedResult(directory,sparse,[new(1,36,56,true),new(2,4,0,true),new(3,51,133,true)],"Fixture","Opponent",6,6);
            var repaired=MatchAnalysis.Load(directory,catalog:catalog).Entries.Single(e=>e.MatchId==sparse.MatchId);
            Check(repaired.IsCompleted && repaired.CaptureNote is not null && repaired.LocalProfile?.ResultRoundScores?.Length==3,
                "An interrupted capture can retain its sparse journal while supplied settlement data completes the match.");
            var sparseEffective=MatchReviewStore.Apply(sparse,reviews.Get(sparse));
            var suppressed=OpponentDeckMatcher.Match([deck],sparseEffective.Opponent,catalog);
            Check(!suppressed.HasMatches && suppressed.TotalObservedCopies==1,"Repairing a result does not unlock a recommendation from one detected card or stored guesses.");
            var sparseView=window.Deck("partial",sparseEffective.Opponent,details.Other.Editor,suppressed.HasMatches);
            Check(sparseView.Groups[0].Cards.Length==1 && sparseView.Supplements.Length==0,"Low-evidence records cannot expose a guessed 25-card list as the recorded deck.");
            reviews.Set(raw,new(Result:"DEFEAT",IncludeInStatistics:true));
            Check(reviews.Get(raw)?.UserCardCopies?.GetValueOrDefault(add.Id)==1 && reviews.Get(raw)?.OpponentCardCopies?.ContainsKey(add.Id)==true,
                "Editing result/deck fields preserves both sides' memory corrections.");
            Check(hashes.All(p=>SHA256.HashData(File.ReadAllBytes(p.Key)).SequenceEqual(p.Value)),"Evidence editing and result repair preserve every raw match byte.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{editorWindow?.Close();window?.Close();DeckDisplayNames.Configure(Path.Combine(root,"cache","deck-display-names.json"));}
    }
}
