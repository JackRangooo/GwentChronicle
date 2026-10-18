using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;

namespace GwentCompanion.App;

internal sealed class MatchReviewWindow : Window
{
    private readonly ComboBox _result = new(), _userFaction = new(), _userLeader = new(), _opponentFaction = new(), _opponentLeader = new();
    internal readonly Button ChooseDeckButton=new(){Content="从卡组库选择…",Padding=new Thickness(12,7,12,7)};
    private readonly TextBlock _deckCaption=new(){Text="保留原有牌表／实录",TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,5,0,12)};
    private DeckDefinition? _selectedDeck;
    private readonly CheckBox _include = new() { Content = "Include in completed-game statistics" };
    private readonly CheckBox _abnormal = new() { Content = "异常对局（掉线等）：不计入胜率，段位单独统计" };
    private readonly TextBox _abnormalReason = new() { MaxLength = 200, MinHeight = 28 };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Salmon };
    private readonly CompactMatch _source;
    private readonly MatchReview? _previous;
    private readonly LocalMatchProfile? _previousProfile;
    private readonly TextBox _userName = new(), _userRank = new(), _opponentName = new(), _opponentRank = new();
    private readonly CheckBox _ladderInclude=new(){Content="Count this game toward ladder progress"};
    private readonly IReadOnlyList<CardDefinition> _catalog;
    private bool _initializing = true;
    public MatchReview? Review { get; private set; }
    public bool ResetCorrections { get; private set; }
    public LocalMatchProfile? PlayerDetails { get; private set; }
    private sealed record Choice(string? Value, string Label);
    public MatchReviewWindow(CompactMatch source, CompactMatch effective, MatchReview? previous,
        IReadOnlyList<CardDefinition> catalog, IEnumerable<DeckDefinition> decks, LocalMatchProfile? profile = null)
    {
        _source = source; _previous = previous; _catalog = catalog; _previousProfile = profile;
        Title = UiTranslation.Translate("Edit match record"); Width = 760; MaxHeight = SystemParameters.WorkArea.Height - 50;
        SizeToContent = SizeToContent.Height; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = FactionPaletteBrush("#11191C"); Foreground = Brushes.White;
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock { Text = "Correct the result and both players. Choose your library deck only if it is the deck you actually played. Original recognition is preserved.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,0,0,14) });
        var factions = new[] { new Choice("Unknown faction", "Unknown faction") }.Concat(MatchAnalysis.Factions.Select(f => new Choice(f,f))).ToArray();
        _userFaction.ItemsSource = factions; _opponentFaction.ItemsSource = factions;
        _userFaction.SelectedValuePath = _opponentFaction.SelectedValuePath = nameof(Choice.Value);
        _userFaction.SelectedValue = effective.User.Faction ?? "Unknown faction";
        _opponentFaction.SelectedValue = effective.Opponent.Faction ?? "Unknown faction";
        _result.ItemsSource = new[] { new Choice("UNKNOWN", "Result unknown"), new Choice("VICTORY", "Match win"),
            new Choice("DEFEAT", "Match loss"), new Choice("DRAW", "Match draw") };
        _result.SelectedValuePath = nameof(Choice.Value); _result.SelectedValue = effective.Result ?? "UNKNOWN";
        var template = new DataTemplate(); var text = new FrameworkElementFactory(typeof(LocalizedTextBlock));
        text.SetBinding(LocalizedTextBlock.TextProperty, new Binding("Label")); template.VisualTree = text;
        foreach (var box in new[] { _result, _userFaction, _userLeader, _opponentFaction, _opponentLeader })
        { box.ItemTemplate = template; box.MaxDropDownHeight = 280; box.Margin = new Thickness(0,4,0,12); box.IsTextSearchEnabled = true; box.SetValue(TextSearch.TextPathProperty, "Label"); }
        SetLeaders(_userLeader, effective.User.Faction, effective.User.Leader);
        SetLeaders(_opponentLeader, effective.Opponent.Faction, effective.Opponent.Leader);
        _userFaction.SelectionChanged += (_,_) => { if (!_initializing) SetLeaders(_userLeader, (_userFaction.SelectedItem as Choice)?.Value, null); };
        _opponentFaction.SelectionChanged += (_,_) => { if (!_initializing) SetLeaders(_opponentLeader, (_opponentFaction.SelectedItem as Choice)?.Value, null); };
        var libraryDecks=decks as IReadOnlyList<DeckDefinition> ?? decks.ToArray();
        ChooseDeckButton.Click += (_,_) =>
        {
            var picker=new DeckLibraryPickerWindow(libraryDecks,catalog,(_userFaction.SelectedItem as Choice)?.Value,
                (_userLeader.SelectedItem as Choice)?.Value,"使用这套己方卡组"){Owner=this};
            if(picker.ShowDialog()!=true || picker.SelectedDeck is not {} deck)return;
            _selectedDeck=deck;_deckCaption.Text=DeckDisplayNames.DisplayCandidate(deck.Id,deck.Name)+" · "+UiTranslation.Translate(deck.Leader);
            _userFaction.SelectedValue = deck.Faction; SetLeaders(_userLeader, deck.Faction, deck.Leader);
        };
        Add(panel, "Match result", _result);
        _abnormal.IsChecked = previous?.Abnormal == true;
        _abnormal.Margin = new Thickness(0,2,0,8); panel.Children.Add(_abnormal);
        _abnormalReason.Text = previous?.AbnormalReason ?? "网络异常／未正常进入对局";
        var abnormalFields = new StackPanel();
        _abnormalReason.Margin = new Thickness(0,0,0,12); Add(abnormalFields,"异常说明",_abnormalReason); panel.Children.Add(abnormalFields);
        panel.Children.Add(PlayerDetailsPanel(profile));
        _ladderInclude.IsChecked=profile?.CountForLadder ?? (previous?.Abnormal == true || previous?.IncludeInStatistics != false);_ladderInclude.Margin=new Thickness(0,0,0,14);panel.Children.Add(_ladderInclude);
        var deckPanel=new StackPanel();deckPanel.Children.Add(ChooseDeckButton);deckPanel.Children.Add(_deckCaption);
        Add(panel, "Your played deck", deckPanel);
        Add(panel, "Your faction", _userFaction); Add(panel, "Your starting leader", _userLeader);
        Add(panel, "Opponent faction", _opponentFaction); Add(panel, "Opponent starting leader", _opponentLeader);
        _include.IsChecked = previous?.IncludeInStatistics ?? MatchAnalysisEntry.ParseOutcome(effective.Result) != MatchOutcome.Unknown;
        _include.Margin = new Thickness(0,2,0,12); panel.Children.Add(_include);
        void AbnormalChanged()
        {
            var abnormal = _abnormal.IsChecked == true;
            abnormalFields.Visibility = abnormal ? Visibility.Visible : Visibility.Collapsed;
            _include.IsEnabled = !abnormal;
            _include.IsChecked = !abnormal && (_result.SelectedItem as Choice)?.Value != "UNKNOWN";
            if (abnormal && (_result.SelectedItem as Choice)?.Value == "UNKNOWN") _result.SelectedValue = "DEFEAT";
        }
        _abnormal.Checked += (_,_) => { AbnormalChanged(); _ladderInclude.IsChecked = true; };
        _abnormal.Unchecked += (_,_) => AbnormalChanged();
        _result.SelectionChanged += (_,_) => { if (!_initializing) _include.IsChecked = _abnormal.IsChecked != true && (_result.SelectedItem as Choice)?.Value != "UNKNOWN"; };
        abnormalFields.Visibility = _abnormal.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (_abnormal.IsChecked == true) { _include.IsChecked = false; _include.IsEnabled = false; }
        panel.Children.Add(_error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0,12,0,0) };
        var reset = new Button { Content = "Reset result/deck corrections", Margin = new Thickness(0,0,8,0), Padding = new Thickness(10,6,10,6) };
        reset.Click += (_,_) => { ResetCorrections = true; DialogResult = true; };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0,0,8,0), Padding = new Thickness(10,6,10,6) };
        var save = new Button { Content = "Save changes", IsDefault = true, Padding = new Thickness(10,6,10,6) };
        save.Click += (_,_) => Save(); buttons.Children.Add(reset); buttons.Children.Add(cancel); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }; _initializing = false;
    }
    private static Brush FactionPaletteBrush(string color) => Controls.FactionPalette.Brush(color);
    private UIElement PlayerDetailsPanel(LocalMatchProfile? profile)
    {
        var section = new StackPanel { Margin = new Thickness(0,0,0,16) };
        section.Children.Add(new TextBlock { Text = "Local player details", Foreground = FactionPaletteBrush("#E3C789"), FontSize = 16 });
        section.Children.Add(new TextBlock { Text = "The result screen supplies both ladder ranks, names and round scores. All player details stay local; leave uncertain fields blank. Exclude casual games from ladder progress below.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0,6,0,10), Foreground = FactionPaletteBrush("#B5BFBC") });
        var grid = new Grid(); grid.ColumnDefinitions.Add(new()); grid.ColumnDefinitions.Add(new());
        void Player(int column, string title, LocalPlayerProfile? player, TextBox name, TextBox rank)
        {
            var fields = new StackPanel { Margin = column == 0 ? new(0,0,12,0) : new(12,0,0,0) };
            fields.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,0,0,6) });
            name.Text = player?.Name ?? ""; name.MaxLength = 80;
            // Existing Rank is the result-screen value. Keep it as a fallback display; do not silently turn it into a manual starting rank.
            rank.Text = player?.Rank?.ToString() ?? "";
            foreach (var field in new[] { name, rank }) { field.MinHeight = 30; field.Margin = new Thickness(0,3,0,8); }
            Add(fields, "Player name", name); Add(fields, "Ladder rank (0–30; 0 = Pro Rank)", rank);
            if (column == 0 && _source.Rank is { } observedRank)
                fields.Children.Add(new TextBlock { Text = UiTranslation.Translate("Post-match rank") + $": {observedRank}", Foreground = FactionPaletteBrush("#B5BFBC") });
            Grid.SetColumn(fields,column); grid.Children.Add(fields);
        }
        Player(0,"You",profile?.User,_userName,_userRank);
        Player(1,"Opponent",profile?.Opponent,_opponentName,_opponentRank);
        section.Children.Add(grid); return section;
    }
    private static void Add(Panel panel, string label, UIElement input)
    { panel.Children.Add(new TextBlock { Text = label, Foreground = FactionPaletteBrush("#E3C789") }); panel.Children.Add(input); }
    private void SetLeaders(ComboBox box, string? faction, string? selected)
    {
        var options = new[] { new Choice("Unknown leader", "Unknown leader") }.Concat(GwentOneCardCatalog.StartingLeaders(_catalog)
            .Where(c => c.Faction == faction).Select(c => new Choice(c.Name,c.Name))).ToList();
        if (selected is not null && options.All(c => c.Value != selected)) options.Add(new Choice(selected,selected));
        box.ItemsSource = options; box.SelectedValuePath = nameof(Choice.Value); box.SelectedValue = selected ?? "Unknown leader";
    }
    private void Save()
    {
        try
        {
            int? Number(TextBox box) => string.IsNullOrWhiteSpace(box.Text) ? null : int.TryParse(box.Text.Trim(), out var number)
                ? number : throw new ArgumentException("Enter a whole number, or leave the field blank.");
            LocalPlayerProfile Player(TextBox name, TextBox rank, LocalPlayerProfile? previous)
            {
                var value = string.IsNullOrWhiteSpace(name.Text) ? null : name.Text.Trim();
                var rankValue=Number(rank);
                var candidate = new LocalPlayerProfile(value,previous?.AccountLevel,previous?.Prestige,rankValue,
                    NameSource:previous?.Name == value ? previous?.NameSource : "Manual",
                    RankSource:previous?.Rank==rankValue?previous?.RankSource:"Manual");
                return previous is not null && (candidate with { Source = previous.Source }) == previous ? previous : candidate;
            }
            PlayerDetails = (_previousProfile??new()) with{User=Player(_userName,_userRank,_previousProfile?.User),
                Opponent=Player(_opponentName,_opponentRank,_previousProfile?.Opponent),CountForLadder=_ladderInclude.IsChecked==true};
            LocalMatchProfileStore.Validate(PlayerDetails);
        }
        catch (ArgumentException error) { _error.Text = UiTranslation.Translate(error.Message); return; }
        var deck = _selectedDeck;
        var reference = deck is null ? _previous?.UserReference : MatchReviewStore.Reference(deck);
        var ownFaction = (_userFaction.SelectedItem as Choice)?.Value ?? "Unknown faction";
        var ownLeader = (_userLeader.SelectedItem as Choice)?.Value ?? "Unknown leader";
        if (reference is not null && (reference.Faction != ownFaction || reference.Leader != ownLeader)) reference = null;
        var review = (_previous??new()) with {Result=(_result.SelectedItem as Choice)?.Value??"UNKNOWN",UserFaction=ownFaction,UserLeader=ownLeader,
            OpponentFaction=(_opponentFaction.SelectedItem as Choice)?.Value??"Unknown faction",OpponentLeader=(_opponentLeader.SelectedItem as Choice)?.Value??"Unknown leader",
            UserReference=reference,UserDeckName=reference is null?null:deck?.Name??_previous?.UserDeckName,IncludeInStatistics=_include.IsChecked==true,
            Note=_previous?.Note??"User-reviewed match fields",Abnormal=_abnormal.IsChecked==true,AbnormalReason=_abnormal.IsChecked==true?_abnormalReason.Text.Trim():null};
        if (review.IncludeInStatistics == true && review.Result == "UNKNOWN")
        { _error.Text = "Choose a known result before including this record in statistics."; return; }
        Review = review; DialogResult = true;
    }
}
