using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Localization;

namespace GwentCompanion.App;

internal sealed class PlayerDeckPromptWindow : Window
{
    public bool ChooseDeck { get; private set; }
    public PlayerDeckPromptWindow()
    {
        Title = UiTranslation.Translate("Choose your played deck"); Width = 510; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; Background = Controls.FactionPalette.Brush("#11191C");
        Foreground = Controls.FactionPalette.Brush("#EEF2F5");
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "No player deck is selected. To save your complete deck and improve recognition, select your deck in the library and click Use selected. The selection is kept until you change decks.", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new Thickness(0,16,0,0), HorizontalAlignment = HorizontalAlignment.Right };
        var choose = new Button { Content = "Choose current deck", IsDefault = true, Padding = new Thickness(10,6,10,6), Margin = new Thickness(0,0,8,0) };
        choose.Click += (_,_) => { ChooseDeck = true; DialogResult = false; };
        var partial = new Button { Content = "Track without a reference deck", Padding = new Thickness(10,6,10,6), Margin = new Thickness(0,0,8,0) };
        partial.Click += (_,_) => DialogResult = true;
        buttons.Children.Add(choose); buttons.Children.Add(partial); panel.Children.Add(buttons); Content = panel;
    }
}
