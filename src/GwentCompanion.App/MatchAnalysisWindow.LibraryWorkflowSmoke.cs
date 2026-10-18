using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    internal static async Task<int> RunLibraryWorkflowAuditAsync(string report,string? dataDirectory=null)
    {
        MatchAnalysisWindow? window=null;
        try
        {
            var directory=dataDirectory??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"GwentVision","match-data");
            var hashes=Directory.GetFiles(directory,"*",SearchOption.AllDirectories).Where(p=>p.EndsWith(".gvm") || p.EndsWith(".json")).ToDictionary(p=>p,p=>SHA256.HashData(File.ReadAllBytes(p)));
            window=new MatchAnalysisWindow(directory,AppContext.BaseDirectory);await window.RefreshAsync();window.DisplayChoice.SelectedIndex=2;window.Render();
            DeckBuilderSmoke.Render(window,Path.ChangeExtension(report,"png"),1920,1080);
            var settings=new LadderProgressStore(directory).Load();var snapshot=window._ladderSnapshot;
            var preserved=hashes.All(p=>p.Value.SequenceEqual(SHA256.HashData(File.ReadAllBytes(p.Key))));
            File.WriteAllText(report,JsonSerializer.Serialize(new{Passed=preserved,UserDataPreserved=preserved,Completed=window._completed.Length,
                Current=snapshot?.Current,HistoricalGames=snapshot?.Series.Caption,Points=snapshot?.Series.Points,
                CurrentGames=snapshot?.Games.Length,snapshot?.Reason,ProgressCharts=window.ProgressCharts.Items.Count,Checkpoints=settings?.PreviousCalibrations.Length+1},new JsonSerializerOptions{WriteIndented=true}));return preserved?0:1;
        }
        catch(Exception error){File.WriteAllText(report,error.ToString());return 1;}
        finally{window?.Close();}
    }
    internal static async Task<int> RunLibraryWorkflowSmokeAsync()
    {
        var root=AppContext.BaseDirectory;var output=Path.Combine(root,"diagnostics","library-workflow-smoke");Directory.CreateDirectory(output);
        var checks=new List<string>();DeckLibraryPickerWindow? picker=null;MatchEvidenceWindow? editorWindow=null;MatchReviewWindow? reviewWindow=null;DeckBuilderWindow? builder=null;
        void Check(bool ok,string why){if(!ok)throw new InvalidOperationException(why);checks.Add(why);}
        try
        {
            var catalog=GwentOneCardCatalog.Load(Path.Combine(root,"cache","gwent-one-cards.json"));var library=DeckLibrary.Load(Path.Combine(root,"cache","deck-library.json"));
            picker=new DeckLibraryPickerWindow(library.Decks,catalog);await picker.RefreshAsync();
            Check(picker.Results.Items.Count<=DeckLibraryPickerWindow.PageSize && picker.Results.Items.Count>0,"Library selection materializes at most one 40-item page");
            picker.SearchBar.Faction.SelectedItem="Scoia'tael";await picker.RefreshAsync();
            Check(picker.Results.Items.Cast<object>().All(e=>((DeckDefinition)e.GetType().GetProperty("Deck")!.GetValue(e)!).Faction=="Scoia'tael"),"The shared selector filters by faction before binding results");
            picker.Results.SelectedIndex=0;Check(picker.UseButton.IsEnabled,"Selecting one result enables the return-to-caller action");
            DeckBuilderSmoke.Render(picker,Path.Combine(output,"library-picker.png"),1280,900);picker.Close();picker=null;
            var deck=library.Decks.First(d=>d.CardCount==25 && d.Cards.All(c=>c.Count<=(c.Card.IsGold?1:2)));
            var extra=catalog.First(c=>StartingDeckRules.IsLegalStartingCard(c,deck.Faction) && deck.CountOf(c.Id)==0);
            var directory=Path.Combine(output,"fixtures",Guid.NewGuid().ToString("N"));var unknown=new MatchPlayer(null,null,null,[],[],[]);
            var source=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,17),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,unknown,
                unknown with{Faction=deck.Faction,Observations=[new(extra.Id,1,MatchCardEvidence.Observed,CardProvenance.ProbableStartingDeck,254)]},[],[]);
            var path=new LocalMatchStore(directory).Save(source);var hash=SHA256.HashData(File.ReadAllBytes(path));var store=new MatchReviewStore(directory);
            store.Set(source,new(Result:"VICTORY",IncludeInStatistics:true,Note:"Officially verified by user"));
            var editor=new OpponentEvidenceEditor(directory,MatchReviewStore.Key(source),[new(extra,CardProvenance.ProbableStartingDeck,1,DateTimeOffset.MinValue)],deck.Faction).WithCatalog(catalog);
            editorWindow=new MatchEvidenceWindow(editor,catalog,deck.Faction,library.Decks);editorWindow.LoadDraft(deck);
            Check(editor.Corrections.Count==0 && editorWindow.SaveDraft.Visibility==Visibility.Visible,"Loading a draft does not write card corrections before explicit confirmation");
            DeckBuilderSmoke.Render(editorWindow,Path.Combine(output,"bulk-draft-initial.png"),860,900);
            var remove=deck.Cards[0];
            var minus=Descendants((DependencyObject)editorWindow.Content).OfType<Button>().First(b=>Equals(b.Content,"−") && b.Tag is ValueTuple<CardDefinition,int> tag && tag.Item1.Id==remove.Card.Id);
            minus.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Check(editor.Corrections.Count==0,"Adjusting an unsaved draft does not pretend those cards were observed");
            DeckBuilderSmoke.Render(editorWindow,Path.Combine(output,"bulk-draft.png"),860,900);
            editorWindow.SaveDraft.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Evidence().Sum(c=>c.ObservedCopies)==25 && editor.Count(extra.Id)==1,"One confirmation imports a whole edited deck while keeping the existing off-list observation");
            var effective=MatchReviewStore.Apply(source,store.Get(source));
            Check(effective.Opponent.Observations.Single(c=>c.CardId==extra.Id).Evidence==MatchCardEvidence.Observed && effective.Opponent.Observations.Any(c=>c.Evidence==MatchCardEvidence.Remembered),"Existing automatic sightings and imported human corrections keep distinct provenance");
            Check(store.Get(source) is {Result:"VICTORY",IncludeInStatistics:true,Note:"Officially verified by user"},"Bulk deck import preserves the user's officially verified outcome");
            Descendants((DependencyObject)editorWindow.Content).OfType<Button>().Single(b=>Equals(b.Content,"撤销上次批量补录")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(editor.Evidence().Length==1 && editor.Corrections.Count==0,"One undo restores the exact card state before the bulk edit");
            Check(hash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(path))),"All deck-selection and bulk-edit actions preserve immutable raw match data");
            reviewWindow=new MatchReviewWindow(source,effective,store.Get(source),catalog,library.Decks);
            DeckBuilderSmoke.Render(reviewWindow,Path.Combine(output,"review-selector-button.png"),760,1100);
            Check(Descendants((DependencyObject)reviewWindow.Content).OfType<ComboBox>().All(b=>b.Items.Count<200) && Descendants((DependencyObject)reviewWindow.Content).OfType<Button>().Contains(reviewWindow.ChooseDeckButton),"Match editing uses the shared library button instead of creating thousands of drop-down items");
            builder=new DeckBuilderWindow(library,Path.Combine(directory,"deck-library.json"),catalog,deck,()=>{});
            picker=builder.CreateTemplatePicker();await picker.RefreshAsync();
            Check(builder.UseTemplateButton.IsEnabled && picker.Results.Items.Count>0 && picker.Results.Items.Count<=DeckLibraryPickerWindow.PageSize,"The deck builder also opens the same paginated library picker for its starting template");
            DeckBuilderSmoke.Render(builder,Path.Combine(output,"builder-library-entry.png"),1320,900);picker.Close();picker=null;
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=true,Checks=checks},new JsonSerializerOptions{WriteIndented=true}));return 0;
        }
        catch(Exception error){File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new{Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally{picker?.Close();editorWindow?.Close();reviewWindow?.Close();builder?.Close();}
    }
}
