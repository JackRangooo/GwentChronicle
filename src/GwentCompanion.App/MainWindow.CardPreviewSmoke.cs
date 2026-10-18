using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using GwentCompanion.App.Controls;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunCardPreviewSmokeAsync()
    {
        static void Check(bool condition, string message)
        { if (!condition) throw new InvalidOperationException(message); }

        MainWindow window;
        try { window = new MainWindow(); }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine("Card preview smoke could not create its host: " + error);
            return 1;
        }
        OpponentReviewWindow? editor = null;
        try
        {
            window.Loaded -= window.OnLoaded;
            window.WindowState = WindowState.Normal;
            window.Width = 1100; window.Height = 760;
            window.HideLoadingShell();
            window.Show();
            IReadOnlyList<CardDefinition> cards = GwentOneCardCatalog.Load(CardDataPath);
            bool HasChinese(string value) => value.Any(character => character is >= '\u3400' and <= '\u9fff');
            var supportedFactions = GwentOneCardCatalog.StartingLeaders(cards).Select(card => card.Faction)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var samples = cards.Where(card => card.CanBeInStartingDeck && !string.IsNullOrWhiteSpace(card.AbilityText))
                .Select(card => (Card: card, Ability: UiTranslation.Translate(card.AbilityText)))
                .Where(item => item.Ability.Length is > 0 and <= 650 && HasChinese(item.Ability) && supportedFactions.Contains(item.Card.Faction))
                .Where(item => new FileInfo(Path.Combine(ResolveArtCacheDirectory(), item.Card.Id + ".jpg")) is { Exists: true, Length: > 10000 })
                .Take(2).ToArray();
            Check(samples.Length == 2, "Need two locally cached cards with translated ability text and full portraits.");

            var candidate = samples[0].Card;
            var observed = samples[1].Card;
            var candidateSlot = new ProjectedDeckSlot(0, candidate, 1, DeckSlotState.Predicted, null, null, false,
                "Offline preview fixture; candidate is not evidence.");
            var observedSlot = new ProjectedDeckSlot(1, observed, 1, DeckSlotState.Observed, null, null, false,
                "Offline preview fixture; identity already observed.");
            window.CandidateCardTray.Rows = new[] { window.Strip(candidateSlot) };
            window.OpponentDeckCards.Rows = new[] { window.Strip(observedSlot) };
            var beforeEvidence = string.Join('|', window._opponentTracker.DeckBuildingObservations.Select(item =>
                $"{item.Card.Id}:{item.ObservedCopies}:{item.Provenance}"));
            string ChoiceState() => string.Join('|', window._opponentEdits.Included.Keys.OrderBy(item => item.CardId).ThenBy(item => item.Copy)
                .Select(item => $"included:{item.CardId}:{item.Copy}")
                .Concat(window._opponentEdits.Excluded.OrderBy(item => item.CardId).ThenBy(item => item.Copy).Select(item => $"excluded:{item.CardId}:{item.Copy}"))
                .Concat(window._opponentEdits.CorrectedEvidence.OrderBy(item => item.CardId).ThenBy(item => item.Copy).Select(item => $"corrected:{item.CardId}:{item.Copy}")));
            var beforeChoice = ChoiceState();

            var output = Path.Combine(Directory.GetCurrentDirectory(), "diagnostics", "card-preview-smoke");
            Directory.CreateDirectory(output);
            window.ShowPage(UiPage.Candidates);
            window.UpdateWorkspaceLayout(1100);
            DeckBuilderSmoke.Render(window, Path.Combine(output, "candidate-row.png"), 1100, 760);
            var candidateInspect = FindPreviewButton(window.CandidateCardTray);
            Check(candidateInspect is not null, "Candidate row does not expose its separate detail action.");
            candidateInspect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, candidateInspect));
            var preview = window._cardPreviewWindow;
            Check(preview is { IsVisible: true } && preview.Card.Id == candidate.Id,
                "Candidate detail action did not open the card preview.");
            Check(preview!.Portrait is not null && HasChinese(preview.Ability), "Candidate preview lacks full artwork or translated ability text.");
            DeckBuilderSmoke.Render(preview, Path.Combine(output, "candidate-card-preview.png"), 790, 570);
            preview.Close();

            window.ShowPage(UiPage.Deck);
            window.UpdateWorkspaceLayout(1100);
            DeckBuilderSmoke.Render(window, Path.Combine(output, "observed-row.png"), 1100, 760);
            var observedInspect = FindPreviewButton(window.OpponentDeckCards);
            Check(observedInspect is not null, "Observed opponent row does not expose its separate detail action.");
            observedInspect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, observedInspect));
            Check(window._cardPreviewWindow is { IsVisible: true } opponentPreview && opponentPreview.Card.Id == observed.Id,
                "Observed opponent detail action did not open the card preview.");
            Check(beforeEvidence == string.Join('|', window._opponentTracker.DeckBuildingObservations.Select(item =>
                      $"{item.Card.Id}:{item.ObservedCopies}:{item.Provenance}")) &&
                  beforeChoice == ChoiceState(),
                "Opening a card preview changed opponent evidence or candidate choices.");

            var reviewRecord = new LearnedOpponentDeck("card-preview-smoke", "Card preview smoke", DateTimeOffset.UtcNow,
                [], NeedsReview: true,
                ReviewedCards: [new ObservedCard(observed, CardProvenance.ProbableStartingDeck, 1, DateTimeOffset.UtcNow, "offline smoke")],
                ReviewedHeader: new(observed.Faction, null, null, null));
            editor = new OpponentReviewWindow(reviewRecord, cards, new DeckLibrary(), LibraryPath, () => { }, (_, _) => { });
            editor.WindowState = WindowState.Normal;
            editor.Width = 1160; editor.Height = 760;
            editor.Show();
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (editor.BuilderStatus.Text.StartsWith("Updating", StringComparison.Ordinal) && DateTimeOffset.UtcNow < deadline)
                await Task.Delay(30);
            Check(!editor.BuilderStatus.Text.StartsWith("Could not", StringComparison.Ordinal), "Opponent editor could not build its fixture: " + editor.BuilderStatus.Text);
            DeckBuilderSmoke.Render(editor, Path.Combine(output, "opponent-review.png"), 1160, 760);
            var draftInspect = FindPreviewButton(editor.DraftCards);
            Check(draftInspect is not null, "Opponent-review draft rows do not expose a separate detail action.");
            draftInspect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, draftInspect));
            Check(editor.ActiveCardPreview is { IsVisible: true } draftPreview && draftPreview.Card.Id == observed.Id,
                "Opponent-review draft detail action did not open its card preview.");
            Check(editor.ActiveCardPreview!.Portrait is not null && HasChinese(editor.ActiveCardPreview.Ability),
                "Opponent-review card preview lacks artwork or translated ability.");
            DeckBuilderSmoke.Render(editor.ActiveCardPreview, Path.Combine(output, "opponent-review-card-preview.png"), 790, 570);
            editor.ActiveCardPreview.Close();
            var collectionInspect = FindPreviewButton(editor.Collection);
            Check(collectionInspect is not null, "Opponent-review collection rows do not expose a separate detail action.");
            collectionInspect!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, collectionInspect));
            Check(editor.ActiveCardPreview is { IsVisible: true }, "Opponent-review collection detail action did not open the card preview.");
            var result = new
            {
                Passed = true,
                Checks = new[]
                {
                    "Main candidate detail button opened a full portrait and Chinese ability.",
                    "Main observed-opponent detail button opened its card preview.",
                    "Opponent-review draft detail button opened a full portrait and Chinese ability.",
                    "Opponent-review collection detail button opened its card preview.",
                    "Preview actions left observed evidence and candidate choices unchanged."
                },
                TranslatedPortraitSamples = samples.Length,
                Screenshots = new[] { "candidate-card-preview.png", "opponent-review-card-preview.png", "candidate-row.png", "observed-row.png", "opponent-review.png" }
            };
            File.WriteAllText(Path.Combine(output, "result.json"), System.Text.Json.JsonSerializer.Serialize(result,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            var previousFailure = Path.Combine(output, "failure.txt");
            if (File.Exists(previousFailure)) File.Delete(previousFailure);
            return 0;
        }
        catch (Exception error)
        {
            System.Diagnostics.Trace.WriteLine("Card preview smoke failed: " + error);
            var failureDirectory = Path.Combine(Directory.GetCurrentDirectory(), "diagnostics", "card-preview-smoke");
            Directory.CreateDirectory(failureDirectory);
            var staleResult = Path.Combine(failureDirectory, "result.json");
            if (File.Exists(staleResult)) File.Delete(staleResult);
            File.WriteAllText(Path.Combine(failureDirectory, "failure.txt"), error.ToString());
            return 1;
        }
        finally
        {
            editor?.ActiveCardPreview?.Close();
            editor?.Close();
            window._cardPreviewWindow?.Close();
            window.Close();
        }
    }

    private static Button? FindPreviewButton(DependencyObject root)
    {
        if (root is Button button && AutomationProperties.GetName(button).StartsWith("View details for ", StringComparison.Ordinal))
            return button;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindPreviewButton(VisualTreeHelper.GetChild(root, index)) is { } found) return found;
        return null;
    }
}
