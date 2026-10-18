using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App.Controls;

public interface ICardPreviewRow { CardDefinition? Card { get; } }

/// <summary>Shared read-only hover and click inspection across card surfaces.</summary>
public static class CardInspection
{
    public static string PortraitsDirectory { get; set; } = Path.Combine(AppContext.BaseDirectory,"cache","portraits");
    public static readonly DependencyProperty CardProperty=DependencyProperty.RegisterAttached("Card",typeof(CardDefinition),typeof(CardInspection),new PropertyMetadata(null,Changed));
    public static void SetCard(DependencyObject target,CardDefinition? value)=>target.SetValue(CardProperty,value);
    public static CardDefinition? GetCard(DependencyObject target)=>(CardDefinition?)target.GetValue(CardProperty);
    private static void Changed(DependencyObject target,DependencyPropertyChangedEventArgs args)
    {
        if(target is not FrameworkElement element) return;
        element.ToolTipOpening-=Opening;
        if(args.NewValue is null) {element.ToolTip=null;return;}
        element.ToolTip=new ToolTip { Content="查看完整卡牌",MaxWidth=520,Background=FactionPalette.Brush("#111C23"),BorderBrush=FactionPalette.Brush("#B8995A"),Padding=new Thickness(12) };
        ToolTipService.SetInitialShowDelay(element,350); ToolTipService.SetShowDuration(element,60000);
        element.ToolTipOpening+=Opening;
    }
    private static void Opening(object sender,ToolTipEventArgs args)
    {
        if(sender is FrameworkElement element && GetCard(element) is { } card && element.ToolTip is ToolTip tip) tip.Content=CreateHover(card);
    }
    public static FrameworkElement CreateHover(CardDefinition card)
    {
        var root=new Grid { Width=450,MaxHeight=430 }; root.ColumnDefinitions.Add(new(){Width=new GridLength(150)}); root.ColumnDefinitions.Add(new());
        var image=ReadPortrait(card.Id);
        root.Children.Add(image is null ? new TextBlock {Text="卡图尚未缓存",Foreground=Brushes.LightGray,VerticalAlignment=VerticalAlignment.Center} : new Image {Source=image,Stretch=Stretch.Uniform,Margin=new Thickness(0,0,12,0)});
        var details=new StackPanel(); Grid.SetColumn(details,1); root.Children.Add(details);
        details.Children.Add(new TextBlock {Text=UiTranslation.CardName(card.Id,card.Name),FontSize=18,FontWeight=FontWeights.SemiBold,Foreground=FactionPalette.Brush("#E5C77E"),TextWrapping=TextWrapping.Wrap});
        details.Children.Add(new TextBlock {Text=UiTranslation.Translate(card.Faction)+$" · {card.Provision} 费用"+(card.Power>0 ? $" · {card.Power} 战力" : ""),Foreground=FactionPalette.Brush("#91C5AB"),Margin=new Thickness(0,7,0,7),TextWrapping=TextWrapping.Wrap});
        details.Children.Add(new ScrollViewer {MaxHeight=305,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Content=new TextBlock {Text=UiTranslation.Translate(card.AbilityText ?? "No ability text in the local card catalogue."),Foreground=Brushes.White,FontSize=13,LineHeight=21,TextWrapping=TextWrapping.Wrap}});
        details.Children.Add(new TextBlock {Text="点击 ⓘ 打开大图与完整效果",FontSize=10,Foreground=Brushes.LightGray,Margin=new Thickness(0,10,0,0)});
        return root;
    }
    private static readonly Dictionary<string,ImageSource?> Images=new(StringComparer.OrdinalIgnoreCase);
    public static ImageSource? ReadPortrait(string cardId)
    {
        var path=Path.Combine(PortraitsDirectory,cardId+".jpg");
        if(Images.TryGetValue(path,out var cached) && cached is not null) return cached;
        try {
            if(!File.Exists(path)) return null;
            var bitmap=new BitmapImage(); bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;bitmap.DecodePixelWidth=400;bitmap.UriSource=new Uri(path);bitmap.EndInit();bitmap.Freeze();
            if(Images.Count>=96) Images.Clear();
            Images[path]=bitmap;return bitmap;
        } catch(Exception error) when(error is IOException or NotSupportedException or FormatException) {return null;}
    }
    public static void Show(FrameworkElement source,CardDefinition card)
    {
        var owner=Window.GetWindow(source);
        new CardPreviewWindow(card,Path.Combine(PortraitsDirectory,card.Id+".jpg")){Owner=owner}.Show();
    }
}
