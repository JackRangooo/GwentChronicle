using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> AuditOpponentMatchesAsync(string[] args)
    {
        var reportPath=args.Last();
        try
        {
            var root=AppContext.BaseDirectory;
            DeckDisplayNames.Configure(Path.Combine(root,"cache","deck-display-names.json"));
            var directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GwentVision","match-data");
            var hashes=Directory.GetFiles(directory,"*.gvm",SearchOption.AllDirectories).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            var window=new MatchAnalysisWindow(directory,root);await window.RefreshAsync();window.PatchChoice.SelectedIndex=0;
            var store=new MatchReviewStore(directory);var output=Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!,"opponent-match-previews");Directory.CreateDirectory(output);
            var reports=new List<object>();
            foreach(var entry in window._completed)
            {
                var raw=LocalMatchStore.Read(entry.Path);var match=MatchReviewStore.Apply(raw,store.Get(raw));
                var candidates=OpponentDeckMatcher.Match(window._libraryDecks,match.Opponent,window._catalog,match.Patch);
                reports.Add(new {entry.MatchId,Time=entry.StartedAtUtc?.ToLocalTime(),entry.OpponentFaction,entry.OpponentLeader,
                    Outcome=entry.Outcome.ToString(),candidates.TotalObservedCopies,candidates.CompatibleDistinctDeckCount,candidates.TopTieCount,candidates.NoMatchReason,
                    TopTwo=candidates.Matches.Select(h=>new {h.Deck.Id,Name=DeckDisplayNames.Display(h.Deck.Id,h.Deck.Name),h.Deck.Faction,h.Deck.Leader,
                        h.ScorePercent,h.ObservedCoveragePercent,h.MatchedCopies,h.TotalObservedCopies,Missing=h.MissingObservedCards.Select(c=>new {Name=UiTranslation.CardName(c.Card.Id,c.Card.Name),c.Copies})})});
                if(window._completed.Length<=12)
                {
                    DeckBuilderSmoke.Render(window,Path.Combine(output,"history.png"),1600,1100);
                    var row=Descendants(window.HistoryList).OfType<Expander>().FirstOrDefault(e=>e.DataContext is HistoryVm vm && vm.Entry.MatchId==entry.MatchId);
                    if(row is not null)
                    {
                        row.IsExpanded=true;var deadline=DateTime.UtcNow.AddSeconds(15);
                        while(row.Content is not ContentControl {Content:DetailsVm} && DateTime.UtcNow<deadline)await Task.Delay(25);
                        if(row.Content is not ContentControl {Content:DetailsVm details} || details.Candidates.Matches.Count!=candidates.Matches.Count)
                            throw new InvalidOperationException("Historical detail binding did not expose its matching candidates.");
                        if(window.HistoryList.Parent is ScrollViewer scroll) scroll.ScrollToVerticalOffset(0);
                        DeckBuilderSmoke.Render(window,Path.Combine(output,entry.MatchId.ToString("N")+".png"),1600,1100);
                        row.IsExpanded=false;
                    }
                }
            }
            var preserved=hashes.All(h=>h.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(h.Key))));
            File.WriteAllText(reportPath,JsonSerializer.Serialize(new {Passed=preserved,RawRecordsPreserved=preserved,CompletedGames=reports.Count,
                LibraryDecks=window._libraryDecks.Length,Matches=reports,PreviewDirectory=output},new JsonSerializerOptions{WriteIndented=true}));
            return preserved?0:1;
        }
        catch(Exception error){File.WriteAllText(reportPath,error.ToString());return 1;}
    }
}
