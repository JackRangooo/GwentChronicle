using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private async void UpdateObservationMatches(DeckListItem item)
    {
        var version = _libraryPreviewRequest;
        var observations = (item.PartialSlots ?? []).Where(s => s.State == DeckSlotState.Observed && s.Card is not null)
            .GroupBy(s => s.Card!.Id).Select(g => new ObservedCard(g.First().Card!, CardProvenance.ProbableStartingDeck,
                1, item.RecordedAt ?? DateTimeOffset.MinValue, ObservedCopies:g.Count())).ToArray();
        try
        {
            var decks = _cachedDecks; var catalog = _candidateCatalog;
            var editor = new OpponentEvidenceEditor(MatchStorageDirectory,item.ReviewMatchKey ?? item.DisplayKey,observations,item.FilterFaction,
                () => { RefreshDeckList(); }).WithCatalog(catalog ?? []);
            var report = await Task.Run(() => OpponentDeckMatcher.Match(decks, editor.Evidence(), item.FilterFaction, item.FilterLeader,
                DeckPatchMetadata.Current(DateTimeOffset.Now).Label, catalog:catalog));
            if (_windowClosing || version != _libraryPreviewRequest || _page != UiPage.Library) return;
            LibraryMatchesPanel.DataRoot = FindDataRoot();
            LibraryMatchesPanel.Editor = editor;
            LibraryMatchesPanel.FilterLabel = (item.FilterFaction ?? "Unknown faction") + " · " + (item.FilterLeader ?? "Unknown leader");
            LibraryMatchesPanel.Report = report;
            LibraryMatchesExpander.Visibility=report.HasEvidence ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        }
        catch (Exception error) { LibraryActionStatus("Could not compare observed cards: " + error.Message); }
    }
}
