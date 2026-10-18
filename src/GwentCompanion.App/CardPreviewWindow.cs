using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

/// <summary>A read-only, full-size inspection view for cards in compact live lists.</summary>
internal sealed class CardPreviewWindow : Window
{
    public CardDefinition Card { get; }
    public ImageSource? Portrait { get; private set; }
    private readonly CancellationTokenSource _lifetime=new();
    public string DisplayName { get; }
    public string Ability { get; }

    public CardPreviewWindow(CardDefinition card, string portraitPath)
    {
        Card = card;
        DisplayName = UiTranslation.CardName(card.Id, card.Name);
        Ability = string.IsNullOrWhiteSpace(card.AbilityText)
            ? UiTranslation.Translate("No ability text in the local card catalogue.")
            : UiTranslation.Translate(card.AbilityText);
        Portrait = LoadPortrait(portraitPath);

        Title = DisplayName;
        Width = 790;
        Height = 570;
        MinWidth = 620;
        MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.FromRgb(16, 21, 26));
        Foreground = new SolidColorBrush(Color.FromRgb(237, 239, 235));
        FontFamily = new FontFamily("Microsoft YaHei UI");
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
        Content = BuildContent();
        Closed+=(_,_)=>_lifetime.Cancel();
        Loaded+=async(_,_)=> {
            if(Portrait is not null || card.ArtUri is null || Environment.GetCommandLineArgs().Any(a=>a.Contains("smoke") || a=="--analysis-control-test" || a=="--opponent-match-audit")) return;
            try {
                using var http=new HttpClient {Timeout=TimeSpan.FromSeconds(12)};
                var bytes=await http.GetByteArrayAsync(card.ArtUri,_lifetime.Token);
                if(bytes.Length is < 256 or > 16000000) return;
                using(var stream=new MemoryStream(bytes)) {
                    var decoded=new BitmapImage();decoded.BeginInit();decoded.CacheOption=BitmapCacheOption.OnLoad;decoded.DecodePixelWidth=720;decoded.StreamSource=stream;decoded.EndInit();decoded.Freeze();Portrait=decoded;
                }
                _lifetime.Token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(portraitPath)!);
                var temporary=portraitPath+".tmp-"+Guid.NewGuid().ToString("N");
                try {await File.WriteAllBytesAsync(temporary,bytes,_lifetime.Token);File.Move(temporary,portraitPath,true);}
                finally {if(File.Exists(temporary)) File.Delete(temporary);}
                if(IsVisible) Content=BuildContent();
            } catch(Exception error) when(error is HttpRequestException or IOException or OperationCanceledException or NotSupportedException or FormatException) { }
        };
    }

    private UIElement BuildContent()
    {
        var root = new Grid { Margin = new Thickness(18) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var portraitFrame = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(25, 33, 42)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(193, 160, 87)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(5),
            Child = Portrait is null
                ? new TextBlock
                {
                    Text = UiTranslation.Translate("Full portrait unavailable in the local cache."),
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(18),
                    Foreground = new SolidColorBrush(Color.FromRgb(174, 183, 192))
                }
                : new Image { Source = Portrait, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center }
        };
        Grid.SetColumn(portraitFrame, 0);
        root.Children.Add(portraitFrame);

        var details = new DockPanel { Margin = new Thickness(20, 2, 2, 0), LastChildFill = true };
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
        header.Children.Add(new TextBlock
        {
            Text = DisplayName,
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(238, 212, 151))
        });
        header.Children.Add(new TextBlock
        {
            Text = UiTranslation.Translate($"{cardKind(Card.Kind)} · {Card.Provision} provisions") +
                (Card.Power > 0 ? " · " + UiTranslation.Translate($"{Card.Power} power") : ""),
            FontSize = 13,
            Margin = new Thickness(0, 7, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(166, 185, 181))
        });
        DockPanel.SetDock(header, Dock.Top);
        details.Children.Add(header);

        var abilityHeading = new TextBlock
        {
            Text = UiTranslation.Translate("Card ability"),
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 8),
            Foreground = new SolidColorBrush(Color.FromRgb(132, 205, 170))
        };
        DockPanel.SetDock(abilityHeading, Dock.Top);
        details.Children.Add(abilityHeading);

        var abilityScroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock
            {
                Text = Ability,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 17,
                LineHeight = 27,
                Foreground = new SolidColorBrush(Color.FromRgb(232, 236, 232))
            }
        };
        details.Children.Add(abilityScroll);
        Grid.SetColumn(details, 1);
        root.Children.Add(details);
        return root;
    }

    private static string cardKind(CardKind kind) => kind switch
    {
        CardKind.Unit => "Unit",
        CardKind.Special => "Special",
        CardKind.Artifact => "Artifact",
        CardKind.Stratagem => "Stratagem",
        CardKind.Leader => "Leader",
        _ => "Card"
    };

    private static BitmapImage? LoadPortrait(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 720;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception error) when (error is IOException or NotSupportedException or FormatException)
        {
            return null;
        }
    }
}
