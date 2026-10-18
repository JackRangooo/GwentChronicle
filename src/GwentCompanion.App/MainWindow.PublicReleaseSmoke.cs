using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static int RunPublicReleaseSmoke()
    {
        var output = Path.Combine(AppContext.BaseDirectory, "diagnostics", "public-release-smoke");
        Directory.CreateDirectory(output);
        var fixture = Path.Combine(Path.GetTempPath(), "gwent-seeds-" + Guid.NewGuid().ToString("N"));
        var checks = new List<string>(); MainWindow? window = null;
        void Check(bool ok, string label) { if (!ok) throw new InvalidOperationException(label); checks.Add(label); }
        try
        {
            Directory.CreateDirectory(Path.Combine(fixture, "defaults", "cache", "portraits"));
            Directory.CreateDirectory(Path.Combine(fixture, "cache"));
            File.WriteAllText(Path.Combine(fixture, "defaults", "cache", "deck-library.json"), "factory");
            File.WriteAllText(Path.Combine(fixture, "defaults", "cache", "gwent-one-cards.json"), "catalog");
            File.WriteAllText(Path.Combine(fixture, "cache", "deck-library.json"), "user-edited");
            File.WriteAllText(Path.Combine(fixture, "defaults", "cache", "portraits", "123.jpg"), "portrait");
            ReleaseDefaults.Initialize(fixture);
            Check(File.ReadAllText(Path.Combine(fixture, "cache", "deck-library.json")) == "user-edited", "Existing user library survives seed initialization.");
            Check(File.ReadAllText(Path.Combine(fixture, "cache", "gwent-one-cards.json")) == "catalog", "Fresh installation gets its catalog.");
            Check(File.ReadAllText(Path.Combine(fixture, "cache", "portraits", "123.jpg")) == "portrait", "Fresh installation gets card artwork.");
            File.WriteAllText(Path.Combine(fixture, "cache", "portraits", "123.jpg"), "newer-portrait");
            ReleaseDefaults.Initialize(fixture);
            Check(File.ReadAllText(Path.Combine(fixture, "cache", "portraits", "123.jpg")) == "newer-portrait", "Seed initialization never replaces an existing cached image.");
            Check(!Directory.Exists(Path.Combine(fixture, "match-data")), "Initialization never creates match records.");
            Check(Directory.Exists(FindGameRoot()), "Portable release can open its library without installing into the game folder.");
            window = new MainWindow(); window.Loaded -= window.OnLoaded; window.HideLoadingShell();
            window.Width = 1024; window.Height = 768; window.ShowPage(UiPage.Settings); window.Show(); window.UpdateLayout();
            Check(window.ReleaseVersionText.Text.Contains("0.4."), "Settings displays the actual release version.");
            Check(window.CheckUpdatesButton.IsEnabled && !window.DownloadUpdateButton.IsEnabled, "Update controls require a check before downloading.");
            Check(!window.OpenUpdateFolderButton.IsEnabled && window._availableRelease is null, "Opening Settings never contacts GitHub or starts downloading.");
            Check(window._visionWorker is null && window._diagnosticSession is null, "Release UI never starts or stops capture.");
            Check(window.FindName("DownloadUpdateButton") is Button, "The download action is connected to a real named button.");
            RenderNativeWindow(window, Path.Combine(output, "update-settings.png"));
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
        finally { window?.Close(); if (Directory.Exists(fixture)) Directory.Delete(fixture, true); }
    }
}
