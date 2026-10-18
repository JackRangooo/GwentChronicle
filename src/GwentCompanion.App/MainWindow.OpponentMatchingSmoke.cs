using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunOpponentMatchingSmokeAsync()
    {
        var output = Path.Combine(AppContext.BaseDirectory,"diagnostics","opponent-matching-smoke");
        Directory.CreateDirectory(output); var checks = new List<string>(); MainWindow? window = null;
        void Check(bool ok,string message) { if (!ok) throw new InvalidOperationException(message); checks.Add(message); }
        try
        {
            window = new MainWindow(); window.Loaded -= window.OnLoaded;
            window.WindowState = WindowState.Normal; window.Width=1320; window.Height=950; window.HideLoadingShell(); window.Show();
            window._candidateCatalog = GwentOneCardCatalog.Load(CardDataPath);
            window._library = DeckLibrary.Load(LibraryPath); window._cachedDecks = window._library.Decks.Select(window.CurrentDeck).ToArray();
            window._useOpponentModel = false;
            var shupe = window._candidateCatalog.Single(c => UiTranslation.CardName(c.Id,c.Name)=="店店的大冒险");
            window.MarkCandidateSeen(shupe,1); window.RenderLiveInference();
            await window._projectionQueue!.Idle.WaitAsync(TimeSpan.FromSeconds(30));
            var seen = ((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).Single(r=>r.Slot.Card?.Id==shupe.Id);
            window.OpponentSlot_OnActivated(null,seen); await window._projectionQueue.Idle.WaitAsync(TimeSpan.FromSeconds(30));
            Check(!((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).Any(r=>r.Slot.Card?.Id==shupe.Id),"Rejecting a seen card removes it from the visible list.");
            window.CandidateCardSearch.Text="店店"; window.ShowPage(UiPage.Candidates); window.UpdateWorkspaceLayout(1320);
            DeckBuilderSmoke.Render(window,Path.Combine(output,"before-restore.png"),1320,950);
            var click = VisualChildren(window.CandidateCardTray).OfType<Button>().First(b => b.DataContext is DeckStripRow r &&
                r.Slot.Card?.Id==shupe.Id && !Equals(b.Content,"ⓘ"));
            click.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,click));
            await window._projectionQueue.Idle.WaitAsync(TimeSpan.FromSeconds(30));
            Check(((IEnumerable<DeckStripRow>)window.OpponentDeckCards.Rows!).Count(r=>r.Slot.Card?.Id==shupe.Id)==1 &&
                !window._opponentEdits.CorrectedEvidence.Any(k=>k.CardId==shupe.Id),"The real candidate row restores Shupe after a correction in default mode.");
            Check(!((IEnumerable<DeckStripRow>)window.CandidateCardTray.Rows!).Any(r=>r.Slot.Card?.Id==shupe.Id),"A restored gold card is removed from candidates without adding duplicate copies.");
            Check(window.OpponentMatchesPanel.Report is {HasMatches:false,TotalObservedCopies:1},"One restored sighting cannot trigger a deck recommendation.");
            var sampleDeck=window._cachedDecks.First(d=>d.CountOf(shupe.Id)>0 && d.Cards.Count>=12);
            var sampleEvidence=sampleDeck.Cards.Take(12).Select(c=>new ObservedCard(c.Card,CardProvenance.ConfirmedStartingDeck,1,DateTimeOffset.MinValue,ObservedCopies:c.Count));
            window.OpponentMatchesPanel.Report=OpponentDeckMatcher.Match(window._cachedDecks,sampleEvidence,sampleDeck.Faction,sampleDeck.Leader,catalog:window._candidateCatalog);
            Check(window.OpponentMatchesPanel.Report is {HasMatches:true} report && report.Matches.Count<=2 &&
                report.Matches.All(h=>h.ObservedCoveragePercent>=80),"Sufficient actual evidence recommends at most two lists meeting copy coverage.");
            window.ShowPage(UiPage.Deck); window.UpdateWorkspaceLayout(1320);
            DeckBuilderSmoke.Render(window,Path.Combine(output,"after-restore-and-matches.png"),1320,950);
            var firstHit = window.OpponentMatchesPanel.Report!.Matches.First();
            var preview = new DeckMatchPreviewWindow(firstHit,FindDataRoot(),window.OpponentMatchesPanel.Report.Evidence);
            DeckBuilderSmoke.Render(preview,Path.Combine(output,"candidate-deck-list.png"),850,900);
            var original = window._cachedDecks.First(); var aliasPath=Path.Combine(output,"rename-aliases.json");
            DeckDisplayNames.Configure(aliasPath); window._libraryReady=true; window.ShowPage(UiPage.Library);
            var originalKey=MatchAnalysis.DeckKey(MatchReviewStore.Reference(original));
            var item = new DeckListItem(original.Name,"",original.SourceUri,original);
            window.RenameLibraryItem(item,"熟悉的测试卡组",persist:false);
            var renamed=window._library.Find(original.Id)!;
            Check(renamed.Deck.Name=="熟悉的测试卡组" && renamed.OriginalNames.Contains(original.Name) &&
                originalKey==MatchAnalysis.DeckKey(MatchReviewStore.Reference(renamed.Deck)),"Renaming a complete deck keeps its exact composition and original source name.");
            var observation = new DeckListItem("Observed opponent deck · fixture","",null,null,DraftSourceKey:"observed:Opponent:fixture");
            window.RenameLibraryItem(observation,"我的对手记录",persist:false);
            Check((observation with {Name="Observed opponent deck · changed time"}).DisplayName=="我的对手记录",
                "Observation names are stored under a stable record key instead of a changing timestamp title.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));
            return 0;
        }
        catch(Exception error) { File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true})); return 1; }
        finally
        {
            DeckDisplayNames.Configure(Path.Combine(FindDataRoot(),"cache","deck-display-names.json"));
            if (window is not null) { window._projectionQueue?.Dispose(); window.Close(); }
        }
    }
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        for(var index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
        { var child=VisualTreeHelper.GetChild(root,index);yield return child;foreach(var nested in VisualChildren(child))yield return nested; }
    }
}
