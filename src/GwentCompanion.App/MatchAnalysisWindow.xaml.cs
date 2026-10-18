using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GwentCompanion.App.Controls;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow : Window
{
    private readonly string _directory, _dataRoot;
    private readonly CancellationTokenSource _lifetime = new();
    private MatchAnalysisEntry[] _all = [], _filtered = [], _completed = [];
    private DeckDefinition[] _libraryDecks = [];
    private IReadOnlyList<CardDefinition> _catalog = [];
    private readonly Dictionary<string, ImageSource?> _images = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, CardDefinition> _cards = new(StringComparer.Ordinal);
    private bool _ready, _fullscreen;
    private int _page, _loadVersion;
    private WindowState _previousState;
    private const int PageSize = 30;
    private int _unreadable, _duplicates;
    private string? _catalogWarning;

    public MatchAnalysisWindow(string directory, string dataRoot)
    {
        _directory = directory; _dataRoot = dataRoot;
        CardInspection.PortraitsDirectory=Path.Combine(dataRoot,"cache","portraits");
        InitializeComponent();
        DisplayChoice.ItemsSource = new[] { "Match history", "Factions & leaders", "Statistics" }; DisplayChoice.SelectedIndex = 0;
        SideChoice.ItemsSource = new[] { "Opponent", "Player" }; SideChoice.SelectedIndex = 0;
        GroupChoice.ItemsSource = new[] { "Factions", "Leader abilities" }; GroupChoice.SelectedIndex = 0;
        MetricChoice.ItemsSource = new[] { "Share", "Win rate" }; MetricChoice.SelectedIndex = 0;
        RecordScopeChoice.ItemsSource = new[] { "正式与异常对局", "Pending records", "All active records", "Recycle bin", "异常对局" }; RecordScopeChoice.SelectedIndex = 0;
        _ready = true;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => { _lifetime.Cancel(); _loadVersion++; };
        InitializeLadderUpdates();
    }

    public async Task RefreshAsync()
    {
        var version = ++_loadVersion;
        RefreshButton.IsEnabled = false; StatusText.Text = "Reading local match records…";
        try
        {
            var loaded = await Task.Run(() =>
            {
                IReadOnlyList<CardDefinition> catalog = []; string? warning = null;
                try { catalog = GwentOneCardCatalog.Load(Path.Combine(_dataRoot, "cache", "gwent-one-cards.json")); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException)
                { warning = "Card names unavailable; original IDs are shown."; }
                var data = MatchAnalysis.Load(_directory, _lifetime.Token, catalog);
                DeckDefinition[] decks = [];
                DeckLibrary? library = null;
                try { library = DeckLibrary.Load(Path.Combine(_dataRoot, "cache", "deck-library.json")); decks=library.Decks; }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException)
                { warning = "Deck library unavailable; faction and result corrections are still available."; }
                var observed = ObservedDeckStore.LinkReviews(new ObservedDeckStore().Load(Path.Combine(_dataRoot,"cache","observed-decks")),
                    data.Entries, Path.GetFullPath(Path.Combine(_directory,"..","diagnostics","sessions")));
                return (data, catalog, warning, decks, library, observed);
            }, _lifetime.Token);
            if (version != _loadVersion || _lifetime.IsCancellationRequested) return;
            var selectedPatch = (PatchChoice.SelectedItem as PatchOption)?.Value;
            var wasInitialized = PatchChoice.Items.Count > 0;
            _unreadable = loaded.data.UnreadableFiles; _duplicates = loaded.data.DuplicateFiles;
            _catalog = loaded.catalog; _catalogWarning = loaded.warning;
            _libraryDecks = loaded.decks;
            Localization.DeckDisplayNames.Configure(Path.Combine(_dataRoot,"cache","deck-display-names.json"),reload:true);
            if (loaded.library is not null) Localization.DeckDisplayNames.ConfigureGroups(loaded.library);
            ConfigureDeckNames(loaded.observed);
            _all = loaded.data.Entries.Select(entry => entry with { UserDeckName = CurrentReferenceName(entry.UserDeckKey) ?? entry.UserDeckName }).ToArray();
            _cards = _catalog.DistinctBy(c => c.Id).ToDictionary(c => c.Id, StringComparer.Ordinal);
            _ready = false;
            var patches = MatchAnalysis.Patches(_all);
            var options = new[] { new PatchOption(null, "All patches") }.Concat(patches.Select(p =>
                new PatchOption(p, p))).ToArray();
            PatchChoice.ItemsSource = options;
            PatchChoice.SelectedItem = wasInitialized
                ? options.FirstOrDefault(p => p.Value == selectedPatch) ?? options[0]
                : options.FirstOrDefault(p => p.Value is not null && p.Value != "Unknown patch") ?? options[0];
            _ready = true; _page = 0; Render();
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            if (version != _loadVersion) return;
            StatusText.Text = "Could not read match data: " + error.Message;
            EmptyText.Text = "The data could not be read. Check the message below, then try Refresh data. Existing files have not been changed.";
            EmptyPanel.Visibility = Visibility.Visible;
        }
        finally { if (version == _loadVersion) RefreshButton.IsEnabled = true; }
    }

    private void FilterChanged(object sender, SelectionChangedEventArgs e)
    { if (_ready) { _page = 0; Render(); } }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Render()
    {
        var patch = MatchAnalysis.Filter(_all, (PatchChoice.SelectedItem as PatchOption)?.Value);
        _completed = MatchAnalysis.Completed(patch);
        var pending = patch.Count(m => !m.Deleted && !m.IsCompleted && !m.Abnormal);
        var abnormal = patch.Count(m => !m.Deleted && m.Abnormal);
        _filtered = RecordScopeChoice.SelectedIndex switch
        { 1 => patch.Where(m => !m.Deleted && !m.IsCompleted && !m.Abnormal).ToArray(), 2 => patch.Where(m => !m.Deleted).ToArray(),
          3 => patch.Where(m => m.Deleted).ToArray(), 4 => patch.Where(m => !m.Deleted && m.Abnormal).ToArray(),
          _ => patch.Where(m => m.IsCompleted || !m.Deleted && m.Abnormal).ToArray() };
        var totals = MatchAnalysis.Totals(_completed);
        Kpis.ItemsSource = new[]
        {
            new MetricVm("COMPLETED GAMES", totals.Games.ToString("N0"), $"已排除 {abnormal} 场异常、{pending} 条待核对记录"),
            new MetricVm("WIN RATE", Percent(totals.WinRate), $"{totals.Wins + totals.Losses:N0} wins + losses; draws excluded"),
            new MetricVm("WINS / LOSSES / DRAWS", $"{totals.Wins} / {totals.Losses} / {totals.Draws}", "Only explicitly recorded outcomes"),
            new MetricVm("WIN / LOSS BALANCE", $"{totals.Wins - totals.Losses:+0;-0;0}", "Wins minus losses in the selected patch")
        };
        var breakdown = DisplayChoice.SelectedIndex == 1;
        HistoryView.Visibility = DisplayChoice.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordScopeControls.Visibility = HistoryView.Visibility;
        BreakdownView.Visibility = breakdown ? Visibility.Visible : Visibility.Collapsed;
        StatisticsView.Visibility = DisplayChoice.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        SideControls.Visibility = GroupControls.Visibility = MetricControls.Visibility = breakdown ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Visibility = (DisplayChoice.SelectedIndex == 0 ? _filtered.Length : DisplayChoice.SelectedIndex == 2 ? patch.Count(m => !m.Deleted) : _completed.Length) == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyPanel.Margin = HistoryView.Visibility == Visibility.Visible ? new Thickness(0,110,10,40) : new Thickness(0,0,10,0);
        EmptyText.Text = _all.Length == 0 ? "Run live recognition in Gwent Vision, then return here and refresh. Saved games will appear automatically."
            : "此视图暂无记录。可切换正式与异常对局、待核对记录、异常对局或回收站，也可在上方手动添加异常对局。";
        StatusText.Text = $"{_completed.Length} 场正式对局 · {abnormal} 场异常（不计胜率） · {pending} 条待核对 · {patch.Count(m => m.Deleted)} 条已删除。段位单独计分。" +
            (_unreadable == 0 ? "" : $"  {_unreadable:N0} unreadable files skipped; originals preserved.") +
            (_duplicates == 0 ? "" : $"  {_duplicates:N0} duplicate files excluded.") + (_catalogWarning is null ? "" : "  " + _catalogWarning);
        if (HistoryView.Visibility == Visibility.Visible) RenderHistory();
        if (breakdown) RenderBreakdown();
        if (StatisticsView.Visibility == Visibility.Visible) RenderStatistics(totals);
    }

    private void RenderHistory()
    {
        _page = Math.Clamp(_page, 0, Math.Max(0, (_filtered.Length - 1) / PageSize));
        var rows = _filtered.Skip(_page * PageSize).Take(PageSize).Select(m => new HistoryVm(m,
            Identity(m.UserFaction, m.UserLeader), Identity(m.OpponentFaction, m.OpponentLeader))).ToArray();
        HistoryList.ItemsSource = rows;
        UpdateOpponentDeckNames(rows);
        PreviousButton.IsEnabled = _page > 0; NextButton.IsEnabled = (_page + 1) * PageSize < _filtered.Length;
        PageLabel.Text = _filtered.Length == 0 ? "No matches" : $"{_page * PageSize + 1:N0}–{Math.Min((_page + 1) * PageSize, _filtered.Length):N0} of {_filtered.Length:N0} games";
    }
    private void PreviousClicked(object sender, RoutedEventArgs e) { _page--; RenderHistory(); }
    private void NextClicked(object sender, RoutedEventArgs e) { _page++; RenderHistory(); }

    private void RenderBreakdown()
    {
        var leaders = GroupChoice.SelectedIndex == 1; var winRate = MetricChoice.SelectedIndex == 1;
        var player = SideChoice.SelectedIndex == 1;
        var groups = leaders
            ? MatchAnalysis.Breakdown(_completed, m => LeaderKey(player ? m.UserFaction : m.OpponentFaction, player ? m.UserLeader : m.OpponentLeader),
                GwentOneCardCatalog.StartingLeaders(_catalog).Select(c => LeaderKey(c.Faction, c.Name)))
            : MatchAnalysis.Breakdown(_completed, m => player ? m.UserFaction : m.OpponentFaction, MatchAnalysis.Factions);
        BreakdownTitle.Text = (player ? "Your " : "Opponent ") + (leaders ? "leader abilities" : "factions");
        BreakdownExplanation.Text = winRate
            ? "Win rate = wins ÷ (wins + losses). Draws and unknown results are excluded. Small samples remain visible; — means no decisive results."
            : player ? "Play share = completed games using this faction or leader ÷ completed games in the selected patch. Pending and deleted records are excluded."
            : "Encounter share = completed games against this group ÷ completed games in the selected patch. Pending and deleted records are excluded.";
        BreakdownList.ItemsSource = groups.Select(g =>
        {
            IdentityVm identity;
            if (leaders)
            {
                var split = g.Key.Split('\u001f');
                identity = Identity(split[0], split.Length > 1 ? split[1] : "Unknown leader");
            }
            else identity = Identity(g.Key, null);
            return new BreakdownVm(identity, winRate ? g.WinRate ?? 0 : g.Share,
                winRate ? Percent(g.WinRate) : $"{g.Share:0.#}%", $"{g.Games:N0} games",
                $"{g.Wins} W  /  {g.Losses} L  /  {g.Draws} D", $"{g.Unknown} unknown · {g.Wins + g.Losses} decisive");
        }).ToArray();
    }

    private string LeaderKey(string faction, string leader)
    {
        var card = ResolveLeader(faction, leader);
        return (card?.Faction ?? faction) + "\u001f" + (card?.Name ?? leader);
    }

    private void RenderStatistics(MatchAnalysisTotals t)
    {
        RenderLadderProgress();
        var latest = _completed.Length == 0 ? DateOnly.FromDateTime(DateTime.UtcNow) : _completed.Max(m => m.Date);
        var recent = MatchAnalysis.Totals(_completed.Where(m => m.Date > latest.AddDays(-7)).ToArray());
        var previous = MatchAnalysis.Totals(_completed.Where(m => m.Date <= latest.AddDays(-7) && m.Date > latest.AddDays(-14)).ToArray());
        var breakdown = MatchAnalysis.Breakdown(_completed, m => m.OpponentFaction);
        var eligible = breakdown.Where(g => g.Wins + g.Losses >= 5 && g.Key != "Unknown faction").ToArray();
        var toughest = MatchAnalysis.MatchupToReview(breakdown);
        var strongest = eligible.OrderByDescending(g => g.WinRate).ThenByDescending(g => g.Games).FirstOrDefault();
        StatisticsList.ItemsSource = new[]
        {
            new MetricVm("Recent win rate", Percent(recent.WinRate), $"7 days ending {latest:dd MMM}: {recent.Wins} wins / {recent.Losses} losses. Draws excluded."),
            new MetricVm("Change from the previous week", recent.WinRate is { } now && previous.WinRate is { } before ? $"{now - before:+0.#;-0.#;0} pp" : "—", $"Previous 7 days: {Percent(previous.WinRate)} across {previous.Wins + previous.Losses} decisive games. Opponents and decks may differ."),
            new MetricVm("Matchup to review", toughest?.Key ?? "—", toughest is null ? "Needs at least 3 wins + losses, including one loss, against a faction." : $"{Percent(toughest.WinRate)} · {toughest.Wins} W / {toughest.Losses} L. Review these losses in match history; small samples can change quickly."),
            new MetricVm("Strongest matchup so far", strongest?.Key ?? "—", strongest is null ? "Needs at least 5 wins + losses against one faction." : $"{Percent(strongest.WinRate)} · {strongest.Wins} W / {strongest.Losses} L. Small samples can change quickly; this combines your decks."),
            new MetricVm("Draw rate", t.Wins + t.Losses + t.Draws == 0 ? "—" : $"{100d * t.Draws / (t.Wins + t.Losses + t.Draws):0.#}%", $"{t.Draws} draws among {t.Wins + t.Losses + t.Draws} completed results.")
        };
        var progress = new[] { _ladderSnapshot?.Series??MatchAnalysis.RankProgress(_completed) };
        ProgressCharts.ItemsSource = progress.Where(s => s.Points.Any(p => p.Rating.HasValue)).ToArray();
        FactionResults.ItemsSource = MatchAnalysis.Breakdown(_completed,m => m.UserFaction).Where(g => g.Games > 0)
            .Select(g => new MetricVm(g.Key,Percent(g.WinRate),$"{g.Games} games · {g.Wins} W / {g.Losses} L / {g.Draws} D")).ToArray();
        var ratings = MatchAnalysis.Factions.Select(f => MatchAnalysis.RatingProgress(_completed, f))
            .Where(s => s.Points.Any(p => p.Rating.HasValue)).OrderByDescending(s => s.Points.Length).ToArray();
        RatingCharts.ItemsSource = ratings;
        RatingEmptyNotice.Visibility = ratings.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var ordinaryRanks = _completed.Count(m => m.EffectiveRank is > 0);
        RatingEmptyText.Text = ordinaryRanks > 0
            ? "These records include ordinary ladder games. Faction MMR here requires a visible Pro Rank rating; use rank and win/loss progression above for ordinary ladder. Missing ratings are not calculated from wins."
            : "No confirmed, ordered faction-MMR readings in these games. Win/loss statistics above are available. Keep recognition running through the ranked result panel; account level, ladder rank and faction MMR are separate values.";
    }

    private async void MatchExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { DataContext: HistoryVm row } expander || e.OriginalSource != sender) return;
        if (row.Entry.ManualRecord && row.Entry.Abnormal)
        {
            expander.Content = new TextBlock { Text = row.AbnormalSummary + "\n这是手动补录的异常败局，没有卡牌、比分或对手识别信息。可通过右侧“编辑记录”修正时间，删除后撤销其段位影响。",
                TextWrapping = TextWrapping.Wrap, Foreground = FactionPalette.Brush("#E8CC8C"), Margin = new Thickness(0,12,0,0) };
            return;
        }
        var pending = new TextBlock { Text = "Loading both decks…", Margin = new Thickness(0, 12, 0, 0) };
        expander.Content = pending;
        try
        {
            var loaded = await Task.Run(() =>
            {
                var source=LocalMatchStore.Read(row.Entry.Path);
                var effective = MatchIdentityInference.Apply(MatchReviewStore.Apply(source,
                    new MatchReviewStore(_directory).Load().GetValueOrDefault(MatchReviewStore.Key(row.Entry.InstallationId, row.Entry.MatchId))), _catalog);
                return (Source:source,Match: effective, Candidates: OpponentDeckMatcher.Match(_libraryDecks, effective.Opponent, _catalog, effective.Patch),
                    UserCandidates:OpponentDeckMatcher.Match(_libraryDecks,effective.User,_catalog,effective.Patch));
            }, _lifetime.Token);
            var match = loaded.Match;
            if (!expander.IsExpanded || !ReferenceEquals(expander.Content, pending) || _lifetime.IsCancellationRequested) return;
            var rounds=row.Entry.LocalProfile?.ResultRoundScores??match.Rounds;
            var scores = rounds.Length == 0 ? "Round scores not recorded."
                : string.Join(" · ", rounds.Select(r => $"R{r.Number}: {r.UserScore?.ToString() ?? "?"}–{r.OpponentScore?.ToString() ?? "?"}{(r.FinalConfirmed ? " (final)" : " (last observed)")}"));
            var info = $"Patch {match.Patch}  |  {scores}\n" +
                "Saved references are confirmed deck lists. Partial views keep observed cards and guesses separate. Card provisions use the current catalog." +
                (row.Entry.ManuallyEdited ? "\nUser corrections are applied; the original detector record is preserved." : "") +
                (row.Entry.UserFactionInferred ? "\nYour faction was inferred from at least three observed starting cards; leader and full deck remain unconfirmed." : "");
            void Changed()
            {
                _opponentNameEvidence.Remove(RowKey(row.Entry));
                UpdateOpponentDeckNames(HistoryList.Items.Cast<HistoryVm>().ToArray());
                if(expander.IsExpanded)MatchExpanded(expander,new RoutedEventArgs(Expander.ExpandedEvent,expander));
            }
            var userEditor=EvidenceEditor(MatchReviewStore.Key(match),loaded.Source.User,match.User,true,Changed);
            var opponentEditor=EvidenceEditor(MatchReviewStore.Key(match),loaded.Source.Opponent,match.Opponent,false,Changed);
            expander.Content = new ContentControl
            {
                ContentTemplate = (DataTemplate)FindResource("MatchDetails"),
                Content = new DetailsVm(info, Deck("你的卡牌实录" + (row.Entry.UserDeckName is null ? "" : " · " + row.Entry.UserDeckName), match.User, userEditor,loaded.UserCandidates.HasMatches), Deck("对手卡牌实录", match.Opponent, opponentEditor,loaded.Candidates.HasMatches),
                    loaded.Candidates, _dataRoot, (match.Opponent.Faction ?? "Unknown faction") + " · " + (match.Opponent.Leader ?? "Unknown leader"),
                    opponentEditor, ResultRanks: row.ResultRanks, ResultScores: FormatRoundScores(rounds), UserCandidates:loaded.UserCandidates,CaptureWarning:row.Entry.CaptureNote??"")
            };
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        { if (ReferenceEquals(expander.Content, pending)) pending.Text = "This match could not be opened: " + error.Message; }
    }
    private void MatchCollapsed(object sender, RoutedEventArgs e)
    { if (sender is Expander expander && e.OriginalSource == sender) expander.Content = null; }

    private OpponentEvidenceEditor EvidenceEditor(string key,MatchPlayer baseline,MatchPlayer effective,bool user,Action changed)=>
        new OpponentEvidenceEditor(_directory,key,baseline.Observations.Where(c=>
            _cards.ContainsKey(c.CardId) && c.Evidence is MatchCardEvidence.Observed or MatchCardEvidence.Remembered && StartingDeckRules.CountsAgainstStartingDeck(c.Origin))
            .Select(c=>new ObservedCard(_cards[c.CardId],c.Origin,1,DateTimeOffset.MinValue,ObservedCopies:c.Copies)).ToArray(),
            MatchAnalysis.Factions.Contains(effective.Faction)?effective.Faction:null,changed,forUser:user).WithCatalog(_catalog);

    private DeckVm Deck(string label, MatchPlayer player, OpponentEvidenceEditor? editor=null,bool allowPrediction=false)
    {
        // Evidence is never intersected with a candidate deck or a chosen reference.
        var observed=player.Observations.Where(c=>c.Evidence==MatchCardEvidence.Observed).ToArray();
        var remembered=player.Observations.Where(c=>c.Evidence==MatchCardEvidence.Remembered).ToArray();
        var groups=new List<CardGroupVm>{Group("自动识别实录","#9AD1B3",
            "只显示本场实际捕获的卡牌，不按候选卡组筛选。衍生、生成或来源未定的观察会保留在此，但不一定计入起始牌组匹配。",observed)};
        if(remembered.Length>0)groups.Add(Group("记忆补录","#E5C77E","用户确认的卡牌；可用“补录／校对卡牌”修改数量或撤回。",remembered));
        var supplements=new List<CardGroupVm>();
        if(player.Reference.Length>0)supplements.Add(Group("保存的参考牌表（不是本场识别实录）","#B4C9E2","这是开局选择或保存的牌表；其中未实际捕获的卡牌不能视为本场已看到。",player.Reference));
        if(allowPrediction && player.Hypothesis.Length>0)supplements.Add(Group("预测补全（非实录）","#89BFFF","模型推测与手动假设单独保留，不加入实际观测，也不作为相似卡组推荐的证据。",player.Hypothesis));
        return new(label,Identity(player.Faction,player.Leader),groups.ToArray(),supplements.ToArray(),editor);
    }
    private CardGroupVm Group(string title, string color, string note, MatchCard[] cards) =>
        new(title + $" · {cards.Sum(c => c.Copies)} cards", FactionPalette.Brush(color), cards.Length == 0 ? "None recorded. " + note : note,
            cards.OrderBy(c => _cards.GetValueOrDefault(c.CardId) ?? new(c.CardId, c.CardId, "Unknown", CardKind.Unknown, 0), DeckBuilderOrder.Comparer).Select((c, i) =>
            {
                var card = _cards.GetValueOrDefault(c.CardId);
                var kind = c.Evidence switch { MatchCardEvidence.Remembered => "记忆补录", MatchCardEvidence.Inferred => "Inferred", MatchCardEvidence.ManualHypothesis => "Manual hypothesis", MatchCardEvidence.SelectedReference => "Reference", _ => Origin(c.Origin) };
                var confidence = c.Confidence == 255 ? "score unknown" : $"{100d * c.Confidence / 254:0}% {(c.Evidence == MatchCardEvidence.Observed ? "recognition" : "model score")}";
                var guessed = c.Evidence is MatchCardEvidence.Inferred or MatchCardEvidence.ManualHypothesis;
                var badge = c.Evidence switch { MatchCardEvidence.Remembered => "记忆补录", MatchCardEvidence.SelectedReference => "参考牌表", MatchCardEvidence.Observed => "已识别",
                    MatchCardEvidence.ManualHypothesis => "手选 ?", _ => "预测 ?" };
                var stateColor = c.Evidence switch { MatchCardEvidence.Observed => "#7DD9AE", MatchCardEvidence.Inferred => "#89BFFF",
                    MatchCardEvidence.ManualHypothesis => "#E5C77E", _ => color };
                return new CardVm(card?.Name ?? "Card " + c.CardId, (c.CopyCountIsEstimate ? "~" : "") + c.Copies + "×",
                    kind + (c.Evidence == MatchCardEvidence.Remembered ? " · 用户确认，可在补录／校对中撤回" : c.Evidence == MatchCardEvidence.SelectedReference ? " · selected deck" : " · " + confidence), CardArt(c.CardId),
                    (i + 1).ToString(), card?.Provision.ToString() ?? "?", badge,
                    FactionPalette.Brush(stateColor), FactionPalette.Brush(card?.IsGold == true ? "#9E844D" : "#59615C"), guessed ? 0.76 : 1,card);
            }).ToArray());

    private static string Origin(CardProvenance origin) => origin switch
    {
        CardProvenance.ConfirmedStartingDeck => "Confirmed starting origin", CardProvenance.ProbableStartingDeck => "Probable starting origin",
        CardProvenance.Unknown => "Origin unknown", _ => origin.ToString()
    };
    private CardDefinition? ResolveLeader(string? faction, string? leader) => _catalog.FirstOrDefault(c => c.Kind == CardKind.Leader &&
        (c.Id == leader || c.Name.Equals(leader, StringComparison.OrdinalIgnoreCase)) &&
        (string.IsNullOrWhiteSpace(faction) || faction == "Unknown faction" || c.Faction.Equals(faction, StringComparison.OrdinalIgnoreCase)));

    private IdentityVm Identity(string? faction, string? leader)
    {
        var card = ResolveLeader(faction, leader);
        var name = MatchAnalysisEntry.Clean(faction);
        var palette = FactionPalette.For(name);
        var icon = card is null ? null : LoadImage(Path.Combine(_dataRoot, "cache", "leader-portraits", card.Id + ".jpg"), 80)
            ?? LoadImage(Path.Combine(_dataRoot, "assets", "vision", "leaders", card.Id + ".jpg"), 80)
            ?? LoadImage(Path.Combine(AppContext.BaseDirectory, "vision-assets", "leaders", card.Id + ".jpg"), 80);
        var monogram = name switch { "Monsters" => "MO", "Nilfgaard" => "NG", "Northern Realms" => "NR", "Scoia'tael" => "ST", "Skellige" => "SK", "Syndicate" => "SY", _ => "?" };
        return new(name, leader is null ? "Faction" : card?.Name ?? MatchAnalysisEntry.Clean(leader, "Unknown leader"), icon,
            monogram, FactionPalette.Brush(palette.Accent), FactionPalette.Brush(palette.Surface));
    }
    private ImageSource? CardArt(string id) => LoadImage(Path.Combine(_dataRoot, "cache", "portraits", id + ".jpg"), 150);
    private ImageSource? LoadImage(string path, int width)
    {
        if (_images.TryGetValue(path, out var existing)) return existing;
        ImageSource? source = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.DecodePixelWidth = width; bitmap.UriSource = new Uri(Path.GetFullPath(path)); bitmap.EndInit(); bitmap.Freeze(); source = bitmap;
            }
        }
        catch (Exception error) when (error is IOException or NotSupportedException or InvalidOperationException) { }
        _images[path] = source; return source;
    }
    private static string Percent(double? value) => value is { } number ? $"{number:0.#}%" : "—";

    private void FullscreenClicked(object sender, RoutedEventArgs e) => SetFullscreen(!_fullscreen);
    private void WindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F11) { SetFullscreen(!_fullscreen); e.Handled = true; }
        else if (e.Key == Key.Escape && _fullscreen) { SetFullscreen(false); e.Handled = true; }
    }
    private void SetFullscreen(bool full)
    {
        if (full == _fullscreen) return;
        if (full)
        {
            _previousState = WindowState; WindowState = WindowState.Normal; WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize; WindowState = WindowState.Maximized;
        }
        else { WindowState = WindowState.Normal; WindowStyle = WindowStyle.SingleBorderWindow; ResizeMode = ResizeMode.CanResize; WindowState = _previousState; }
        _fullscreen = full; FullscreenButton.Content = full ? "Exit full screen · Esc" : "Full screen · F11";
    }

    private sealed record PatchOption(string? Value, string Label);
    private sealed record MetricVm(string Label, string Value, string Note);
    private sealed record IdentityVm(string Title, string Subtitle, ImageSource? Icon, string Monogram, Brush Accent, Brush Surface);
    private sealed record BreakdownVm(IdentityVm Identity, double Value, string ValueLabel, string SampleLabel, string RecordLabel, string UnknownLabel);
    private sealed record CardVm(string Name, string ValueBadge, string Detail, ImageSource? Artwork,
        string Position, string Provision, string Badge, Brush StateColor, Brush FrameColor, double RowOpacity,CardDefinition? Card=null) : ICardPreviewRow
    {
        public bool CanPreview=>Card is not null;
        public string AccessibleName => $"{Name}, {ValueBadge}, {Badge}. {Detail}";
        public string Tooltip => AccessibleName;
    }
    private sealed record CardGroupVm(string Title, Brush Accent, string Note, CardVm[] Cards);
    private sealed record DeckVm(string Label, IdentityVm Identity, CardGroupVm[] Groups,CardGroupVm[] Supplements,OpponentEvidenceEditor? Editor=null);
    private static string FormatRoundScores(IEnumerable<MatchRound> rounds)
    {
        var values=rounds.ToArray();
        return values.Length==0 ? "小局比分：尚未识别" : "小局比分（你∶对手）："+string.Join("    ",values.Select(r=>
            $"第 {r.Number} 局  {r.UserScore?.ToString()??"?"}∶{r.OpponentScore?.ToString()??"?"}"+(r.FinalConfirmed ? "" : "（最后观测）")));
    }
    private sealed record DetailsVm(string Context, DeckVm Mine, DeckVm Other, OpponentDeckMatchReport Candidates, string DataRoot, string FilterLabel, OpponentEvidenceEditor? Editor = null,
        string ResultRanks = "", string ResultScores = "",OpponentDeckMatchReport? UserCandidates=null,string CaptureWarning="");
    private sealed record HistoryVm(MatchAnalysisEntry Entry, IdentityVm Mine, IdentityVm Other) : System.ComponentModel.INotifyPropertyChanged
    {
        private OpponentDeckNameVm _opponentDeck = new("对手卡组：正在匹配…", "正在根据已见卡、阵营和初始领袖查找候选卡组。");
        public string OpponentDeckLabel => _opponentDeck.Label;
        public string OpponentDeckTooltip => _opponentDeck.Tooltip;
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
        public void SetOpponentDeck(OpponentDeckNameVm value)
        {
            _opponentDeck=value;
            PropertyChanged?.Invoke(this,new(nameof(OpponentDeckLabel)));
            PropertyChanged?.Invoke(this,new(nameof(OpponentDeckTooltip)));
        }
        public string UserProfileLabel => ProfileLabel(Entry.LocalProfile?.User,"你的",Entry.EffectiveRank);
        public string OpponentProfileLabel => ProfileLabel(Entry.LocalProfile?.Opponent,"对手的");
        public string ResultRanks => RankLabel("你的",Entry.LocalProfile?.User?.Rank??Entry.Rank)+"    ·    "+RankLabel("对手的",Entry.LocalProfile?.Opponent?.Rank);
        public string RoundScoresLabel => FormatRoundScores(Entry.LocalProfile?.ResultRoundScores??Entry.RoundScores??[]);
        public Visibility NormalDetailsVisibility => Entry.Abnormal ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AbnormalVisibility => Entry.Abnormal ? Visibility.Visible : Visibility.Collapsed;
        public string AbnormalSummary => (Entry.AbnormalReason ?? "异常对局") + "\n" +
            (Entry.ManualRecord ? "手动补录 · " : "已标记异常 · ") + "不计入任何胜率" +
            (Entry.CountsForLadder ? " · 参与段位／碎片追踪（校准后的对局）" : " · 不计入段位");
        public string CaptureWarning => Entry.CaptureNote??"";
        public Visibility CaptureWarningVisibility => string.IsNullOrWhiteSpace(CaptureWarning)?Visibility.Collapsed:Visibility.Visible;
        public Visibility UserProfileVisibility => string.IsNullOrWhiteSpace(UserProfileLabel) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility OpponentProfileVisibility => string.IsNullOrWhiteSpace(OpponentProfileLabel) ? Visibility.Collapsed : Visibility.Visible;
        private static string RankLabel(string side,int? rank) => side+"段位："+(rank is null ? "尚未识别" : rank==0 ? "职业天梯" : rank+" 段");
        private static string ProfileLabel(LocalPlayerProfile? player,string side,int? fallbackRank=null)
        {
            if (player is null) return RankLabel(side,fallbackRank);
            string T(string value) => Localization.UiTranslation.Translate(value);
            var parts = new List<string>();
            if (player.Name is { Length: > 0 } name) parts.Add(name + (player.NameSource is "ScreenOcr" or "ResultBanner" ? " (OCR)" : ""));
            parts.Add(RankLabel(side,player.Rank??fallbackRank));
            return string.Join(" · ",parts) + (parts.Count == 0 ? "" : " · " + T(player.Source == "UserConfirmed" || player.RankSource is "UserConfirmed" or "Manual" ? "User confirmed"
                : player.RankSource == "ResultBanner" || player.Source == "ResultBanner" ? "Result screen · editable"
                : player.Source == "ScreenOcr" ? "Screen OCR · editable" : "Manually entered"));
        }
        public string DateLabel => !Entry.ExactTimeKnown ? Entry.Date.ToString("yyyy-MM-dd")+" · 时间未保存" : Entry.StartedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm") ?? Entry.Date.ToString("yyyy-MM-dd");
        public string PatchLabel => "Patch " + Entry.Patch;
        public string DeckLabel => Entry.UserDeckName ?? "";
        public Visibility DeckLabelVisibility => string.IsNullOrWhiteSpace(Entry.UserDeckName) ? Visibility.Collapsed : Visibility.Visible;
        public string ResultLabel => (Entry.Abnormal ? "异常 · " : "") + (Entry.Outcome switch { MatchOutcome.Win => "胜利", MatchOutcome.Loss => "败局", MatchOutcome.Draw => "平局", _ => "结果未知" });
        public string RecordLabel => Entry.Deleted ? "In recycle bin" : Entry.Abnormal ? "不计胜率 · " + (Entry.CountsForLadder ? "段位单独计分" : "段位已排除") : Entry.CaptureNote is not null ? "采集不完整 · 已补录结算" : Entry.IsCompleted ? (Entry.ManuallyEdited ? "Reviewed game" : "Completed game") : Entry.EmptyCapture ? "Empty capture · excluded" : "Pending review · excluded";
        public Visibility DeleteVisibility => Entry.Deleted ? Visibility.Collapsed : Visibility.Visible;
        public Visibility RestoreVisibility => Entry.Deleted ? Visibility.Visible : Visibility.Collapsed;
        public Brush OutcomeBrush => FactionPalette.Brush(Entry.Abnormal ? "#F1B986" : Entry.Outcome switch { MatchOutcome.Win => "#8FD7AD", MatchOutcome.Loss => "#F09289", MatchOutcome.Draw => "#E3C789", _ => "#B4C0BB" });
        public string RatingLabel => Entry.LocalProfile?.User?.Rank is { } rank && Entry.FactionMmr is null
            ? Localization.UiTranslation.Translate("Ladder rank") + $" {rank}" : Entry.RatingLabel;
        public string AccessibleName => $"{DateLabel}, {ResultLabel}, {Mine.Title} {Mine.Subtitle} against {Other.Title} {Other.Subtitle}. Expand decks.";
    }
}
