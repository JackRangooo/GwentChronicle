using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using GwentCompanion.Core.Data;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunAbnormalMatchesSmokeAsync()
    {
        var root=AppContext.BaseDirectory; var output=Path.Combine(root,"diagnostics","abnormal-matches-smoke");
        Directory.CreateDirectory(output); var checks=new List<string>(); MatchAnalysisWindow? window=null;
        void Check(bool ok,string why) { if(!ok)throw new InvalidOperationException(why);checks.Add(why); }
        try
        {
            var directory=Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            var at=DateTimeOffset.UtcNow.AddHours(-2);var player=new MatchPlayer("Syndicate","Lined Pockets",null,[],[],[]);
            var win=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),DateOnly.FromDateTime(at.UtcDateTime),"fixture","fixture","14.10",false,1,true,true,"VICTORY",
                null,null,null,false,null,player,player,[],[],StartedAtUtc:at.AddMinutes(1));
            var rawPath=new LocalMatchStore(directory).Save(win);var hash=SHA256.HashData(File.ReadAllBytes(rawPath));
            var ladder=new LadderProgressStore(directory); ladder.Calibrate(new(6,2),[],at);
            window=new MatchAnalysisWindow(directory,root){ShowInTaskbar=false,WindowState=WindowState.Minimized};window.Show();window.Hide();window.WindowState=WindowState.Normal;
            await window.RefreshAsync();await WaitForRefresh(window);
            var journal=new ManualMatchRecord(win.InstallationId,Guid.NewGuid(),at.AddMinutes(2),"14.10");
            var dialog=new AbnormalMatchWindow(journal,ladder.Load()!.Baseline);
            DeckBuilderSmoke.Render(dialog,Path.Combine(output,"add-dialog.png"),510,560);
            dialog.TimeInput.Text="25:99";Check(!dialog.PrepareRecord(),"Invalid local times are rejected without saving");
            dialog.TimeInput.Text=journal.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss");
            Check(dialog.PrepareRecord() && dialog.Record!.OccurredAtUtc.ToUnixTimeSeconds()==journal.OccurredAtUtc.ToUnixTimeSeconds(),"Local time round-trips to UTC without shifting the occurrence");
            dialog.Close();
            _=window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
            {
                var actual=Application.Current.Windows.OfType<AbnormalMatchWindow>().Single();
                actual.DateInput.SelectedDate=journal.OccurredAtUtc.ToLocalTime().Date;
                actual.TimeInput.Text=journal.OccurredAtUtc.ToLocalTime().ToString("HH:mm:ss");
                Descendants(actual).OfType<Button>().Single(b=>Equals(b.Content,"添加异常对局")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            window.AddAbnormalButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WaitForRefresh(window);
            Check(window._all.Count(m=>m.ManualRecord)==1 && window._completed.Length==1 && window._filtered.Length==2,"The real Add button saves one abnormal row beside normal history without changing the win-rate sample");
            Check(MatchAnalysis.Totals(window._all) is { Games:1,Wins:1,Losses:0,WinRate:100 },"Global statistics exclude the abnormal loss");
            Check(LadderProgress.Build(ladder.Load()!.Baseline,window._all) is {Current.Fragments:2,Games.Length:2},"The manual loss affects ladder progress exactly once");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"history.png"),1600,1050);
            window.RecordScopeChoice.SelectedIndex=4;
            Check(window._filtered.Length==1 && window._filtered[0].Abnormal,"The abnormal-only filter shows the added record");
            DeckBuilderSmoke.Render(window,Path.Combine(output,"abnormal-only.png"),1600,1050);
            var row=(HistoryVm)window.HistoryList.Items[0];
            Check(row.ResultLabel=="异常 · 败局" && row.AbnormalSummary.Contains("不计入任何胜率"),"Abnormal rows clearly disclose their classification and statistical treatment");
            _=window.Dispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>
            {
                var actual=Application.Current.Windows.OfType<AbnormalMatchWindow>().Single();actual.ReasonInput.Text="网络掉线（已核对）";
                Descendants(actual).OfType<Button>().Single(b=>Equals(b.Content,"保存修改")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }));
            Descendants(window.HistoryList).OfType<Button>().Single(b=>Equals(b.Content,"Edit record")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WaitForRefresh(window);
            Check(window._all.Count(m=>m.ManualRecord)==1 && window._all.Single(m=>m.ManualRecord).AbnormalReason=="网络掉线（已核对）","The real Edit button changes a manual record without duplicating it");
            window.RecordScopeChoice.SelectedIndex=4;DeckBuilderSmoke.Render(window,Path.Combine(output,"abnormal-edited.png"),1600,1050);
            var expander=Descendants(window.HistoryList).OfType<Expander>().First();expander.IsExpanded=true;
            Check(expander.Content is TextBlock t && t.Text.Contains("没有卡牌"),"Manual abnormal details do not fabricate cards, ranks or scores");
            var delete=Descendants(window.HistoryList).OfType<Button>().Single(b=>Equals(b.Content,"Delete record"));
            delete.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WaitForRefresh(window);
            Check(LadderProgress.Build(ladder.Load()!.Baseline,window._all).Current.Fragments==3,"The real Delete button reverses the abnormal ladder loss");
            window.RecordScopeChoice.SelectedIndex=3;DeckBuilderSmoke.Render(window,Path.Combine(output,"recycle-bin.png"),1600,1050);
            Descendants(window.HistoryList).OfType<Button>().Single(b=>Equals(b.Content,"Restore record")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));await WaitForRefresh(window);
            Check(LadderProgress.Build(ladder.Load()!.Baseline,window._all).Current.Fragments==2,"The real Restore button restores the same single ladder loss");
            window.RecordScopeChoice.SelectedIndex=0;window.DisplayChoice.SelectedIndex=2;
            DeckBuilderSmoke.Render(window,Path.Combine(output,"statistics.png"),1600,1050);
            Check(window._ladderSnapshot is {Current.Fragments:2,Games.Length:2},"The statistics page includes the abnormal result in the mosaic estimate");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(rawPath))),"All UI operations preserve the original detector record");
            window.Close();window=null;
            // Render a detached preview: a minimized HWND otherwise clips screenshots to its desktop viewport.
            window=new MatchAnalysisWindow(directory,root);await window.RefreshAsync();
            DeckBuilderSmoke.Render(window,Path.Combine(output,"history.png"),1600,1050);
            Check(window.HistoryList.ActualWidth>1400,"The history preview includes the full row and edit/delete controls");
            window.RecordScopeChoice.SelectedIndex=4;DeckBuilderSmoke.Render(window,Path.Combine(output,"abnormal-only.png"),1600,1050);
            window.DisplayChoice.SelectedIndex=2;DeckBuilderSmoke.Render(window,Path.Combine(output,"statistics.png"),1600,1050);
            window.Close();window=null;
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error)
        { File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1; }
        finally { window?.Close(); }
    }
}
