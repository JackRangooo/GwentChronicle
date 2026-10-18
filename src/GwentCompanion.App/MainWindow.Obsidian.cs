using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Text.Json;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MainWindow
{
    private DeckStripRow? _inspectedLiveCard;
    private string? _inspectedEncounter;
    private bool _liveInspectorOpen,_appearanceReady;
    private Action? _undoLiveCorrection;
    private sealed record WorkspaceAppearance(double Left,double Top,double Width,double Height,double Zoom,bool Expanded);
    private static string WorkspaceAppearancePath=>Path.Combine(PersistentUserDataDirectory,"workspace-appearance.json");
    private void LiveSource_OnChanged(object sender,SelectionChangedEventArgs e)
    {if(OpponentDeckCards is not null&&_lastProjection is not null)RenderCompletedProjection();}
    private IEnumerable<ProjectedDeckSlot> FilterLiveSlots(OpponentDeckProjection projection)
    {
        var originals=PresentedOpponentSlots(projection);
        var generated=_opponentTracker.Observations.Where(o=>o.Provenance is CardProvenance.Spawned or CardProvenance.Created or CardProvenance.Copied or CardProvenance.Transformed or CardProvenance.Stolen)
            .SelectMany(o=>Enumerable.Range(1,o.ObservedCopies).Select(copy=>new ProjectedDeckSlot(0,o.Card,copy,DeckSlotState.Observed,null,null,false,"生成／变化记录 · 不计起始牌组。 "+o.Evidence)));
        return LiveSourceChoice?.SelectedIndex switch {1=>generated,2=>originals.Concat(generated),_=>originals};
    }
    private void InitializeObsidian()
    {
        ReducedTransparencyChoice.IsChecked=AppearancePreferences.Current.ReducedTransparency;
        _appearanceReady=true;
    }
    private void RestoreWorkspaceAppearance()
    {
        if(AppearancePreferences.IsAudit)return;
        try {
            if(File.Exists(WorkspaceAppearancePath)&&JsonSerializer.Deserialize<WorkspaceAppearance>(File.ReadAllText(WorkspaceAppearancePath)) is {} saved &&
                double.IsFinite(saved.Width)&&double.IsFinite(saved.Height)&&saved.Width>=MinWidth&&saved.Height>=MinHeight) {
                var bounds=new Rect(saved.Left,saved.Top,saved.Width,saved.Height);
                var displays=DesktopDisplays.All();DesktopDisplays.Place(this,DesktopDisplays.Fit(bounds,DesktopDisplays.Nearest(bounds,displays).WorkArea));
                _workspaceZoom=saved.Zoom is >=1 and <=2?saved.Zoom:1;_expandedWorkspace=saved.Expanded;UpdateWorkspaceLayout();
            }
        } catch(Exception e) when(e is IOException or JsonException or InvalidOperationException or ArgumentException){FooterStatusText.Text="窗口位置无法恢复，已使用当前显示器。";}
        // Keep normal bounds in desktop pixels. WPF RestoreBounds/Left/Width are
        // DIPs and must not be passed to the physical-pixel placement API.
        var physicalBounds=DesktopDisplays.Bounds(this);
        void RememberNormalBounds()
        {if(WindowState==WindowState.Normal&&!_fullScreen)physicalBounds=DesktopDisplays.Bounds(this);}
        LocationChanged+=(_,_)=>RememberNormalBounds();
        SizeChanged+=(_,_)=>RememberNormalBounds();
        Closing+=(_,_)=>RememberNormalBounds();
        Closed+=(_,_)=> {
            try {
                var rect=_fullScreen&&_windowedBounds is {} normal?normal:physicalBounds;
                Directory.CreateDirectory(PersistentUserDataDirectory);
                File.WriteAllText(WorkspaceAppearancePath,JsonSerializer.Serialize(new WorkspaceAppearance(rect.Left,rect.Top,rect.Width,rect.Height,_workspaceZoom,_expandedWorkspace)));
            } catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException) { }
        };
    }
    private void Appearance_OnChanged(object sender,RoutedEventArgs e)
    {
        if(!_appearanceReady)return;
        AppearancePreferences.Apply(ReducedTransparencyChoice.IsChecked==true);
        FooterStatusText.Text=ReducedTransparencyChoice.IsChecked==true?"已使用实色外壳 · 外观偏好已保存":"已使用曜石玻璃材质 · 外观偏好已保存";
    }
    private void LiveCardSelected(object? sender,object row)
    {
        if(row is not DeckStripRow {Slot.Card:{} card} item)return;
        _inspectedLiveCard=item;_inspectedEncounter=CurrentEncounterId;_liveInspectorOpen=true;
        LiveCardTitle.Text=UiTranslation.CardName(card.Id,card.Name);
        LiveCardAbility.Text=string.IsNullOrWhiteSpace(card.AbilityText)?"本地卡牌目录暂缺效果描述。":card.AbilityText;
        var manual=item.Slot.Reason.Contains("记忆")||_opponentTracker.Observations.Any(o=>o.Card.Id==card.Id&&o.Evidence?.StartsWith("User ")==true);
        LiveCardSource.Text=$"{card.Provision} 费用 · 第 {item.Slot.Copy} 张副本\n"+
            (manual?"记忆补录 · 用户确认":item.Slot.Reason.StartsWith("生成")?"生成／变化 · 不计起始卡组":item.Slot.State==DeckSlotState.Observed?"屏幕观测 · 可在下方核对误识别":"参考信息 · 尚未确认为本局起始牌")+
            "\n"+UiTranslation.Translate(item.Slot.Reason);
        LiveCardPortrait.Source=CardArt(card);LiveCardFullButton.IsEnabled=true;
        LiveRejectButton.IsEnabled=CanRejectLiveCard(item);
        UpdateWorkspaceLayout();
        if(!_wideWorkspace)CloseLiveInspectorButton.Focus();
    }
    private void CloseLiveInspector_OnClick(object sender,RoutedEventArgs e)
    {_liveInspectorOpen=false;UpdateWorkspaceLayout();OpponentDeckCards.Focus();}
    private void LiveCardFull_OnClick(object sender,RoutedEventArgs e)
    {if(_inspectedLiveCard?.Slot.Card is {} card)ShowCardPreview(card);}
    private bool CanRejectLiveCard(DeckStripRow? item)=>_inspectedEncounter==CurrentEncounterId &&
        item is {Slot.State:DeckSlotState.Observed,Slot.Card:not null}&&!item.Slot.Reason.StartsWith("生成");
    private void LiveReject_OnClick(object sender,RoutedEventArgs e)
    {
        if(_inspectedLiveCard is not {Slot.Card:{} card} item)return;
        if(_inspectedEncounter!=CurrentEncounterId){FooterStatusText.Text="对局已经变化，请重新选择当前对局的卡牌。";return;}
        if(!CanRejectLiveCard(item)){FooterStatusText.Text="生成／变化记录不能通过起始牌误识别操作移除。";return;}
        var memory=LiveEvidenceEditor();
        int? previousManual=memory?.Corrections.TryGetValue(card.Id,out var prior)==true?prior:null;
        var correctionEncounter=CurrentEncounterId;
        var rejected=_opponentEdits.CorrectedEvidence.ToHashSet();
        OpponentSlot_OnActivated(this,item);
        var added=_opponentEdits.CorrectedEvidence.Except(rejected).ToArray();
        var afterManual=memory?.Corrections.GetValueOrDefault(card.Id);
        if(added.Length==0&&previousManual==afterManual){FooterStatusText.Text="这张记录没有可移除的起始牌副本。";return;}
        _undoLiveCorrection=()=> {
            if(correctionEncounter!=CurrentEncounterId)throw new InvalidOperationException("对局已经变化，旧修正不能用于当前对局。");
            if(memory is not null&&previousManual is {} oldCount) {
                if(memory.Corrections.GetValueOrDefault(card.Id)!=afterManual)
                    throw new InvalidOperationException("这张牌已被再次校对，请在补录窗口修改数量。");
                memory.SetCopies(card,oldCount);
            }
            foreach(var key in added)_opponentEdits.RestoreEvidence(card,key.Copy);
            RenderLiveInference();PersistCurrentMatch();
        };
        LiveUndoButton.IsEnabled=true;LiveRejectButton.IsEnabled=false;
        LiveCardSource.Text="已移除一张误识别 · 原始观测保留在审计记录中，可点击撤销。";
    }
    private void LiveUndo_OnClick(object sender,RoutedEventArgs e)
    {
        try {if(_undoLiveCorrection is null)return;_undoLiveCorrection.Invoke();_undoLiveCorrection=null;LiveUndoButton.IsEnabled=false;LiveRejectButton.IsEnabled=CanRejectLiveCard(_inspectedLiveCard);
            FooterStatusText.Text="已撤销本次卡牌修正，原记录已恢复。";}
        catch(Exception error){FooterStatusText.Text="未能撤销："+error.Message;}
    }
    private void OpenLiveEvidence_OnClick(object sender,RoutedEventArgs e)
    {
        var editor=LiveEvidenceEditor();
        if(editor is null){FooterStatusText.Text="当前没有可补录的实时对局。可在“对局复盘”打开一局，再补录／校对。";return;}
        var catalog=_candidateCatalog??=GwentOneCardCatalog.Load(CardDataPath);
        new MatchEvidenceWindow(editor,catalog,_opponentTracker.HasStableFaction?_opponentTracker.Faction:null,_cachedDecks){Owner=this}.Show();
    }
    private void OpenLiveMore_OnClick(object sender,RoutedEventArgs e)
    {
        var menu=new ContextMenu();
        void Add(string title,RoutedEventHandler action){var item=new MenuItem{Header=title};item.Click+=action;menu.Items.Add(item);}
        Add("本局截图",(_,_)=>ShowPage(UiPage.Pinned));Add("候选卡牌／补录",(_,_)=>ShowPage(UiPage.Candidates));
        Add("高级信息与证据",(_,_)=>ShowPage(UiPage.Plays));Add("保存对手观察记录",OpenOpponentMemory_OnClick);
        Add("开关悬停参考估计",(_,_)=>ShowHoverThreats.IsChecked=ShowHoverThreats.IsChecked!=true);
        menu.Items.Add(new Separator());Add("视频识别",StreamMode_OnClick);Add("显示与多屏",WorkspaceDisplay_OnClick);
        menu.PlacementTarget=sender as FrameworkElement;menu.IsOpen=true;
    }
}
