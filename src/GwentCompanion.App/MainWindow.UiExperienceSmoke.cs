using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunUiExperienceSmokeAsync()
    {
        var output=Path.Combine(AppContext.BaseDirectory,"diagnostics","ui-experience-smoke");Directory.CreateDirectory(output);
        var fixture=Path.Combine(output,"fixture");Directory.CreateDirectory(fixture);
        var checks=new List<string>();MainWindow? window=null;MatchAnalysisWindow? history=null;DeckBuilderWindow? builder=null;
        void Check(bool ok,string note){if(!ok)throw new InvalidOperationException(note);checks.Add(note);}
        var originalHash=SHA256.HashData(File.ReadAllBytes(LibraryPath));
        try {
            var catalog=GwentOneCardCatalog.Load(CardDataPath);var library=DeckLibrary.Load(LibraryPath);
            var original=library.Records.Select(r=>(r.Fingerprint,r.Deck.Name,r.CustomName)).OrderBy(r=>r.Fingerprint).ToArray();
            var oldGroups=library.VariationGroups.Count;
            var beforeSy=library.Decks.Where(d=>d.Faction=="Syndicate" && d.Leader=="Lined Pockets").Select(d=>library.VariationGroupFor(d.Id)!.Id).Distinct().Count();
            library.UpgradeCoreVariationGroups(catalog);
            Check(original.SequenceEqual(library.Records.Select(r=>(r.Fingerprint,r.Deck.Name,r.CustomName)).OrderBy(r=>r.Fingerprint)),"Core migration preserves exact compositions and user deck names.");
            var metric=new DeckVariationSimilarity(library.VariationPolicy!.Prices);
            Check(library.VariationPolicy.Metric==DeckVariationPolicy.CoreMetric && library.VariationGroups.All(g=>library.GroupVariants(g).All(a=>library.GroupVariants(g).All(b=>metric.IsCoreVariation(a.Deck,b.Deck)))),"Every pair in a real cluster satisfies the gold-core criteria and construction gates.");
            library.Save(Path.Combine(fixture,"library.json"));library.ExportTransfer(Path.Combine(fixture,"portable.json"));
            Check(new DeckLibrary().PreviewTransfer(Path.Combine(fixture,"portable.json")).Library.VariationGroups.Count==library.VariationGroups.Count,"Core clusters round-trip through portable libraries.");
            var sy=library.Decks.Where(d=>d.Faction=="Syndicate" && d.Leader=="Lined Pockets").Select(d=>library.VariationGroupFor(d.Id)!.Id).Distinct().Count();
            var deck=library.Decks.First(d=>d.Cards.Any(c=>c.Card.AbilityText is {Length:>20} && File.Exists(Path.Combine(CardInspection.PortraitsDirectory,c.Card.Id+".jpg"))));
            var legacy=new DeckLibrary(useLegacyVariationPolicy:true);legacy.Merge([deck]);legacy.ExportTransfer(Path.Combine(fixture,"legacy-import.json"));
            var imported=new DeckLibrary().PreviewTransfer(Path.Combine(fixture,"legacy-import.json"));imported.Library.UpgradeCoreVariationGroups(catalog);
            Check(imported.Library.VariationPolicy!.Metric==DeckVariationPolicy.CoreMetric,"Old-format imports into an empty library are upgraded before final save.");
            var card=deck.Cards.First(c=>c.Card.AbilityText is {Length:>20} && File.Exists(Path.Combine(CardInspection.PortraitsDirectory,c.Card.Id+".jpg"))).Card;
            window=new MainWindow();window.Loaded-=window.OnLoaded;window._matchStorageDirectoryOverride=Path.Combine(fixture,"matches");
            window._library=library;window._candidateCatalog=catalog;window._cachedDecks=library.Decks;window._libraryReady=true;
            DeckDisplayNames.ConfigureGroups(library);DeckDisplayNames.Configure(Path.Combine(fixture,"aliases.json"));
            window.HideLoadingShell();window.Width=394;window.Height=1032;window.Show();window.ShowPage(UiPage.Library);
            window.LibrarySourceChoice.SelectedIndex=0;
            var slot=new ProjectedDeckSlot(1,card,1,DeckSlotState.Observed,null,null,false,"fixture direct sighting");
            var observation=new DeckListItem("对手观察记录 · 界面检查","特别复杂的说明 "+string.Join("，",Enumerable.Repeat(card.Name,20)),null,null,
                FilterFaction:deck.Faction,FilterLeader:deck.Leader,RecordedAt:DateTimeOffset.Now,PartialSlots:[slot],DraftSourceKey:"observed:Opponent:ui-fixture");
            window.SetDeckListItems(window.CreateDeckItems(window._cachedDecks).Prepend(observation));
            await Task.Delay(120);DeckBuilderSmoke.Render(window,Path.Combine(output,"compact-browse.png"),394,1032);window.UpdateLayout();
            Check(window.DeckList.ActualHeight>=430 && window.LibraryDeckCards.Visibility==Visibility.Collapsed,"Compact browsing gives the full remaining height to the deck list.");
            Check(!observation.Summary.Contains("特别复杂"),"Long recognition journals are kept out of result rows.");
            var heading=VisualChildren(window.DeckList).OfType<DeckHeading>().First();Check(heading.ActualHeight<=65,"Result headings stay within two compact lines.");
            window.LibraryDeckPreview_OnClick(new Button{DataContext=observation},new RoutedEventArgs());
            var deadline=DateTime.UtcNow.AddSeconds(25);while(window.LibraryMatchesPanel.Report is null && DateTime.UtcNow<deadline)await Task.Delay(25);
            DeckBuilderSmoke.Render(window,Path.Combine(output,"compact-details.png"),394,1032);
            Check(window.DeckList.Visibility==Visibility.Collapsed && window.LibraryDeckCards.ActualHeight>=400 && window.LibraryBrowseButton.Visibility==Visibility.Visible,"Compact selection opens a full-height card view with an explicit return action.");
            Check(window.LibraryRenameSelectedButton.IsVisible && window.LibraryRenameSelectedButton.IsEnabled &&
                window.LibraryRenameSelectedButton.ActualWidth>=50,"Compact deck details expose an enabled, readable rename button.");
            Check(!window.LibraryMatchesExpander.IsExpanded && window.LibraryMatchesPanel.Report is {HasEvidence:true,HasMatches:false},"Sparse evidence exposes a reason but cannot recommend a complete deck.");
            var rowButton=VisualChildren(window.LibraryDeckCards).OfType<Button>().First(b=>CardInspection.GetCard(b)?.Id==card.Id);
            Check(rowButton.ToolTip is ToolTip,"Library cards have the shared hover inspector attached.");
            rowButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,rowButton));
            Check(window._cardPreviewWindow is {IsVisible:true,Portrait:not null} p && p.Card.Id==card.Id,"Clicking a library card opens its full portrait and ability.");
            DeckBuilderSmoke.Render(window._cardPreviewWindow!,Path.Combine(output,"library-card-details.png"),790,570);window._cardPreviewWindow!.Close();
            var hover=CardInspection.CreateHover(card);
            DeckBuilderSmoke.Render(new Window{Content=hover},Path.Combine(output,"card-hover.png"),480,440);
            Check(VisualChildren(hover).OfType<Image>().Any(i=>i.Source is not null) && VisualChildren(hover).OfType<TextBlock>().Any(t=>t.Text==UiTranslation.Translate(card.AbilityText!)),"Hover inspection contains the portrait and complete translated ability.");
            window.LibraryBrowse_OnClick(window.LibraryBrowseButton,new RoutedEventArgs());
            Check(ReferenceEquals(window.DeckList.SelectedItem,observation) && window.DeckList.Visibility==Visibility.Visible,"Returning restores the selection and existing search list.");
            var groupRow=window.DeckList.Items.Cast<DeckListItem>().First(i=>i.Variations?.Length>1);
            window.VariantPreview_OnClick(new Button{DataContext=groupRow.Variations!.Last()},new RoutedEventArgs());
            Check(window.LibraryInspector.Visibility==Visibility.Visible && window.DeckList.Visibility==Visibility.Collapsed,"Clicking a specific variant in compact mode opens its card view.");
            window.LibraryBrowse_OnClick(window.LibraryBrowseButton,new RoutedEventArgs());
            window.LibrarySourceChoice.SelectedIndex=1;
            Check(window.DeckList.Items.Cast<DeckListItem>().All(i=>i.Deck is not null || i.IndexEntry is not null),"The persistent source filter separates complete decks from observations.");
            window.LibrarySourceChoice.SelectedIndex=0;
            window.OpenLibraryWorkspace();await Task.Delay(100);window.UpdateLayout();
            Check(window._expandedWorkspace && window._libraryWide,"The library opens a dedicated larger two-column workspace.");
            Check(VisualChildren(window.DeckList).OfType<Button>().Any(b=>Equals(b.Content,"重命名") && b.IsVisible),"Library result rows expose rename directly without opening More actions.");
            window.DeckList.SelectedItem=window.DeckList.Items.Cast<DeckListItem>().First(i=>i.Deck?.Id==deck.Id || i.Variations?.Any(v=>v.Deck?.Id==deck.Id)==true);
            DeckBuilderSmoke.Render(window,Path.Combine(output,"wide-library.png"),1280,940);
            window.ShowPage(UiPage.Deck);
            Check(window._expandedWorkspace && Math.Abs(window.Width-Math.Min(1280,SystemParameters.WorkArea.Width-32))<3,"Returning to live preserves the explicitly selected workspace size instead of resizing on navigation.");

            builder=new DeckBuilderWindow(library,Path.Combine(fixture,"builder-library.json"),catalog,deck,()=>{});builder.Show();builder.UpdateLayout();
            var inspect=VisualChildren(builder.DraftCards).OfType<Button>().First(b=>Equals(b.Content,"ⓘ") && b.Visibility==Visibility.Visible);
            inspect.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,inspect));
            Check(builder.ActiveCardPreview is {IsVisible:true},"Normal deck editing exposes card details independently of add/remove actions.");builder.ActiveCardPreview!.Close();builder.Close();builder=null;
            var player=MatchReviewStore.Reference(deck) with {Observations=[new(card.Id,1,MatchCardEvidence.Observed,CardProvenance.ProbableStartingDeck,240)]};
            var raw=new CompactMatch(Guid.NewGuid(),Guid.NewGuid(),new(2026,10,15),"fixture","fixture","14.10",false,1,true,true,"DEFEAT",null,null,null,false,null,player,player,[],[]);
            var rawPath=new LocalMatchStore(window.MatchStorageDirectory).Save(raw);var rawHash=SHA256.HashData(File.ReadAllBytes(rawPath));
            history=new MatchAnalysisWindow(window.MatchStorageDirectory,FindDataRoot());history.Show();await history.RefreshAsync();
            await Task.Delay(120);history.UpdateLayout();
            var expanded=VisualChildren(history).OfType<Expander>().First(e=>e.DataContext?.GetType().Name=="HistoryVm");expanded.IsExpanded=true;
            deadline=DateTime.UtcNow.AddSeconds(20);while(!VisualChildren(expanded).OfType<DeckCardList>().Any() && DateTime.UtcNow<deadline)await Task.Delay(25);history.UpdateLayout();
            var historyCard=VisualChildren(expanded).OfType<Button>().First(b=>CardInspection.GetCard(b)?.Id==card.Id);
            historyCard.RaiseEvent(new RoutedEventArgs(Button.ClickEvent,historyCard));
            Check(history.OwnedWindows.OfType<CardPreviewWindow>().Any(p=>p.IsVisible && p.Card.Id==card.Id),"Historical card rows support full-card inspection.");
            Check(rawHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(rawPath))) && SHA256.HashData(File.ReadAllBytes(LibraryPath)).SequenceEqual(originalHash),"All UI operations leave detector records and the live library unchanged.");
            File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=true,Checks=checks,Decks=library.Records.Count,BeforeClusters=oldGroups,AfterClusters=library.VariationGroups.Count,
                BeforeLinedPocketsClusters=beforeSy,AfterLinedPocketsClusters=sy,CompactListHeight=window.DeckList.ActualHeight},new JsonSerializerOptions{WriteIndented=true}));return 0;
        } catch(Exception error) {File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Passed=false,Checks=checks,Error=error.ToString()},new JsonSerializerOptions{WriteIndented=true}));return 1;}
        finally {history?.Close();builder?.Close();if(window is not null){window._projectionQueue?.Dispose();window.Close();}DeckDisplayNames.Configure(Path.Combine(FindDataRoot(),"cache","deck-display-names.json"));}
    }
}
