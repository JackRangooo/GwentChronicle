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

public partial class MainWindow
{
    internal static async Task<int> RunLibraryMemorySmokeAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","library-memory-smoke"); Directory.CreateDirectory(output);
        var isolated=Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(isolated);
        var checks=new List<string>(); MainWindow? window=null;
        void Check(bool ok,string message) { if(!ok) throw new InvalidOperationException(message); checks.Add(message); }
        try {
            var catalog=GwentOneCardCatalog.Load(CardDataPath);
            var library=DeckLibrary.Load(LibraryPath);
            var selected=library.Decks.Where(d=>d.Faction=="Syndicate" && d.Leader=="Lined Pockets").ToArray();
            var oldGroups=selected.Select(d=>library.VariationGroupFor(d.Id)!.Id).Distinct().Count();
            var exact=library.Records.Select(r=>r.Fingerprint).Order().ToArray();
            library.ConsolidateVariationGroups(.85,catalog);
            var newGroups=selected.Select(d=>library.VariationGroupFor(d.Id)!.Id).Distinct().Count();
            Check(exact.SequenceEqual(library.Records.Select(r=>r.Fingerprint).Order()),"Clustering retains every exact composition.");
            Check(newGroups<=oldGroups && library.VariationPolicy!.Threshold==.85,"85% grouping is applied to the real library.");
            var metric=new DeckVariationSimilarity(library.VariationPolicy!.Prices);
            Check(library.VariationGroups.All(g=>library.GroupVariants(g).All(a=>library.GroupVariants(g).All(b=>metric.MatchesPolicy(a.Deck,b.Deck,library.VariationPolicy.Metric,.85)))),"Every pair in every cluster meets the threshold and construction rules.");

            var deck=selected.First(d=>d.Cards.Any(c=>c.Count==2));
            var bronze=deck.Cards.First(c=>c.Count==2).Card; var gold=deck.Cards.First(c=>c.Card.IsGold).Card;
            var player=new MatchPlayer(deck.Faction,deck.Leader,null,[new(gold.Id,1,MatchCardEvidence.Observed,CardProvenance.ProbableStartingDeck,230)],[],[]);
            var raw=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,14),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,player,player,[],[]);
            var store=new LocalMatchStore(isolated); var rawPath=store.Save(raw); var rawHash=SHA256.HashData(File.ReadAllBytes(rawPath));
            var baseline=new[]{new ObservedCard(gold,CardProvenance.ProbableStartingDeck,1,DateTimeOffset.MinValue)}.Concat(deck.Cards
                .Where(c=>c.Card.Id!=gold.Id && c.Card.Id!=bronze.Id).Take(11).Select(c=>new ObservedCard(c.Card,CardProvenance.ProbableStartingDeck,1,DateTimeOffset.MinValue,ObservedCopies:c.Count))).ToArray();
            var changed=0; var allowed=true;
            var editor=new OpponentEvidenceEditor(isolated,MatchReviewStore.Key(raw),baseline,deck.Faction,()=>changed++,()=>allowed).WithCatalog(catalog);
            var hit=OpponentDeckMatcher.Match([deck],baseline,deck.Faction,deck.Leader).Matches.Single();
            var preview=new DeckMatchPreviewWindow(hit,FindDataRoot(),editor:editor);
            preview.Show(); await Task.Delay(60);
            // Real routed UI clicks, including bronze 0 -> 1 -> 2 -> 1 -> 0.
            DockPanel RowFor(DependencyObject node) {
                while(node is not DockPanel) node=System.Windows.Media.VisualTreeHelper.GetParent(node) ?? LogicalTreeHelper.GetParent(node) ?? throw new InvalidOperationException("Card row host was not found.");
                return (DockPanel)node;
            }
            var bronzeRow=RowFor(VisualChildren(preview).OfType<TextBlock>().First(t=>t.Text.Contains(UiTranslation.CardName(bronze.Id,bronze.Name)) && t.Text.Contains("×2")));
            void BronzeClick(string prefix) {
                preview.UpdateLayout();
                bronzeRow=RowFor(VisualChildren(preview).OfType<TextBlock>().First(t=>t.Text.Contains(UiTranslation.CardName(bronze.Id,bronze.Name)) && t.Text.Contains("×2")));
                var button=VisualChildren(bronzeRow!).OfType<Button>().First(b=>b.Content is string s && s.StartsWith(prefix));
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,button));
            }
            BronzeClick("1 ·"); Check(editor.Count(bronze.Id)==1,"The unseen button saves one remembered bronze copy.");
            BronzeClick("2 ·"); Check(editor.Count(bronze.Id)==2,"The second bronze button saves exactly two copies.");
            BronzeClick("2 ·"); BronzeClick("1 ·"); Check(editor.Count(bronze.Id)==0,"Seen buttons reverse both remembered bronze copies.");
            BronzeClick("1 ·");
            var reopened=new OpponentEvidenceEditor(isolated,MatchReviewStore.Key(raw),baseline,deck.Faction).WithCatalog(catalog);
            Check(reopened.Count(bronze.Id)==1 && MatchReviewStore.Apply(raw,new MatchReviewStore(isolated).Get(raw)).Opponent.Observations.Any(c=>c.CardId==bronze.Id && c.Evidence==MatchCardEvidence.Remembered),"Reopening retains memory provenance and count.");
            editor.SetCopies(gold,0); Check(editor.Count(gold.Id)==0,"A false automatic sighting can be unmarked without editing the raw record.");
            editor.SetCopies(gold,1); Check(editor.Count(gold.Id)==1,"An unmarked automatic sighting can be restored as a user-confirmed memory.");
            allowed=false; var rejected=false; try {editor.SetCopies(bronze,2);} catch(InvalidOperationException){rejected=true;}
            Check(rejected && reopened.Count(bronze.Id)==1,"A preview captured from another encounter cannot edit the new match."); allowed=true;
            preview.Close();
            var memoryPreview=new DeckMatchPreviewWindow(hit,FindDataRoot(),editor:reopened); memoryPreview.Show();
            DeckBuilderSmoke.Render(memoryPreview,Path.Combine(output,"memory-buttons.png"),850,850); memoryPreview.Close();
            Check(rawHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(rawPath))),"All toggles leave the raw GVM byte-for-byte intact.");
            Check(CommunityArchetypes.All.Count>=25 && MatchAnalysis.Factions.All(f=>CommunityArchetypes.All.Any(a=>a.Faction==f)),"Sourced community names cover all six factions.");
            var own=library.Decks.First(d=>d.Faction=="Northern Realms" && d.CountOf("203198")>0 && d.CountOf("202504")>0);
            var suggestions=CommunityArchetypes.Suggest(own.Faction,own.Leader,own.Cards);
            Check(suggestions.Any(s=>s.Name=="神庙围攻" && s.Qualified && s.Reason.Contains("关键卡")),"Temple and Siege cards produce an explained community-name suggestion.");
            var rename=new DeckRenameWindow("测试名称",own.Name,own.Faction,own.Leader,own.Cards,true);
            rename.Show(); rename.Suggestions.SelectedItem=rename.Suggestions.Items.Cast<CommunityNameSuggestion>().First(s=>s.Name=="神庙围攻");
            Check(rename.DeckName=="神庙围攻" && rename.RenameGroup,"Selecting a community name fills the editable group name.");
            DeckBuilderSmoke.Render(rename,Path.Combine(output,"community-rename.png"),620,650); rename.Close();
            var group=library.VariationGroups.First(g=>g.Members.Length>1); library.RenameVariationGroup(group.Id,"熟悉的卡组簇");
            var isolatedLibrary=Path.Combine(isolated,"library.json"); library.Save(isolatedLibrary);
            Check(DeckLibrary.Load(isolatedLibrary).VariationGroups.Single(g=>g.Id==group.Id) is {Name:"熟悉的卡组簇",CustomName:true},"Cluster rename persists without renaming exact variants.");
            library.ExportTransfer(Path.Combine(isolated,"portable.json"));
            Check(new DeckLibrary().PreviewTransfer(Path.Combine(isolated,"portable.json")).Library.VariationGroups.Single(g=>g.Id==group.Id).CustomName,"Portable libraries retain custom cluster names.");

            window=new MainWindow(); window.Loaded-=window.OnLoaded; window._matchStorageDirectoryOverride=isolated;
            window._candidateCatalog=catalog; window._library=library; window._cachedDecks=library.Decks; window._libraryReady=true;
            DeckDisplayNames.ConfigureGroups(library);
            DeckDisplayNames.Configure(Path.Combine(isolated,"aliases.json"));
            window.HideLoadingShell(); window.ShowPage(UiPage.Library); window.Show(); window.SetExpandedWorkspace(true); window.Width=1320; window.Height=950;
            window._expandedVariationGroups.Add(group.Id); window.SetDeckListItems(window.CreateDeckItems(window._cachedDecks));
            window.DeckSearchBox.Text="熟悉的卡组簇";
            var item=window.DeckList.Items.Cast<DeckListItem>().Single(i=>i.GroupId==group.Id); window.DeckList.SelectedItem=item;
            window.DeckList.ScrollIntoView(item); window.UpdateWorkspaceLayout(1320);
            await Task.Delay(100); window.UpdateLayout();
            DeckBuilderSmoke.Render(window,Path.Combine(output,"expanded-variants.png"),1320,950);
            var expanded=VisualChildren(window.DeckList).OfType<Expander>().First(e=>e.DataContext is DeckListItem i && i.GroupId==group.Id);
            Check(expanded.IsExpanded && VisualChildren(expanded).OfType<Button>().Count(b=>b.DataContext is DeckListItem)>=item.Variations!.Length,"Expanded variants are rendered as visible clickable rows.");
            Check(item.HasVariations && item.VariantsExpanded,"The real library exposes an expandable variant list.");
            var last=item.Variations!.Last(); window.VariantPreview_OnClick(new Button{DataContext=last},new RoutedEventArgs());
            Check(((DeckListItem)window.DeckList.SelectedItem).Deck!.Id==last.Deck!.Id,"Clicking an expanded variant switches the exact preview.");
            window._matchIdentity=new InstallationIdentity(1,Guid.NewGuid(),DateTimeOffset.UtcNow);
            window._matchAcquisition=new MatchAcquisition("fixture",window._matchIdentity.Id);
            var oldContext=window.CurrentReviewKey; var sameEncounter=window.CurrentEncounterId;
            Check(window.ProjectionContextCurrent(sameEncounter,oldContext),"The captured projection context belongs to its original match.");
            window._matchAcquisition=new MatchAcquisition("fixture",window._matchIdentity.Id);
            Check(!window.ProjectionContextCurrent(sameEncounter,oldContext),"A delayed projection cannot bind old memories to a new UUID in the same diagnostic session.");
            window._matchAcquisition=null;
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks,CommunityNames=CommunityArchetypes.All.Count,
                LibraryDecks=library.Records.Count,LibraryClusters=library.VariationGroups.Count,LinedPocketsLists=selected.Length,LinedPocketsBefore=oldGroups,LinedPocketsAfter=newGroups,Changed=changed},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        } catch(Exception error) {
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true})); return 1;
        } finally { if(window is not null) {window._projectionQueue?.Dispose();window.Close();} DeckDisplayNames.Configure(Path.Combine(FindDataRoot(),"cache","deck-display-names.json")); }
    }
}
