using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Localization;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private void DeckDisplayName_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeckListItem clicked }) DeckList.SelectedItem = clicked;
        if (DeckList.SelectedItem is not DeckListItem item) { LibraryActionStatus("Select a deck to edit."); return; }
        var original = item.Deck?.Name ?? item.Name;
        var cards = item.Deck?.Cards ?? (item.PartialSlots ?? []).Where(s => s.Card is not null).GroupBy(s => s.Card!.Id)
            .Select(g => new GwentCompanion.Core.Domain.DeckCard(g.First().Card!,g.Count())).ToArray();
        var clustered = item.TotalVariants>1;
        var initialName = clustered && item.GroupId is { } id ? DeckDisplayNames.Display(id,_library.VariationGroups.Single(g=>g.Id==id).Name) : item.DisplayName;
        var dialog = new DeckRenameWindow(initialName,original,item.HeadingFaction,item.HeadingLeader,cards,clustered) { Owner=this };
        if (dialog.ShowDialog() != true) return;
        try { RenameLibraryItem(item,dialog.DeckName,renameGroup:dialog.RenameGroup); }
        catch (Exception error) { MessageBox.Show(this,error.Message,"名称未保存",MessageBoxButton.OK,MessageBoxImage.Error); }
    }

    private void RenameLibraryItem(DeckListItem item, string name, bool persist = true, bool renameGroup = false)
    {
        name = name.Trim();
        if (name.Length > 160) throw new ArgumentException("Deck names can contain at most 160 characters.");
        if (renameGroup && item.GroupId is { } groupId)
        {
            if (!_libraryReady) throw new InvalidOperationException("The deck library is not ready.");
            var group = _library.VariationGroups.Single(g => g.Id == groupId);
            _library.RenameVariationGroup(groupId,name.Length > 0 ? name : _library.GroupVariants(group)[0].OriginalNames.FirstOrDefault() ?? group.Name);
            if (persist && _reviewEvidencePath is null) _library.Save(LibraryPath);
        }
        else if (name.Length > 0 && item.Deck is { } deck && _library.Find(deck.Id) is not null)
        {
            if (!_libraryReady) throw new InvalidOperationException("The deck library is not ready.");
            _library.Rename(deck.Id, name);
            if (persist && _reviewEvidencePath is null) _library.Save(LibraryPath);
        }
        var displayKey = renameGroup ? item.GroupId! : item.Deck?.Id ?? item.DisplayKey;
        DeckDisplayNames.Set(displayKey, name);
        if (name.Length == 0 && (item.LegacyDisplayKey ?? item.Name) != displayKey)
            DeckDisplayNames.Set(item.LegacyDisplayKey ?? item.Name, "");
        RefreshLibraryReferences();
        DeckList.SelectedItem = DeckList.Items.Cast<DeckListItem>().FirstOrDefault(candidate => SameLibraryItem(candidate, item));
        RefreshLibraryPreview();
        LibraryActionStatus("Deck name saved. Cards and original observations are unchanged.");
    }
}
