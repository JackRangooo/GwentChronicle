using System.Windows;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private void EditEvidenceClicked(object sender,RoutedEventArgs e)
    {
        e.Handled=true;
        if(sender is FrameworkElement{DataContext:DeckVm{Editor:{ } editor} deck})
            new MatchEvidenceWindow(editor,_catalog,deck.Identity.Title,_libraryDecks){Owner=this}.Show();
    }
    private async void EditRecordClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: HistoryVm row }) return;
        try
        {
            if (row.Entry.ManualRecord && row.Entry.Abnormal)
            {
                var manual = new AbnormalMatchWindow(ManualMatchStore.ReadRecord(row.Entry.Path),
                    new LadderProgressStore(_directory).Load()?.Baseline, editing: true) { Owner = this };
                if (manual.ShowDialog() == true && manual.Record is { } record) await SaveAbnormalMatchAsync(record);
                return;
            }
            var source = LocalMatchStore.Read(row.Entry.Path); var store = new MatchReviewStore(_directory);
            var previous = store.Get(source);
            var effective = MatchIdentityInference.Apply(MatchReviewStore.Apply(source, previous), _catalog);
            var profiles = new LocalMatchProfileStore(_directory);
            var editor = new MatchReviewWindow(source, effective, previous, _catalog, _libraryDecks, profiles.Get(source)) { Owner = this };
            if (editor.ShowDialog() != true) return;
            if (editor.ResetCorrections) store.Reset(source);
            else if (editor.Review is { } review)
            {
                store.Set(source, review);
                if (editor.PlayerDetails is { } details) profiles.Set(source, details);
            }
            await RefreshAsync();
        }
        catch (Exception error) { StatusText.Text = "Could not save match correction: " + error.Message; }
    }
    private async void DeleteRecordClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: HistoryVm row }) return;
        try { new MatchReviewStore(_directory).Delete(LocalMatchStore.Read(row.Entry.Path)); await RefreshAsync(); }
        catch (Exception error) { StatusText.Text = "Could not delete record: " + error.Message; }
    }
    private async void RestoreRecordClicked(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement { DataContext: HistoryVm row }) return;
        try { new MatchReviewStore(_directory).Restore(LocalMatchStore.Read(row.Entry.Path)); await RefreshAsync(); }
        catch (Exception error) { StatusText.Text = "Could not restore record: " + error.Message; }
    }
    private async void CleanEmptyCapturesClicked(object sender, RoutedEventArgs e)
    {
        var empty = _all.Where(m => m.EmptyCapture && !m.Deleted).ToArray();
        if (empty.Length == 0) { StatusText.Text = "No empty captures to clean."; return; }
        if (MessageBox.Show(this, $"Move {empty.Length} empty captures to the recycle bin? These records contain no result, cards, scores or rating. You can restore them later.",
            "Clean empty captures", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var sources = empty.Select(m => LocalMatchStore.Read(m.Path)).ToArray();
            new MatchReviewStore(_directory).Delete(sources.Where(MatchReviewStore.IsEmptyCapture));
            await RefreshAsync();
        }
        catch (Exception error) { StatusText.Text = "Could not clean empty captures: " + error.Message; }
    }
}
