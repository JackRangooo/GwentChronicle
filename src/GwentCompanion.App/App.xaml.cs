using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        ReleaseDefaults.Initialize(AppContext.BaseDirectory);
        Localization.UiTranslation.Enabled = !e.Args.Contains("--english-ui");
        AppearancePreferences.Load();
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => {
                WindowPinning.ConfigureWindow((Window)sender);
                Localization.UiTranslation.ConfigureWindow((Window)sender);
            }));
        base.OnStartup(e);
        if(e.Args.Contains("--public-release-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(GwentCompanion.App.MainWindow.RunPublicReleaseSmoke());return;
        }
        if(e.Args.Contains("--obsidian-ui-audit")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunObsidianAuditAsync(e.Args.Contains("--baseline")));return;
        }
        if(e.Args.Contains("--window-pinning-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunWindowPinningSmokeAsync());return;
        }
        if(e.Args.Contains("--result-replay-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunResultReplaySmokeAsync(e.Args.Last()));return;
        }
        if(e.Args.Contains("--restore-result-fields")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;var n=Array.IndexOf(e.Args,"--restore-result-fields");
            Shutdown(await MatchAnalysisWindow.RestoreResultFieldsAsync(e.Args[n+1],e.Args[n+2],e.Args[n+3]));return;
        }
        if(e.Args.Contains("--result-number-probe")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;var n=Array.IndexOf(e.Args,"--result-number-probe");
            Shutdown(await MatchAnalysisWindow.ProbeResultNumbersAsync(e.Args[n+1],e.Args[n+2]));return;
        }
        if(e.Args.Contains("--result-fields-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;var n=Array.IndexOf(e.Args,"--result-fields-smoke");
            Shutdown(await MatchAnalysisWindow.RunResultFieldsSmokeAsync(e.Args[n+1]));return;
        }
        if(e.Args.Contains("--library-workflow-audit")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var n=Array.IndexOf(e.Args,"--library-workflow-audit");
            Shutdown(await MatchAnalysisWindow.RunLibraryWorkflowAuditAsync(e.Args[n+1],e.Args.Length>n+2?e.Args[n+2]:null));return;
        }
        if(e.Args.Contains("--library-workflow-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(await MatchAnalysisWindow.RunLibraryWorkflowSmokeAsync());return;
        }
        if(e.Args.Contains("--continuous-capture-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(await GwentCompanion.App.MainWindow.RunContinuousCaptureSmokeAsync());return;
        }
        if(e.Args.Contains("--abnormal-matches-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(await MatchAnalysisWindow.RunAbnormalMatchesSmokeAsync());return;
        }
        if(e.Args.Contains("--evidence-first-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;Shutdown(await MatchAnalysisWindow.RunEvidenceSmokeAsync());return;
        }
        if(e.Args.Contains("--probe-provided-result")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;var n=Array.IndexOf(e.Args,"--probe-provided-result");
            Shutdown(await MatchAnalysisWindow.ProbeProvidedResultAsync(e.Args[n+1],e.Args[n+2]));return;
        }
        if(e.Args.Contains("--result-visibility-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunResultVisibilitySmokeAsync());return;
        }
        if(e.Args.Contains("--result-display-audit")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            var n=Array.IndexOf(e.Args,"--result-display-audit");
            Guid? target=n+2<e.Args.Length && Guid.TryParse(e.Args[n+2],out var id)?id:null;
            Shutdown(await MatchAnalysisWindow.AuditResultDisplayAsync(e.Args[n+1],target));return;
        }
        if(e.Args.Contains("--deck-names-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunDeckNamesSmokeAsync());return;
        }
        if(e.Args.Contains("--deck-names-audit")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.AuditDeckNamesAsync(e.Args[Array.IndexOf(e.Args,"--deck-names-audit")+1]));return;
        }
        if(e.Args.Contains("--result-ladder-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunResultLadderSmokeAsync(e.Args[Array.IndexOf(e.Args,"--result-ladder-smoke")+1]));return;
        }
        if(e.Args.Contains("--initialize-result-ladder")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.InitializeResultLadderAsync(e.Args));return;
        }
        if(e.Args.Contains("--match-insights-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunInsightsSmokeAsync(e.Args));return;
        }
        if(e.Args.Contains("--confirm-local-rank")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(MatchAnalysisWindow.ConfirmLocalRank(e.Args));return;
        }
        if(e.Args.Contains("--ui-experience-smoke")) {
            ShutdownMode=ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunUiExperienceSmokeAsync());return;
        }
        if (e.Args.Contains("--library-memory-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunLibraryMemorySmokeAsync()); return;
        }
        if (e.Args.Contains("--opponent-matching-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunOpponentMatchingSmokeAsync()); return;
        }
        if (e.Args.Contains("--opponent-match-audit"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.AuditOpponentMatchesAsync(e.Args)); return;
        }
        if (e.Args.Contains("--card-preview-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunCardPreviewSmokeAsync()); return;
        }
        if (e.Args.Contains("--match-management-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.RunManagementSmokeAsync()); return;
        }
        if (e.Args.Contains("--confirm-match-deck"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(MatchAnalysisWindow.ConfirmPlayedDeck(e.Args)); return;
        }
        if (e.Args.Contains("--apply-match-review-plan"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await MatchAnalysisWindow.ApplyReviewPlanAsync(e.Args)); return;
        }
        if (e.Args.Contains("--chinese-scan-regression"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await Localization.ChineseScanRegression.RunAsync(e.Args)); return;
        }
        GwentCompanion.Platform.Windows.Vision.ScreenStateRecognizer.DefaultOcrLanguage =
            Localization.UiTranslation.Enabled ? "zh-Hans-CN" : "en-US";
        if (e.Args.Contains("--localization-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunLocalizationSmokeAsync()); return;
        }
        if (e.Args.Contains("--stream-mode-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try { Shutdown(StreamModeWindow.RunSmoke()); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--match-analysis-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunMatchAnalysisSmokeAsync()); return;
        }
        if (e.Args.Contains("--match-storage-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunMatchStorageSmokeAsync()); return;
        }
        if (e.Args.Length > 0 && e.Args[0] == "--export-match")
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                if (e.Args.Length != 3) throw new ArgumentException("Usage: GwentVision --export-match input.gvm output.json");
                var match = LocalMatchStore.Read(e.Args[1]);
                var options = new System.Text.Json.JsonSerializerOptions(GameStateJournal.Json) { WriteIndented = true };
                using (var output = new FileStream(e.Args[2], FileMode.CreateNew, FileAccess.Write))
                    System.Text.Json.JsonSerializer.Serialize(output, match, options);
                Shutdown(0);
            }
            catch (Exception error) { Console.Error.WriteLine(error.Message); Shutdown(1); }
            return;
        }
        if (e.Args.Contains("--export-browser-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckExportWindow.RunExportBrowserSmokeAsync()); return;
        }
        if (e.Args.Contains("--automatic-export-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckExportWindow.RunAutomaticExportSmokeAsync()); return;
        }
        if (e.Args.Contains("--post-match-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunPostMatchSmokeAsync()); return;
        }
        if (e.Args.Contains("--season-tools-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunSeasonToolsAsync()); return;
        }
        if (e.Args.Contains("--card-data-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunCardDataSmokeAsync()); return;
        }
        if (e.Args.Contains("--deck-editor-gestures-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunGesturesAsync()); return;
        }
        if (e.Args.Contains("--observed-editor-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await GwentCompanion.App.MainWindow.RunObservedEditorSmokeAsync()); return;
        }
        if (e.Args.Contains("--workspace-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(GwentCompanion.App.MainWindow.RunWorkspaceSmoke()); return;
        }
        if (e.Args.Contains("--library-variation-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(GwentCompanion.App.MainWindow.RunVariationSmoke()); return;
        }
        if (e.Args.Contains("--deck-builder-smoke"))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Shutdown(await DeckBuilderSmoke.RunAsync()); return;
        }
        if (!e.Args.Contains("--library-sandbox"))
        {
            MainWindow = new MainWindow(); MainWindow.Show(); return;
        }
        // Explicit UI-test mode: never open the main live pipeline or write the user's library.
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Gwent.exe"))) root = root.Parent;
        if (root is null) throw new InvalidOperationException("Test workspace not found.");
        var project = Path.Combine(root.FullName, "GwentCompanion");
        var sandbox = Path.Combine(project, "diagnostics", "library-ui-sandbox");
        Directory.CreateDirectory(sandbox);
        var libraryPath = Path.Combine(sandbox, "deck-library.json");
        var library = DeckLibrary.Load(libraryPath);
        if (library.Decks.Length == 0)
        {
            var payload = Directory.GetFiles(Path.Combine(project, "cache", "decks"), "*.json").First();
            library.Merge([new PlayGwentDeckPageParser().ParseStateJson(File.ReadAllText(payload), new Uri("https://www.playgwent.com/en/decks/" + Path.GetFileNameWithoutExtension(payload)))]);
        }
        var window = new DeckLibraryWindow(library, libraryPath, Path.Combine(sandbox, "payloads"),
            GwentOneCardCatalog.Load(Path.Combine(project, "cache", "gwent-one-cards.json")), library.Decks.First(), () => { }, Path.Combine(sandbox, "scans"));
        window.Title += " · LIBRARY TEST";
        window.ScanButton.IsEnabled = false;
        window.ScanStatus.Text = "Isolated UI test: game capture disabled; writes go only to diagnostics/library-ui-sandbox.";
        MainWindow = window; window.Show();
    }
}
