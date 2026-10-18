using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private static async Task<Expander> ExpandFirstResult(MatchAnalysisWindow window,Guid? matchId=null)
    {
        window.UpdateLayout();
        var expander=Descendants(window.HistoryList).OfType<Expander>().First(e=>e.DataContext is HistoryVm row && (!matchId.HasValue || row.Entry.MatchId==matchId));
        expander.IsExpanded=true;
        var deadline=DateTime.UtcNow.AddSeconds(30);
        while(expander.Content is not ContentControl{Content:DetailsVm} && DateTime.UtcNow<deadline)await Task.Delay(40);
        if(expander.Content is not ContentControl{Content:DetailsVm})throw new InvalidOperationException("Expanded result did not load.");
        window.UpdateLayout();return expander;
    }

    internal static async Task<int> RunResultVisibilitySmokeAsync()
    {
        var root=AppContext.BaseDirectory;var output=Path.Combine(root,"diagnostics","result-visibility-smoke");Directory.CreateDirectory(output);
        var fixture=Path.Combine(output,"fixture-"+Guid.NewGuid().ToString("N"));var cache=Path.Combine(fixture,"cache");Directory.CreateDirectory(cache);
        var checks=new List<string>();MatchAnalysisWindow? history=null;Window? wheelWindow=null;
        void Check(bool ok,string note){if(!ok)throw new InvalidOperationException(note);checks.Add(note);}
        try
        {
            var cardsPath=Path.Combine(root,"cache","gwent-one-cards.json");File.Copy(cardsPath,Path.Combine(cache,"gwent-one-cards.json"));
            var catalog=GwentOneCardCatalog.Load(cardsPath);var deck=DeckLibrary.Load(Path.Combine(root,"cache","deck-library.json")).Decks.First();
            var library=new DeckLibrary();library.Merge([deck]);library.Save(Path.Combine(cache,"deck-library.json"));
            var directory=Path.Combine(fixture,"match-data");var player=MatchReviewStore.Reference(deck);
            var scores=new MatchRound[]{new(1,52,36,true),new(2,11,15,true),new(3,39,103,true)};
            var match=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,15),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,
                player,player,scores,[],StartedAtUtc:new DateTimeOffset(2026,10,15,2,49,0,TimeSpan.Zero));
            var path=new LocalMatchStore(directory).Save(match);var hash=SHA256.HashData(File.ReadAllBytes(path));
            new LocalMatchProfileStore(directory).Set(match,new(User:new(Name:"FixturePlayer",Rank:6,Source:"ResultBanner",RankSource:"ResultBanner"),
                Opponent:new(Name:"FixtureOpponent",Source:"ResultBanner"),ResultRoundScores:scores));
            history=new MatchAnalysisWindow(directory,fixture){WindowState=WindowState.Normal,Width=1600,Height=1000};history.Show();await history.RefreshAsync();await WaitForDeckNames(history);
            DeckBuilderSmoke.Render(history,Path.Combine(output,"history-scores-visible.png"),1600,1000);
            var row=history.HistoryList.Items.Cast<HistoryVm>().Single();
            Check(row.RoundScoresLabel.Contains("52∶36") && row.RoundScoresLabel.Contains("11∶15") && row.RoundScoresLabel.Contains("39∶103"),"All settled round scores are available on the collapsed match header.");
            Check(Descendants(history.HistoryList).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.IsVisible && t.Text==row.RoundScoresLabel),"Round scores are actually visible without opening an explanation section.");
            Check(row.OpponentProfileLabel.Contains("对手的段位：尚未识别") && row.UserProfileLabel.Contains("你的段位：6 段"),"Unknown opponent rank is explicitly distinguished from the recognized player rank.");
            var expanded=await ExpandFirstResult(history);
            DeckBuilderSmoke.Render(history,Path.Combine(output,"expanded-result.png"),1600,1050);
            Check(Descendants(expanded).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.IsVisible && t.Text=="结算信息") &&
                Descendants(expanded).OfType<System.Windows.Controls.TextBlock>().Any(t=>t.IsVisible && t.Text.Contains("52∶36")),"Expanded matches show a visible settlement panel above candidates and deck cards.");
            var withoutProfile=row.Entry with{LocalProfile=null};
            Check(new HistoryVm(withoutProfile,row.Mine,row.Other).RoundScoresLabel.Contains("39∶103"),"Saved compact round scores remain visible when no profile sidecar is present.");
            Check(new HistoryVm(withoutProfile with{RoundScores=[]},row.Mine,row.Other).RoundScoresLabel.Contains("尚未识别"),"Unavailable scores show a clear missing-data label.");

            var card=catalog.First(c=>c.CanBeInStartingDeck);
            var rows=Enumerable.Range(1,35).Select(i=>new CardVm("滚动检查 "+i,"","效果",null,i.ToString(),"7","",Brushes.LightGreen,Brushes.Gold,1,card)).ToArray();
            var list=new DeckCardList{Rows=rows};var activated=0;list.RowActivated+=(_,_)=>activated++;
            var content=new StackPanel();content.Children.Add(list);content.Children.Add(new Border{Height=700});
            var outer=new ScrollViewer{Content=content,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
            wheelWindow=new Window{Content=outer,Width=800,Height=600};wheelWindow.Show();await Task.Delay(80);wheelWindow.UpdateLayout();
            async Task Wheel(FrameworkElement source,int delta)
            {
                source.RaiseEvent(new MouseWheelEventArgs(Mouse.PrimaryDevice,Environment.TickCount,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent,Source=source});
                await Task.Delay(60);wheelWindow.UpdateLayout();
            }
            Button CardButton()=>Descendants(list).OfType<Button>().First(b=>CardInspection.GetCard(b) is not null);
            var inner=Descendants(list).OfType<ScrollViewer>().First();
            Check(inner.ScrollableHeight==0 && outer.ScrollableHeight>0,"The history-style nested card list reproduces an unscrollable inner viewer inside a scrollable page.");
            await Wheel(CardButton(),-120);
            Check(outer.VerticalOffset>0,"Wheel input over a card row scrolls the containing history page.");
            var before=outer.VerticalOffset;await Wheel(CardButton(),120);
            Check(outer.VerticalOffset<before,"Wheel-up over a card row returns the containing page upward.");
            var info=Descendants(list).OfType<Button>().First(b=>Equals(b.Content,"ⓘ"));await Wheel(info,-120);
            Check(outer.VerticalOffset>0,"The card information button also allows wheel scrolling.");
            list.Height=240;outer.ScrollToTop();await Task.Delay(60);wheelWindow.UpdateLayout();
            Check(inner.ScrollableHeight>0,"A constrained library-style list retains its own scroll range.");
            await Wheel(CardButton(),-120);
            Check(inner.VerticalOffset>0 && outer.VerticalOffset==0,"A bounded card list scrolls its cards before moving the parent page.");
            // Virtualized rows can revise the extent after the first end-scroll
            // measures them. Settle the real endpoint before testing handoff.
            for(var settle=0;settle<4;settle++) {
                inner.ScrollToBottom();await Task.Delay(100);wheelWindow.UpdateLayout();
                if(inner.VerticalOffset>=inner.ScrollableHeight-.01)break;
            }
            Check(inner.VerticalOffset>=inner.ScrollableHeight-.01,"The scrolling fixture has reached the measured end of its virtualized card list.");
            await Wheel(CardButton(),-120);
            Check(outer.VerticalOffset>0,$"At the end of the card list, wheel input continues to the outer page (inner={inner.VerticalOffset}/{inner.ScrollableHeight}, outer={outer.VerticalOffset}/{outer.ScrollableHeight}).");
            Check(activated==0,"Wheel navigation never adds, removes, selects or opens a card.");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"Display and scrolling preserve the raw match record.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{wheelWindow?.Close();history?.Close();Localization.DeckDisplayNames.Configure(Path.Combine(root,"cache","deck-display-names.json"));}
    }

    internal static async Task<int> AuditResultDisplayAsync(string report,Guid? matchId=null)
    {
        MatchAnalysisWindow? window=null;
        try
        {
            var root=AppContext.BaseDirectory;var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GwentVision","match-data");
            window=new MatchAnalysisWindow(directory,root);await window.RefreshAsync();await WaitForDeckNames(window);
            DeckBuilderSmoke.Render(window,Path.ChangeExtension(report,"summary.png"),1920,1080);
            var expanded=await ExpandFirstResult(window,matchId);var row=(HistoryVm)expanded.DataContext;
            var details=(DetailsVm)((ContentControl)expanded.Content).Content;
            DeckBuilderSmoke.Render(window,Path.ChangeExtension(report,"png"),1920,1080);
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=true,row.Entry.MatchId,row.ResultRanks,row.RoundScoresLabel,Completed=window._completed.Length,
                details.CaptureWarning,OwnJournalCards=details.Mine.Groups.SelectMany(g=>g.Cards).Select(c=>new{Id=c.Card?.Id,c.Badge}),
                OpponentJournalCards=details.Other.Groups.SelectMany(g=>g.Cards).Select(c=>new{Id=c.Card?.Id,c.Badge}),
                OwnRecommendations=details.UserCandidates?.Matches.Count,OpponentRecommendations=details.Candidates.Matches.Count},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
        finally{window?.Close();}
    }
}
