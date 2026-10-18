using System.Windows.Controls;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private async void GameLanguage_OnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_libraryReady || _restoringReviewPreference || GameLanguageBox is null) return;
        var language = GameLanguageBox.SelectedIndex == 1 ? "en-US" : "zh-Hans-CN";
        if (language == ScreenStateRecognizer.DefaultOcrLanguage) return;
        if (_analysisTransition || _libraryWindow is not null || _builderWindow is not null || _diagnosticSession?.IsRunning == true || _visionWorker is not null)
        {
            _restoringReviewPreference = true;
            GameLanguageBox.SelectedIndex = ScreenStateRecognizer.DefaultOcrLanguage == "en-US" ? 1 : 0;
            _restoringReviewPreference = false;
            ShowAnalysisStatus("采集仍在继续。更改游戏语言前请先手动点击 ■，此操作不会自动中止录像。");
            return;
        }
        _analysisTransition = true; GameLanguageBox.IsEnabled = false; RefreshAnalysisButton();
        try
        {
            // Prove the engine is available before changing the persisted preference.
            using var probe = new ScreenStateRecognizer(language);
            if (_visionLoadTask is { IsCompleted: false } loading) await loading;
            if (_ocrWarmupTask is { } warmup)
            {
                try { (await warmup).Dispose(); } catch { /* Discard the old warm-up failure. */ }
                _ocrWarmupTask = null;
            }
            _visionPipeline?.Dispose(); _visionPipeline = null;
            ScreenStateRecognizer.DefaultOcrLanguage = language;
            SaveUserSettings(); ShowAnalysisStatus("Game language updated. Click Play to resume tracking.");
        }
        catch (Exception error) { ShowAnalysisFailure("Could not change the game language:", error); }
        finally
        {
            _restoringReviewPreference = true;
            GameLanguageBox.SelectedIndex = ScreenStateRecognizer.DefaultOcrLanguage == "en-US" ? 1 : 0;
            _restoringReviewPreference = false;
            _analysisTransition = false; GameLanguageBox.IsEnabled = true; RefreshAnalysisButton();
        }
    }
}
