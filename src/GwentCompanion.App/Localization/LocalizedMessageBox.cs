using System.Windows;

namespace GwentCompanion.App.Localization;

public static class LocalizedMessageBox
{
    public static MessageBoxResult Show(Window owner, string message, string caption,
        MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage icon = MessageBoxImage.None,
        MessageBoxResult defaultResult = MessageBoxResult.None) =>
        System.Windows.MessageBox.Show(owner, UiTranslation.Translate(message), UiTranslation.Translate(caption), buttons, icon, defaultResult);
}
