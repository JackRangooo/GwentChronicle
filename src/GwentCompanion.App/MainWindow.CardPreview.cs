using System.IO;
using System.Windows;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private CardPreviewWindow? _cardPreviewWindow;

    private void InitializeCardPreviewActions()
    {
        Controls.CardInspection.PortraitsDirectory=ResolveArtCacheDirectory();
        CandidateCardTray.RowPreviewRequested += CardList_OnPreviewRequested;
        OpponentDeckCards.RowPreviewRequested += CardList_OnPreviewRequested;
        LibraryDeckCards.RowPreviewRequested += CardList_OnPreviewRequested;
    }

    private void CardList_OnPreviewRequested(object? sender, object row)
    {
        if (row is not DeckStripRow { Slot.Card: { } card }) return;
        ShowCardPreview(card);
    }

    private void ShowCardPreview(CardDefinition card)
    {
        if (_cardPreviewWindow is { IsVisible: true } existing)
        {
            existing.Close();
        }

        var path = Path.Combine(ResolveArtCacheDirectory(), card.Id + ".jpg");
        var preview = new CardPreviewWindow(card, path) { Owner = this };
        _cardPreviewWindow = preview;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cardPreviewWindow, preview)) _cardPreviewWindow = null;
        };
        preview.Show();
    }
}
