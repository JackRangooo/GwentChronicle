using System.IO;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class DeckBuilderWindow
{
    private void CardPreviewRequested(object? sender, object item)
    {
        if (item is not CardRow row) return;
        if (_cardPreviewWindow is { IsVisible: true } existing) existing.Close();
        var portrait = Path.Combine(Path.GetDirectoryName(_path)!, "portraits", row.Card.Id + ".jpg");
        var preview = new CardPreviewWindow(row.Card, portrait) { Owner = this };
        _cardPreviewWindow = preview;
        preview.Closed += (_, _) =>
        {
            if (ReferenceEquals(_cardPreviewWindow, preview)) _cardPreviewWindow = null;
        };
        preview.Show();
    }
}
