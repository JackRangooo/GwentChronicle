using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

internal sealed class AbnormalMatchWindow : Window
{
    internal readonly DatePicker DateInput = new();
    internal readonly TextBox TimeInput = new(), ReasonInput = new();
    private readonly TextBlock _error = new() { Foreground = Brushes.Salmon, TextWrapping = TextWrapping.Wrap };
    private readonly ManualMatchRecord _record;
    public ManualMatchRecord? Record { get; private set; }
    public AbnormalMatchWindow(ManualMatchRecord record, LadderBaseline? baseline, bool editing = false)
    {
        _record = record;
        Title = editing ? "编辑异常对局" : "手动添加异常对局";
        Width = 510; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = FactionPalette.Brush("#11191C"); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "记录一次网络异常败局", FontSize = 21, Foreground = FactionPalette.Brush("#E8CC8C") });
        panel.Children.Add(new TextBlock { Text = "用于掉线、未正常进入对局就被判负的情况。保存后只影响段位／碎片追踪，不计入整体、阵营或卡组胜率。", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,12,0,16) });
        panel.Children.Add(new TextBlock { Text = "发生时间（本机时间）" });
        var time = record.OccurredAtUtc.ToLocalTime();
        DateInput.SelectedDate = time.Date; DateInput.DisplayDateEnd = DateTime.Today;
        DateInput.Margin = new Thickness(0,5,0,8); panel.Children.Add(DateInput);
        TimeInput.Text = time.ToString("HH:mm:ss"); TimeInput.MaxLength = 8;
        TimeInput.Margin = new Thickness(0,0,0,16); TimeInput.MinHeight = 30; panel.Children.Add(TimeInput);
        panel.Children.Add(new TextBlock { Text = "异常说明（可直接保留默认值）" });
        ReasonInput.Text = record.Reason; ReasonInput.MaxLength = 200; ReasonInput.MinHeight = 30;
        ReasonInput.Margin = new Thickness(0,5,0,14); panel.Children.Add(ReasonInput);
        var note = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = FactionPalette.Brush("#BAC4C0"), Margin = new Thickness(0,0,0,12) };
        void UpdatePreview()
        {
            var before = baseline is not null && TryTime(out var occurred) && occurred <= baseline.AtUtc;
            note.Text = before
                ? $"该时间早于最近一次段位校准（{baseline!.AtUtc.ToLocalTime():yyyy-MM-dd HH:mm}）。仍会保存记录；校准已包含此前结果，因此不会再次扣碎片。"
                : "计分：败局扣 1 个碎片，最低为 0；不会降段。不会添加卡牌、比分或对手信息。请确认这场异常尚未登记，避免重复扣分。";
        }
        DateInput.SelectedDateChanged += (_,_) => UpdatePreview(); TimeInput.TextChanged += (_,_) => UpdatePreview();
        UpdatePreview(); panel.Children.Add(note); panel.Children.Add(_error);
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        actions.Children.Add(new Button { Content = "取消", IsCancel = true, Padding = new Thickness(14,7,14,7), Margin = new Thickness(0,0,8,0) });
        var save = new Button { Content = editing ? "保存修改" : "添加异常对局", IsDefault = true, Padding = new Thickness(14,7,14,7) };
        save.Click += (_,_) => { if (PrepareRecord()) DialogResult = true; }; actions.Children.Add(save); panel.Children.Add(actions);
        Content = panel;
    }
    private bool TryTime(out DateTimeOffset occurred)
    {
        occurred = default;
        if (DateInput.SelectedDate is not { } date || !TimeSpan.TryParseExact(TimeInput.Text.Trim(),
            new[] { @"hh\:mm\:ss", @"hh\:mm" }, CultureInfo.InvariantCulture, out var time) || time.TotalHours >= 24) return false;
        occurred = new DateTimeOffset(DateTime.SpecifyKind(date.Date.Add(time), DateTimeKind.Local)).ToUniversalTime(); return true;
    }
    internal bool PrepareRecord()
    {
        if (!TryTime(out var occurred) || occurred > DateTimeOffset.UtcNow.AddMinutes(1))
        { _error.Text = "请选择已发生的日期，时间填写为 HH:mm 或 HH:mm:ss。"; return false; }
        Record = _record with { OccurredAtUtc = occurred, Reason = string.IsNullOrWhiteSpace(ReasonInput.Text)
            ? "网络异常／未正常进入对局" : ReasonInput.Text.Trim() };
        return true;
    }
}
