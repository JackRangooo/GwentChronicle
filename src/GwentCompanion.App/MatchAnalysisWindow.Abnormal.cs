using System.IO;
using System.Windows;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private async void AddAbnormalMatchClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var baseline = new LadderProgressStore(_directory).Load()?.Baseline;
            var installation = _all.FirstOrDefault()?.InstallationId ?? InstallationIdentity.LoadOrCreate(
                Path.Combine(Path.GetFullPath(Path.Combine(_directory,"..")), "installation.json")).Id;
            var patch = baseline?.Patch ?? _all.FirstOrDefault(m => m.Patch != "Unknown patch")?.Patch ?? "Unknown patch";
            var dialog = new AbnormalMatchWindow(new(installation, Guid.NewGuid(), DateTimeOffset.UtcNow, patch), baseline) { Owner = this };
            if (dialog.ShowDialog() != true || dialog.Record is not { } record) return;
            await SaveAbnormalMatchAsync(record);
        }
        catch (Exception error) { StatusText.Text = "无法添加异常对局：" + error.Message; }
    }
    private async Task SaveAbnormalMatchAsync(ManualMatchRecord record)
    {
        new ManualMatchStore(_directory).Save(record);
        _ready = false; RecordScopeChoice.SelectedIndex = 0;
        PatchChoice.SelectedIndex = 0; _ready = true;
        await RefreshAsync();
    }
}
