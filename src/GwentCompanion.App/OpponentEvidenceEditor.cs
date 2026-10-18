using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;
using System.IO;

namespace GwentCompanion.App;

/// <summary>A captured context: an open preview must never edit a later encounter.</summary>
public sealed class OpponentEvidenceEditor(string directory, string recordKey,
    IReadOnlyList<ObservedCard> baseline, string? faction, Action? changed = null, Func<bool>? canEdit = null, bool forUser = false)
{
    public string RecordKey { get; } = recordKey;
    public PlayerSide Side => forUser ? PlayerSide.User : PlayerSide.Opponent;
    private readonly MatchReviewStore _store = new(directory);
    public bool CanEdit => canEdit?.Invoke() != false;
    private static readonly object CacheGate = new();
    private static readonly Dictionary<string,(DateTime Stamp,long Length,Dictionary<string,MatchReview> Reviews)> Cache = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, int> Corrections
    {
        get {
            var path = Path.Combine(Path.GetFullPath(directory),"match-reviews.json");
            lock(CacheGate) {
                var file = new FileInfo(path); var stamp=file.Exists ? file.LastWriteTimeUtc : DateTime.MinValue; var length=file.Exists ? file.Length : 0;
                if (!Cache.TryGetValue(path,out var cached) || cached.Stamp!=stamp || cached.Length!=length)
                    Cache[path]=cached=(stamp,length,_store.Load());
                var review=cached.Reviews.GetValueOrDefault(RecordKey);
                return (forUser ? review?.UserCardCopies : review?.OpponentCardCopies) ?? new Dictionary<string,int>();
            }
        }
    }
    public ObservedCard[] Evidence()
    {
        var counts = Corrections;
        var definitions = baseline.Select(c => c.Card).Concat(_rememberedDefinitions.Values).DistinctBy(c => c.Id).ToDictionary(c => c.Id);
        foreach(var id in counts.Keys) if (!definitions.ContainsKey(id) && _catalog.FirstOrDefault(c=>c.Id==id) is { } card) definitions[id]=card;
        return baseline.Where(c => !counts.ContainsKey(c.Card.Id)).Concat(counts.Where(p => p.Value > 0 && definitions.ContainsKey(p.Key))
            .Select(p => new ObservedCard(definitions[p.Key], CardProvenance.ConfirmedStartingDeck, 1,
                DateTimeOffset.MinValue, ObservedCopies:p.Value))).ToArray();
    }
    private readonly Dictionary<string, CardDefinition> _rememberedDefinitions = new(StringComparer.Ordinal);
    private IEnumerable<CardDefinition> _catalog = [];
    public OpponentEvidenceEditor WithCatalog(IEnumerable<CardDefinition> catalog)
    {
        _catalog = catalog;
        return this;
    }
    public int Count(string cardId) => Evidence().Where(c => c.Card.Id == cardId).Select(c => c.ObservedCopies).DefaultIfEmpty(0).Max();
    public void ApplyDraft(IReadOnlyList<DeckCard> cards)
    {
        if(!CanEdit)throw new InvalidOperationException("对局已切换，请从历史记录重新打开。");
        if(cards.Any(c=>!StartingDeckRules.IsLegalStartingCard(c.Card,MatchAnalysis.Factions.Contains(faction)?faction:null) || c.Count<1 || c.Count>(c.Card.IsGold?1:2)) || cards.Select(c=>c.Card.Id).Distinct().Count()!=cards.Count)
            throw new ArgumentException("底稿的阵营、卡牌或数量不符合规则。");
        var current=Evidence().GroupBy(c=>c.Card.Id).ToDictionary(g=>g.Key,g=>g.Max(c=>c.ObservedCopies));
        var target=cards.ToDictionary(c=>c.Card.Id,c=>c.Count);var edits=current.Keys.Union(target.Keys).Where(id=>current.GetValueOrDefault(id)!=target.GetValueOrDefault(id)).ToDictionary(id=>id,id=>target.GetValueOrDefault(id));
        foreach(var item in cards)_rememberedDefinitions[item.Card.Id]=item.Card;
        _store.SetCardCopiesBatch(RecordKey,edits,forUser);Invalidate();changed?.Invoke();
    }
    public void RestoreCorrections(IReadOnlyDictionary<string,int> values)
    {
        if(!CanEdit)throw new InvalidOperationException("对局已切换，请从历史记录重新打开。");
        _store.SetCardCopiesBatch(RecordKey,values,forUser,replace:true);Invalidate();changed?.Invoke();
    }
    private void Invalidate(){lock(CacheGate)Cache.Remove(Path.Combine(Path.GetFullPath(directory),"match-reviews.json"));}
    public void SetCopies(CardDefinition card, int copies)
    {
        if (!CanEdit) throw new InvalidOperationException("此预览所属的对局已结束或切换，请从对战记录重新打开后补录。");
        if (!StartingDeckRules.IsLegalStartingCard(card, MatchAnalysis.Factions.Contains(faction)?faction:null) || copies < 0 || copies > (card.IsGold ? 1 : 2))
            throw new ArgumentException("这张卡或数量不符合起始牌组规则。");
        _rememberedDefinitions[card.Id] = card;
        if(forUser)_store.SetUserCardCopies(RecordKey,card.Id,copies);else _store.SetOpponentCardCopies(RecordKey, card.Id, copies);
        lock(CacheGate) Cache.Remove(Path.Combine(Path.GetFullPath(directory),"match-reviews.json"));
        changed?.Invoke();
    }
}
