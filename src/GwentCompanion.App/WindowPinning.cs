using System.Windows;
using System.Windows.Controls;

namespace GwentCompanion.App;

internal static class WindowPinning
{
    // Applied to both XAML windows and code-created previews/dialogs. The native
    // title bar and the content tree are retained, including on later refreshes.
    internal static void ConfigureWindow(Window window)
    {
        if (!ReferenceEquals(window.Template, window.TryFindResource("PinnableWindowTemplate")))
            window.SetResourceReference(Control.TemplateProperty, "PinnableWindowTemplate");
    }
}
