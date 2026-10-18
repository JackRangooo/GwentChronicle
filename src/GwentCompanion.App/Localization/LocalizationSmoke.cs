using System.ComponentModel;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using GwentCompanion.Core.Vision;
using GwentCompanion.Platform.Windows.Vision;

namespace GwentCompanion.App;

public partial class MainWindow
{
    internal static async Task<int> RunLocalizationSmokeAsync()
    {
        var folder = Path.Combine(AppContext.BaseDirectory, "diagnostics", "chinese-ui-smoke");
        Directory.CreateDirectory(folder);
        var checks = new List<string>();
        void Check(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
            checks.Add(message);
        }
        try
        {
            Check(UiTranslation.CardCount == 1381, "All 1381 card IDs have Chinese names");
            Check(UiTranslation.UiCount > 150, "Main UI translation dictionary is present");
            var name = new LocalizedTextBlock { Text = "Ciri" };
            Check(name.Text == "Ciri" && name.DisplayText == "希里", "Source card name stays English while its rendered value is Chinese");
            var source = new NameFixture { Name = "Ciri" };
            var bound = new LocalizedTextBlock();
            bound.SetBinding(LocalizedTextBlock.TextProperty, new Binding(nameof(NameFixture.Name)) { Source = source });
            Check(bound.DisplayText == "希里" && source.Name == "Ciri", "Chinese binding does not mutate the model");
            source.Name = "Geralt: Igni";
            Check(bound.Text == source.Name && bound.DisplayText == "杰洛特：伊格尼法印", "Binding updates translate newly detected card names");
            Check(ChineseDeckSearchCatalog.Matches(ChineseDeckSearchCatalog.Normalize("Geralt of Rivia Unit Witcher"), ["杰洛特"]), "Partial Chinese card-name search matches English records");
            Check(!ChineseDeckSearchCatalog.Matches(ChineseDeckSearchCatalog.Normalize("Ciri Unit Witcher"), ["杰洛特"]), "Chinese search rejects unrelated cards");
            Check(ChineseDeckSearchCatalog.Matches(ChineseDeckSearchCatalog.Normalize("Ciri Unit Witcher"), ["ciri", "witcher"]), "Existing English multi-term search remains valid");

            var cataloguePath = Path.Combine(FindDataRoot(), "cache", "gwent-one-cards.json");
            var libraryHash = SHA256.HashData(File.ReadAllBytes(LibraryPath));
            var catalogueHash = SHA256.HashData(File.ReadAllBytes(cataloguePath));
            var catalogue = GwentOneCardCatalog.Load(cataloguePath);
            var ciri = catalogue.Single(card => card.Id == "112101");
            var titles = new HoverTitleIndex(catalogue);
            Check(titles.Read("希 里\n猎魔人")?.Id == ciri.Id && titles.Read("Ciri")?.Id == ciri.Id,
                "Chinese OCR spacing and existing English card titles map to the same ID");
            Check(titles.Read("希里\n人类，贵族，密探") is null,
                "A missing False Ciri prefix cannot identify Witcher Ciri in Chinese");
            Check(titles.ReadChoiceGlyphEquivalent("UEREENA")?.Name == "Vereena" &&
                titles.ReadGlyphEquivalent("FAISE CIRI")?.Name == "False Ciri",
                "Adding Chinese aliases retains the English glyph-equivalence indexes");
            var aerondight = catalogue.Single(card => card.Id == "203102");
            Check(DeckBuilderScanner.MatchLines([new("湖 女 之 剑 x2", new(.06,.2,.19,.22))], catalogue).Single() is { Count: 1, Card.Id: "203102" },
                "Chinese deck rows retain canonical IDs and never create two gold copies");
            Check(DeckBuilderScanner.MatchHeader([new("战术优势", new(.04,.2,.18,.22))], catalogue, CardKind.Stratagem)?.Name == "Tactical Advantage",
                "Chinese stratagem headers map to canonical metadata");
            Check(DeckBuilderScanner.MatchLines([new("伊克素拉", new(.06,.2,.19,.22))], catalogue, true).Count == 0,
                "Incorrect Chinese characters are rejected rather than guessed");
            Check(ScreenStateRecognizer.NormalizeHeader("第 2 小局") == "ROUND 2" &&
                ScreenStateRecognizer.NormalizeHeader("重新抽牌\n选择 1 张卡牌重抽。[0/3]") == "REDRAW" &&
                ScreenStateRecognizer.NormalizeHeader("敌方墓场") == "GRAVEYARD" &&
                ScreenStateRecognizer.NormalizeHeader("战败") == "DEFEAT",
                "Local GWENT round, mulligan, graveyard and outcome labels normalize to shared states");
            Check(DeckPlayResolutionTracker.IsPlayChoiceHeader(ScreenStateRecognizer.NormalizeHeader("选择打出一张牌")) &&
                !DeckPlayResolutionTracker.IsPlayChoiceHeader(ScreenStateRecognizer.NormalizeHeader("选择抽取一张牌")),
                "Chinese play-choice headers reach the existing tutor resolution gate");
            Check(PostMatchMmrRecognizer.IsStandardModeMenu("标 准 模 式\n赛 季") &&
                !PostMatchMmrRecognizer.IsStandardModeMenu("非排位模式"),
                "Chinese standard-menu labels retain the mode and season gate");
            Check(PostMatchScoreRecognizer.IsNextControl("继 续") && PostMatchScoreRecognizer.IsNextControl("NEXT") &&
                !PostMatchScoreRecognizer.IsNextControl("继续对局"),
                "The exact Chinese result-table button preserves its layout gate");
            var ranked = PostMatchMmrRecognizer.ParseRankedPanel("VICTORY", "排 位 赛",
                [new("2500", new(.47,.26,.53,.30)), new("2400", new(.47,.32,.53,.34))]);
            Check(ranked?.RatingAfter == 2400 && ranked.SeasonPeak == 2500,
                "Chinese ranked labels preserve current-versus-season-peak semantics");
            var mmr = PostMatchMmrRecognizer.Parse("VICTORY", [new("当前阵营评级分数：2400 (+12)", new(.2,.3,.5,.32))]);
            Check(mmr?.RatingAfter == 2400 && mmr.Change == 12 && mmr.IsFactionRating,
                "Chinese faction-rating labels preserve value, delta and scope");
            Check(RuntimeCardValueReader.Read(aerondight, "湖 女 之 剑\n对 1 个敌军单位造成 12 点伤害。")?.Amount == 12 &&
                RuntimeCardValueReader.Read(aerondight, "湖女之剑\n对1个敌军单位造成12点伤害。对1个敌军单位造成3点伤害。") is null,
                "Chinese runtime damage requires a full card title and one unambiguous clause");
            Check(RuntimeCardValueReader.Read(aerondight, "Aerondight\nDamage an enemy unit by 12.")?.Amount == 12,
                "English runtime damage keeps the existing parser behavior");
            var spring = catalogue.Single(card => card.Id == "202219");
            Check(RuntimeCardValueReader.Read(spring, "春分\n使1个友军单位获得[8]点增益。")?.Amount == 8 &&
                RuntimeCardValueReader.Read(spring, "春分\n使1个友军单位获得[99]点增益。") is null,
                "Chinese Nature-card values retain the existing numeric bounds");
            var goldenChild = catalogue.Single(card => card.Id == "203159");
            Check(RuntimeCardValueReader.Read(goldenChild, "卡兰希尔：金童\n之前5个敌方回合中霜造成的伤害量（2、3、0、1、4）。")?.Amount == 11,
                "Chinese frost history retains the five-turn arithmetic");
            Check(RuntimeCardValueReader.Read(spring, "春分\n使1个友军单位获得8点增益。使1个友军单位获得9点增益。") is null &&
                RuntimeCardValueReader.Read(goldenChild, "卡兰希尔：金童\n之前5个敌方回合中霜造成的伤害量（1、1、1、1、1）。之前5个敌方回合中霜造成的伤害量（2、2、2、2、2）。") is null,
                "Conflicting Chinese Nature and frost clauses cannot publish a runtime value");
            var infusion = CultistInfusionReader.Read(ciri, "希里\n人类，呓语\n己方每打出1个“呓语”单位，使其获得3点增益，若其为铜色牌，则随后增益+1");
            Check(infusion is { HasCultistCategory: true } && infusion.Boosts.SequenceEqual([3]),
                "The local Chinese Cultist infusion clause retains its measured boost");
            Check(UiTranslation.Translate("Analyzing · training recording off · 0 frames retained · 190 analyzed") ==
                "分析中 · 训练录制已关闭 · 已保留 0 帧 · 已分析 190 帧",
                "The screenshot's dynamic tracking status is fully translated");
            Check(DeckDisplayNames.Suggest("2x Commandos Foltest Coen").Contains("蓝衣突击队") &&
                DeckDisplayNames.Suggest("Yago Sigi Reuven Ludovicus Roderick · 2 variations").Contains("2 个版本"),
                "The screenshot's deck shorthand and version suffix receive Chinese display suggestions");
            var aliasFixture = Path.Combine(folder, "deck-alias-fixture.json");
            DeckDisplayNames.Configure(aliasFixture);
            DeckDisplayNames.Set("fixture", "我的中文名称");
            Check(DeckDisplayNames.Display("fixture", "Original author title") == "我的中文名称" &&
                File.ReadAllText(aliasFixture).Contains("fixture"), "Custom display aliases persist in their own file");
            DeckDisplayNames.Set("fixture", "");
            Check(DeckDisplayNames.Display("fixture", "Original author title") == "Original author title",
                "Clearing an alias restores the source or suggested title");
            DeckDisplayNames.Configure(Path.Combine(FindDataRoot(), "cache", "deck-display-names.json"));
            Check(ciri.Name == "Ciri" && ciri.AbilityText!.Contains("Deploy:"), "Canonical card names and effect text remain English");
            var translatedAbility = UiTranslation.Translate(ciri.AbilityText);
            Check(translatedAbility.Contains("部署") && translatedAbility.Contains("3") && translatedAbility.Contains("1") && !translatedAbility.Contains('{'),
                "Current Ciri description resolves the Chinese game's numeric placeholders");
            var translatedStats = UiTranslation.Translate("25 FIXED · 0 AUTO · 0 excluded copies");
            Check(translatedStats.Contains("25") && translatedStats.Contains("固定") && translatedStats.Contains("排除") &&
                !translatedStats.Contains("FIXED") && !translatedStats.Contains("AUTO"),
                "Specific statistic templates take priority over generic copy-count templates: " + translatedStats);

            var window = new MainWindow();
            window.Loaded -= window.OnLoaded;
            window._library = DeckLibrary.Load(LibraryPath);
            window._cachedDecks = window._library.Decks;
            window._candidateCatalog = catalogue;
            window.SetDeckListItems(window.CreateDeckItems(window._cachedDecks.Take(50)));
            var deck = window._cachedDecks.First();
            window._selectedUserDeck = deck;
            window.UpdateSelectedUserDeckDisplay();
            var observations = deck.Cards.Take(8).Select(card => new ObservedCard(card.Card,
                CardProvenance.ProbableStartingDeck, .95, DateTimeOffset.UtcNow, "Offline Chinese UI fixture", card.Count)).ToArray();
            window._lastProjection = new OpponentDeckProjector().Build(window._cachedDecks.Take(100), observations, deck.Faction, catalog: catalogue);
            window.RenderCompletedProjection();
            window.HideLoadingShell();
            window.GameStatusText.Text = "Connected · GWENT · Windowed";
            window.DeckDataStatusText.Text = "Read-only sample library · " + window._cachedDecks.Length + " public decks";
            Check(window.GameStatusText.Text == "Connected · GWENT · Windowed" && window.GameStatusText.DisplayText.Contains("已连接"),
                "Dynamic connection status renders Chinese without changing its source value");
            window._expandedWorkspace = true;
            void Render(string file, UiPage page, int width, int height)
            {
                window.ShowPage(page); window.UpdateWorkspaceLayout(width);
                DeckBuilderSmoke.Render(window, Path.Combine(folder, file + ".png"), width, height);
            }
            Render("main-wide", UiPage.Deck, 1600, 900);
            Render("library", UiPage.Library, 1400, 900);
            Render("settings", UiPage.Settings, 1200, 900);
            window._expandedWorkspace = false;
            Render("main-compact", UiPage.Deck, 480, 900);
            Check(window._visionWorker is null && window._diagnosticSession is null, "UI validation never starts screen capture or match recording");

            var builder = new DeckBuilderWindow(window._library, LibraryPath, catalogue, deck, () => { });
            builder.CardSearch.Text = "杰洛特";
            Check(builder.Collection.Rows!.Cast<object>().Any(), "Deck-builder search accepts Chinese card names");
            builder.InspectedCardName.Text = ciri.Name;
            builder.InspectedCardAbility.Text = ciri.AbilityText;
            Check(builder.InspectedCardName.DisplayText == "希里", "Card inspector presents the local game's Chinese card name");
            Check(builder.DraftStatistics.DisplayText.Contains("费用曲线") && !builder.DraftStatistics.DisplayText.Contains("FIXED"),
                "Multiline deck statistics translate without changing source data");
            DeckBuilderSmoke.Render(builder, Path.Combine(folder, "deck-builder.png"), 1320, 900);
            var stats = new MatchAnalysisWindow(Path.Combine(folder, "empty-match-fixture"), FindDataRoot());
            await stats.RefreshAsync();
            DeckBuilderSmoke.Render(stats, Path.Combine(folder, "match-data.png"), 1400, 900);
            stats.DisplayChoice.SelectedIndex = 1;
            DeckBuilderSmoke.Render(stats, Path.Combine(folder, "matchups.png"), 1400, 900);
            Check((string)stats.DisplayChoice.SelectedItem == "Factions & leaders" &&
                VisualDescendants(stats.DisplayChoice).OfType<LocalizedTextBlock>().Any(text => text.DisplayText == "阵营与领袖"),
                "Statistics dropdowns display Chinese while keeping their English selected values");
            stats.DisplayChoice.SelectedIndex = 2;
            DeckBuilderSmoke.Render(stats, Path.Combine(folder, "statistics.png"), 1400, 900);
            var consent = new DataContributionConsentWindow();
            DeckBuilderSmoke.Render(consent, Path.Combine(folder, "consent.png"), 540, 500);
            var template = Application.Current.Resources[new DataTemplateKey(typeof(string))] as DataTemplate;
            Check(template is not null, "String buttons, dropdown values and tooltips use a presentation-only template");
            var renderedString = (LocalizedTextBlock)template!.LoadContent();
            renderedString.DataContext = "Import decks";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.DataBind);
            Check(renderedString.DisplayText == "导入卡组" && renderedString.Text == "Import decks", "Button labels translate while original English content stays intact");
            Check(VisualDescendants(builder.SaveDeckButton).OfType<LocalizedTextBlock>().Any(text =>
                text.DisplayText != text.Text && !string.IsNullOrWhiteSpace(text.DisplayText)), "Actual button templates render Chinese labels");
            Check(catalogueHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(cataloguePath))) &&
                libraryHash.SequenceEqual(SHA256.HashData(File.ReadAllBytes(LibraryPath))), "UI validation leaves the English catalogue and deck library byte-for-byte unchanged");
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new
            {
                Passed = true, CardNames = UiTranslation.CardCount, UiStrings = UiTranslation.UiCount,
                Checks = checks, Screenshots = Directory.GetFiles(folder, "*.png").Select(Path.GetFileName).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(folder, "result.json"), JsonSerializer.Serialize(new { Passed = false, Checks = checks, Error = error.ToString() },
                new JsonSerializerOptions { WriteIndented = true }));
            return 1;
        }
    }

    private sealed class NameFixture : INotifyPropertyChanged
    {
        private string _name = "";
        public string Name { get => _name; set { _name = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Name))); } }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject target)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(target); index++)
        {
            var child = VisualTreeHelper.GetChild(target, index);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
}
