using System.IO;
using GwentCompanion.App.Localization;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

namespace GwentCompanion.App;

public partial class MatchAnalysisWindow
{
    private sealed record OpponentDeckNameVm(string Label, string Tooltip);
    private sealed record OpponentNameEvidence(string? ReferenceKey, OpponentDeckMatchReport? Report, string? Error = null);
    private Dictionary<string, string> _referenceNames = new(StringComparer.Ordinal);
    private Dictionary<string, string> _observationNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OpponentNameEvidence> _opponentNameEvidence = new(StringComparer.Ordinal);
    private int _deckNamePageVersion;

    private static string RowKey(MatchAnalysisEntry entry) => MatchReviewStore.Key(entry.InstallationId, entry.MatchId);

    private void ConfigureDeckNames(ObservedDeckRecord[] observed)
    {
        _referenceNames = _libraryDecks.GroupBy(d => MatchAnalysis.DeckKey(MatchReviewStore.Reference(d)))
            .Where(g => g.Key is not null)
            .Select(g => (Key:g.Key!, Names:g.Select(d => DeckDisplayNames.DisplayCandidate(d.Id,d.Name)).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Names.Length == 1).ToDictionary(g => g.Key,g => g.Names[0],StringComparer.Ordinal);
        _observationNames = observed.Where(r => r.Side == PlayerSide.Opponent && !r.TrainingOnly && r.ReviewMatchKey is not null)
            .Select(r => (Key:r.ReviewMatchKey!, Name:DeckDisplayNames.Alias("observed:Opponent:"+r.SessionId,
                $"Observed opponent deck · {r.RecordedAt:yyyy-MM-dd HH:mm}")))
            .Where(r => !string.IsNullOrWhiteSpace(r.Name)).GroupBy(r => r.Key)
            .Select(g => (Key:g.Key, Names:g.Select(r => r.Name!).Distinct(StringComparer.Ordinal).ToArray()))
            .Where(g => g.Names.Length == 1).ToDictionary(g => g.Key,g => g.Names[0],StringComparer.Ordinal);
        _opponentNameEvidence.Clear();
        _deckNamePageVersion++;
    }

    private string? CurrentReferenceName(string? key) => key is null ? null : _referenceNames.GetValueOrDefault(key);

    private OpponentDeckNameVm OpponentName(string key, OpponentNameEvidence? evidence)
    {
        if (_observationNames.TryGetValue(key,out var name))
            return new("对手卡组："+name+" · 手动命名", "这是你在卡组库为本场对手观察记录保存的名称；观察记录仍是部分牌表。修改该记录的名称后会同步显示。");
        if (CurrentReferenceName(evidence?.ReferenceKey) is { } referenceName)
            return new("对手卡组："+referenceName, "名称来自本场保存的完整参考牌表，并与卡组库当前名称同步。");
        if (evidence?.Report is { HasMatches:true } report)
        {
            var lines=report.Matches.Take(2).Select((hit,index) =>
                $"候选{(index==0 ? "①" : "②")}：{DeckDisplayNames.DisplayCandidate(hit.Deck.Id,hit.Deck.Name)} · 张数覆盖 {hit.ObservedCoveragePercent:0}%").ToArray();
            return new(string.Join("\n",lines), string.Join("\n",lines)+
                $"\n基于 {report.TotalObservedCopies} 张已见卡，按阵营与初始领袖筛选。覆盖率表示候选解释已见卡的程度，并非整套卡组确认率。"+
                (report.TopTieCount>1 ? $"\n有 {report.TopTieCount} 套牌表并列最高；这里只列前两套。" : "")+
                "\n展开对局可查看候选完整牌表与卡牌详情。在卡组库重命名卡组或卡组簇后，这里的候选名称会同步更新。");
        }
        return new(evidence is null ? "对手卡组：正在匹配…" : "对手卡组：证据不足，暂不推荐",
            evidence?.Error ?? (evidence?.Report?.HasEvidence == true
                ? $"已记录 {evidence.Report.TotalObservedCopies} 张有效卡牌。推荐要求至少 12 张，且候选包含至少 80% 的已记录卡牌；也须满足阵营和初始领袖。可展开对局直接补录／校对卡牌。"
                : "缺少可用于匹配的起始牌组卡牌；可展开对局补录，或在卡组库给关联的对手观察记录命名。"));
    }

    // Match only the visible page, off the UI thread. Updating properties keeps
    // expanded rows, scroll position and keyboard focus intact when names arrive.
    private async void UpdateOpponentDeckNames(HistoryVm[] rows)
    {
        var pageVersion=++_deckNamePageVersion; var loadVersion=_loadVersion;
        foreach(var row in rows) row.SetOpponentDeck(row.Entry.Abnormal ? new("异常对局：不推荐卡组", "异常对局不进入卡组胜率或候选推荐。") : OpponentName(RowKey(row.Entry),_opponentNameEvidence.GetValueOrDefault(RowKey(row.Entry))));
        var pending=rows.Where(r => !r.Entry.Abnormal && !_observationNames.ContainsKey(RowKey(r.Entry)) && !_opponentNameEvidence.ContainsKey(RowKey(r.Entry))).ToArray();
        if(pending.Length==0)return;
        var decks=_libraryDecks;var catalog=_catalog;
        try
        {
            var results=await Task.Run(() =>
            {
                var reviews=new MatchReviewStore(_directory).Load();
                return pending.Select(row =>
                {
                    _lifetime.Token.ThrowIfCancellationRequested();
                    try
                    {
                        var key=RowKey(row.Entry);
                        var match=MatchIdentityInference.Apply(MatchReviewStore.Apply(LocalMatchStore.Read(row.Entry.Path),reviews.GetValueOrDefault(key)),catalog);
                        var referenceKey=MatchAnalysis.DeckKey(match.Opponent);
                        return (Key:key,Evidence:new OpponentNameEvidence(referenceKey,
                            referenceKey is not null && _referenceNames.ContainsKey(referenceKey) ? null : OpponentDeckMatcher.Match(decks,match.Opponent,catalog,match.Patch)));
                    }
                    catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException)
                    { return (Key:RowKey(row.Entry),Evidence:new OpponentNameEvidence(null,null,"无法读取该场的卡牌记录。请刷新数据后重试。")); }
                }).ToArray();
            },_lifetime.Token);
            if(_lifetime.IsCancellationRequested || loadVersion!=_loadVersion)return;
            foreach(var result in results)_opponentNameEvidence[result.Key]=result.Evidence;
            if(pageVersion!=_deckNamePageVersion)return;
            foreach(var row in rows.Where(r => !r.Entry.Abnormal))row.SetOpponentDeck(OpponentName(RowKey(row.Entry),_opponentNameEvidence.GetValueOrDefault(RowKey(row.Entry))));
        }
        catch(OperationCanceledException) { }
        catch(Exception)
        {
            if(_lifetime.IsCancellationRequested || loadVersion!=_loadVersion || pageVersion!=_deckNamePageVersion)return;
            foreach(var row in pending)row.SetOpponentDeck(new("对手卡组：待识别","暂时无法加载候选名称。请点击刷新数据；原始对局不受影响。"));
        }
    }
}
