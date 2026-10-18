using System.Windows;
using System.Windows.Markup;

namespace GwentCompanion.App.Localization;

/// <summary>
/// Keep the bound/source text English; render a separate translated TextBlock value.
/// In particular, status checks, bindings and card models must never read Chinese
/// back into recognition, filters or saved decks.
/// </summary>
public sealed class LocalizedTextBlock : System.Windows.Controls.TextBlock
{
    public new static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LocalizedTextBlock),
        new FrameworkPropertyMetadata(string.Empty, OnSourceTextChanged));

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public new string Text
    {
        get => (string?)GetValue(TextProperty) ?? string.Empty;
        set => SetValue(TextProperty, value);
    }

    public string DisplayText => base.Text;

    public LocalizedTextBlock() => Language = XmlLanguage.GetLanguage("zh-CN");

    private static void OnSourceTextChanged(DependencyObject target, DependencyPropertyChangedEventArgs args)
    {
        var text = (LocalizedTextBlock)target;
        text.SetValue(System.Windows.Controls.TextBlock.TextProperty,
            UiTranslation.Translate(args.NewValue as string ?? string.Empty));
    }
}
