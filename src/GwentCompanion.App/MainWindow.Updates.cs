using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Windows;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private AvailableRelease? _availableRelease;
    private string? _downloadedUpdate;
    private readonly CancellationTokenSource _updateCancellation = new();
    private void InitializeReleaseUpdates()
    {
        ReleaseVersionText.Text = "当前版本 " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "未知");
        Closed += (_, _) => _updateCancellation.Cancel();
    }
    private async void CheckUpdates_OnClick(object sender, RoutedEventArgs e)
    {
        CheckUpdatesButton.IsEnabled = DownloadUpdateButton.IsEnabled = false;
        _availableRelease = null; UpdateStatusText.Text = "正在检查 GitHub 正式发行版本…";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
            _availableRelease = await new ReleaseUpdates(http).CheckAsync(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 4, 0), _updateCancellation.Token);
            UpdateStatusText.Text = _availableRelease is { } release ? $"发现 {release.Tag}。点击下载，校验通过后再手动退出工具并覆盖更新。" : "当前没有可用的更新版本。";
            DownloadUpdateButton.IsEnabled = _availableRelease is not null;
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = "检查已取消或超时，请稍后重试。"; }
        catch (Exception error) { UpdateStatusText.Text = "检查失败：" + error.Message + " 可点击“GitHub 发行页”查看。"; }
        finally { CheckUpdatesButton.IsEnabled = true; }
    }
    private async void DownloadUpdate_OnClick(object sender, RoutedEventArgs e)
    {
        if (_availableRelease is not { } release) return;
        CheckUpdatesButton.IsEnabled = DownloadUpdateButton.IsEnabled = false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("GwentVision-zhCN-updater");
            var progress = new Progress<long>(size => UpdateStatusText.Text = $"正在下载 {release.Tag} · {size / 1048576d:0.0} MB；采集不受影响。");
            _downloadedUpdate = await new ReleaseUpdates(http).DownloadAsync(release,
                Path.Combine(PersistentUserDataDirectory, "updates"), progress, _updateCancellation.Token);
            OpenUpdateFolderButton.IsEnabled = true;
            UpdateStatusText.Text = $"{release.Tag} 下载完成，SHA256 校验通过。请先手动停止识别并退出工具，再将 ZIP 解压到原安装目录覆盖；cache 与本地对局不会被发行包覆盖。";
        }
        catch (OperationCanceledException) { UpdateStatusText.Text = "下载已取消或超时，可重试。"; }
        catch (Exception error) { UpdateStatusText.Text = "下载失败：" + error.Message; }
        finally { CheckUpdatesButton.IsEnabled = true; DownloadUpdateButton.IsEnabled = true; }
    }
    private void OpenUpdateFolder_OnClick(object sender, RoutedEventArgs e)
    {
        if (_downloadedUpdate is null || !File.Exists(_downloadedUpdate)) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { "/select,", _downloadedUpdate } }); }
        catch (Exception error) { UpdateStatusText.Text = "无法打开文件夹：" + error.Message; }
    }
    private void OpenReleases_OnClick(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(ReleaseUpdates.ReleasesPage.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) { UpdateStatusText.Text = "无法打开网页：" + error.Message; }
    }
}
