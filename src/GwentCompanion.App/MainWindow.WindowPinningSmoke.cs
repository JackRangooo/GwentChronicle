using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")]
    private static extern nint PinningGetWindowLongPtr(nint handle, int index);

    // Explicit offline mode. Test native always-on-top flags on real window
    // types; never start capture or save the user's library or match data.
    internal static async Task<int> RunWindowPinningSmokeAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","window-pinning-smoke");
        var fixture=Path.Combine(output,"fixture");Directory.CreateDirectory(fixture);
        var checks=new List<string>();var windows=new List<Window>();
        void Check(bool value,string message) {if(!value)throw new InvalidOperationException(message);checks.Add(message);}
        static bool NativePinned(Window w)=>(PinningGetWindowLongPtr(new WindowInteropHelper(w).Handle,-20).ToInt64()&8)!=0;
        static ToggleButton Pin(Window w)=>w is MainWindow main?main.InlinePinButton:(ToggleButton)w.Template.FindName("WindowPinButton",w);
        static void Toggle(Window w)=>((IToggleProvider)new ToggleButtonAutomationPeer(Pin(w)).GetPattern(PatternInterface.Toggle)).Toggle();
        // Native z-order can still be changing during Windows' show/activation
        // animation, after WPF has already reached ApplicationIdle.
        static async Task Idle(Window w) {await Task.Delay(300);await w.Dispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);w.UpdateLayout();}
        static void Screenshot(Window window,string path)
        {
            window.UpdateLayout();var surface=(FrameworkElement)VisualTreeHelper.GetChild(window,0);
            var bitmap=new RenderTargetBitmap((int)Math.Ceiling(surface.ActualWidth),(int)Math.Ceiling(surface.ActualHeight),96,96,PixelFormats.Pbgra32);
            bitmap.Render(surface);var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=File.Create(path);encoder.Save(file);
        }
        try {
            var catalog=GwentOneCardCatalog.Load(CardDataPath);var library=DeckLibrary.Load(LibraryPath);
            var deck=library.Decks.First(d=>d.CardCount==25);
            var main=new MainWindow();main.Loaded-=main.OnLoaded;main._matchStorageDirectoryOverride=Path.Combine(fixture,"matches");main.HideLoadingShell();
            Directory.CreateDirectory(main._matchStorageDirectoryOverride);
            var preview=new CardPreviewWindow(deck.Cards[0].Card,Path.Combine(fixture,"missing-art.jpg"));
            var candidate=new DeckMatchPreviewWindow(new OpponentDeckMatch(deck,100,12,12,[],60,60),FindDataRoot());
            var builder=new DeckBuilderWindow(new DeckLibrary(),Path.Combine(fixture,"builder.json"),catalog,deck,()=>{});
            var picker=new DeckLibraryPickerWindow([deck],catalog);
            var history=new MatchAnalysisWindow(main._matchStorageDirectoryOverride,FindDataRoot());
            var rename=new DeckRenameWindow(deck.Name,deck.Name,deck.Faction,deck.Leader,deck.Cards,false);
            var dialog=new Window {Title="窗口置顶检查",Width=480,Height=260,ResizeMode=ResizeMode.CanResizeWithGrip,
                Content=new TextBlock {Text="本窗口可独立置顶",Margin=new Thickness(20)}};
            windows.AddRange([main,preview,candidate,builder,picker,history,rename,dialog]);
            foreach(var window in windows) {
                window.Show();await Idle(window);
                window.Activate();await Idle(window);
                Check(Pin(window) is {IsVisible:true,IsChecked:false},window.GetType().Name+" exposes an unpinned toolbar button.");
                Check(VisualChildren(window).OfType<ContentPresenter>().Any(p=>ReferenceEquals(p.Content,window.Content)),
                    window.GetType().Name+" directly presents Window.Content without a content wrapper.");
                Toggle(window);await Idle(window);
                Check(window.Topmost&&NativePinned(window)&&Pin(window).IsChecked==true,window.GetType().Name+
                    $" button sets native WS_EX_TOPMOST (Topmost={window.Topmost}, native={NativePinned(window)}, checked={Pin(window).IsChecked}).");
                Check(VisualChildren(Pin(window)).OfType<Localization.LocalizedTextBlock>().Any(t=>t.DisplayText==Localization.UiTranslation.Translate("Pinned on top")),
                    window.GetType().Name+" shows the localized pinned-state label.");
                Check(windows.Where(other=>other!=window).All(other=>!other.Topmost),"Pinning "+window.GetType().Name+" does not set other windows' Topmost properties.");
                Toggle(window);await Idle(window);
                Check(!window.Topmost&&!NativePinned(window)&&Pin(window).IsChecked==false,window.GetType().Name+" button removes native WS_EX_TOPMOST.");
            }
            Toggle(candidate);await Idle(candidate);var pin=Pin(candidate);var root=candidate.Content;
            preview.Activate();candidate.Activate();await Idle(candidate);
            Check(!ReferenceEquals(root,candidate.Content)&&ReferenceEquals(pin,Pin(candidate))&&NativePinned(candidate),"Refreshing a deck preview retains the pin control and native pinned state.");
            candidate.WindowState=WindowState.Minimized;await Idle(candidate);candidate.WindowState=WindowState.Normal;await Idle(candidate);
            Check(candidate.Topmost&&NativePinned(candidate),"Minimize and restore retain the chosen pin state.");Toggle(candidate);
            dialog.Topmost=true;await Idle(dialog);Check(Pin(dialog).IsChecked==true,"External Topmost changes are reflected by the button.");dialog.Topmost=false;
            var grip=(ResizeGrip)dialog.Template.FindName("PART_ResizeGrip",dialog);
            Check(grip.IsVisible,"CanResizeWithGrip retains its native resize grip.");
            dialog.WindowState=WindowState.Maximized;await Idle(dialog);Check(!grip.IsVisible,"Maximized windows hide the resize grip.");dialog.WindowState=WindowState.Normal;
            Toggle(main);main.SetFullScreen(true);await Idle(main);Check(Pin(main).IsVisible&&NativePinned(main),"Fullscreen retains an accessible pin button.");main.SetFullScreen(false);Toggle(main);
            Screenshot(candidate,Path.Combine(output,"deck-preview.png"));
            Toggle(preview);await Idle(preview);Screenshot(preview,Path.Combine(output,"card-preview-pinned.png"));Toggle(preview);
            Toggle(rename);await Idle(rename);Screenshot(rename,Path.Combine(output,"rename-pinned.png"));Toggle(rename);
            Check(main._diagnosticSession is null&&main._visionWorker is null,"Offline test never starts recording or recognition.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks},new JsonSerializerOptions {WriteIndented=true}));
            return 0;
        }
        catch(Exception error) {File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions {WriteIndented=true}));return 1;}
        finally {foreach(var window in windows.AsEnumerable().Reverse())window.Close();}
    }
}
