using System.IO;
using System.Text.Json.Nodes;
using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal static class DeckVariationTests
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    public static void Run(string root)
    {
        var cards = Enumerable.Range(0, 25).Select(i => new DeckCard(new("variation" + i, "Card " + i, "Skellige", CardKind.Unit,
            i < 5 ? 10 : 5, IsGold: i < 5))).ToArray();
        var a = new DeckDefinition("original", "Variation fixture", "Skellige", "Onslaught", 16, cards,
            Patches: [new("14.7", false, "Fixture A")], Occurrences: [new("obs-a", "14.7", "Import", "Fixture A")]);
        DeckDefinition Change(string id, params int[] indices) => a with { Id = id, Name = id, Cards = a.Cards.Select((c, i) =>
            indices.Contains(i) ? c with { Card = c.Card with { Id = "other" + i, Name = "Other " + i } } : c).ToArray(),
            Patches = [new("14.8", false, "Fixture " + id)], Occurrences = [new("obs-" + id, "14.8", "Import", "Fixture " + id)] };
        var b = Change("cheap-two", 5, 6); var gold = Change("gold-two", 0, 1); var c = Change("cheap-four", 5, 6, 7, 8);
        var boundary = Change("boundary", 0, 5);
        var metric = new DeckVariationSimilarity([a, b, gold, c, boundary]);
        Check(metric.IsVariation(a, b) && !metric.IsVariation(a, gold), "Expensive and cheap two-card swaps were treated alike.");
        Check(Math.Abs(metric.Compare(a, boundary).Ratio - .9) < 1e-10 && metric.IsVariation(a, boundary), "90% boundary not inclusive.");
        Check(metric.Compare(a, b) == metric.Compare(b, a), "Equal-spend overlap is not symmetric.");
        Check(!metric.IsVariation(a, a with { Leader = "Ursine Ritual" }) && !metric.IsVariation(a, a with { Cards = cards.Take(24).ToArray() }), "Header/partial guard failed.");
        var rulesChanged = a with { Cards = a.Cards.Select((x, i) => i == 24 ? x with { Card = x.Card with { Name = "Golden Nekker" } } : x).ToArray() };
        Check(!metric.IsVariation(a, rulesChanged), "Construction-rule change was grouped as tuning.");
        var bronzeChanged=Change("new-bronze-package",Enumerable.Range(5,20).ToArray());
        var fourGoldChanged=Change("four-gold-changed",0,1,2,3);
        Check(metric.IsCoreVariation(a,bronzeChanged),"Bronze tuning split an identical gold core.");
        Check(metric.IsCoreVariation(a,gold) && metric.CompareCore(a,gold).GoldReplacements==2,"The up-to-three gold replacements rule did not admit a two-card variation.");
        Check(!metric.IsCoreVariation(a,fourGoldChanged),"A largely different gold mechanism was grouped through bronze overlap.");
        Check(!metric.IsCoreVariation(a,rulesChanged) && !metric.IsCoreVariation(a,a with {Leader="Ursine Ritual"}),"Core grouping crossed construction or leader boundaries.");
        var duplicate = a with { Cards = [cards[5] with { Count = 2 }] };
        Check(metric.Compare(duplicate, a).SharedCopies == 1 && metric.Compare(duplicate, a).SharedProvisions == 5, "Bronze duplicate overlap counted more than shared copies.");
        var reprice = a with { Cards = cards.Select(x => x with { Card = x.Card with { Provision = x.Card.Provision + 1 } }).ToArray() };
        Check(new DeckVariationSimilarity([a, reprice]).Compare(a, reprice).Ratio == 1, "Historical price changes created composition differences.");
        var library = new DeckLibrary(useLegacyVariationPolicy:true); library.Merge([a, b, c]); library.EnsureVariationGroups();
        Check(library.VariationGroups.Count == 2, "Similarity chaining joined distant endpoints.");
        foreach (var group in library.VariationGroups)
            foreach (var left in library.GroupVariants(group)) foreach (var right in library.GroupVariants(group))
                Check(metric.IsVariation(left.Deck, right.Deck), "A group contains an incompatible pair.");
        var reordered = new DeckLibrary(useLegacyVariationPolicy:true); reordered.Merge([c, b, a]); reordered.EnsureVariationGroups();
        Check(library.VariationGroups.Select(g => string.Join(',', g.Members)).SequenceEqual(reordered.VariationGroups.Select(g => string.Join(',', g.Members))), "Initial grouping depends on import enumeration order.");
        var clusters = new DeckLibrary(useLegacyVariationPolicy:true); clusters.Merge([a,b,c,gold]); clusters.EnsureVariationGroups();
        var named = clusters.VariationGroupFor(a.Id)!;
        clusters.RenameVariationGroup(named.Id,"海盗微调版");
        var exactBefore = clusters.Records.Select(r => r.Fingerprint).Order().ToArray();
        Check(clusters.ConsolidateVariationGroups(.85) && clusters.VariationPolicy!.Threshold==.85,"New cluster policy was not applied.");
        Check(clusters.Records.Select(r=>r.Fingerprint).Order().SequenceEqual(exactBefore) && clusters.VariationGroups.Any(g=>g.Name=="海盗微调版" && g.CustomName),"Clustering lost exact lists or a custom group title.");
        foreach(var group in clusters.VariationGroups) foreach(var left in clusters.GroupVariants(group)) foreach(var right in clusters.GroupVariants(group))
            Check(metric.IsVariation(left.Deck,right.Deck,.85),"Relaxed clustering joined a similarity chain.");
        Check(!clusters.ConsolidateVariationGroups(.85),"Repeated migration regrouped an already migrated library.");
        var namedClusters=new DeckLibrary(useLegacyVariationPolicy:true); namedClusters.Merge([a,gold]); namedClusters.EnsureVariationGroups();
        namedClusters.RenameVariationGroup(namedClusters.VariationGroupFor(a.Id)!.Id,"海盗甲");
        namedClusters.RenameVariationGroup(namedClusters.VariationGroupFor(gold.Id)!.Id,"海盗乙");
        namedClusters.ConsolidateVariationGroups(.85);
        Check(namedClusters.VariationGroups.Count==2 && namedClusters.VariationGroups.All(g=>g.CustomName),"Automatic consolidation discarded a conflicting user cluster name.");
        var coreLibrary=new DeckLibrary(useLegacyVariationPolicy:true);coreLibrary.Merge([a,bronzeChanged,gold,fourGoldChanged]);coreLibrary.EnsureVariationGroups();
        coreLibrary.RenameVariationGroup(coreLibrary.VariationGroupFor(a.Id)!.Id,"核心构筑");
        var fingerprints=coreLibrary.Records.Select(r=>r.Fingerprint).Order().ToArray();
        Check(coreLibrary.UpgradeCoreVariationGroups() && coreLibrary.VariationPolicy!.Metric==DeckVariationPolicy.CoreMetric,"Core policy migration did not run.");
        Check(coreLibrary.Records.Select(r=>r.Fingerprint).Order().SequenceEqual(fingerprints) && coreLibrary.VariationGroups.Any(g=>g.Name=="核心构筑" && g.CustomName),"Core migration lost exact decks or user titles.");
        foreach(var group in coreLibrary.VariationGroups) foreach(var left in coreLibrary.GroupVariants(group)) foreach(var right in coreLibrary.GroupVariants(group))
            Check(metric.IsCoreVariation(left.Deck,right.Deck),"Core grouping joined incompatible endpoints through a chain.");
        Check(!coreLibrary.UpgradeCoreVariationGroups(),"Core policy migration was not idempotent.");
        var freshCore=new DeckLibrary();freshCore.Merge([a,gold,bronzeChanged]);freshCore.EnsureVariationGroups();
        Check(freshCore.VariationPolicy!.Metric==DeckVariationPolicy.CoreMetric && freshCore.VariationGroups.Count==1,"New libraries did not start with the core policy.");
        library.SetDetails(a.Id, new("Local notes"));
        library.SetExport(a.Id, new("123", "0123456789abcdef0123456789abcdef", DateTimeOffset.UnixEpoch, true, GameImported: true));
        var folder = Path.Combine(root, "GwentCompanion/diagnostics/deck-variation-tests"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "library.json"); library.Save(path);
        var loaded = DeckLibrary.Load(path);
        Check(loaded.VariationGroups.Select(g => g.Id).SequenceEqual(library.VariationGroups.Select(g => g.Id)), "Group identity was not persisted.");
        var transferPath = Path.Combine(folder, "fixture.gwent-library.json"); loaded.ExportTransfer(transferPath);
        var receiver = new DeckLibrary(useLegacyVariationPolicy:true); var preview = receiver.PreviewTransfer(transferPath);
        Check(receiver.Records.Count == 0 && preview.Added == 3 && preview.Library.VariationGroups.Count == 2, "Preview mutated the receiver or lost grouping.");
        var roundtrip = preview.Library;
        Check(roundtrip.Find(a.Id)?.Export is null && roundtrip.Find(a.Id)?.Details?.Overview == "Local notes", "Account state transferred or notes were lost.");
        Check(roundtrip.Find(a.Id)!.Deck.Occurrences!.Single().Id == "obs-a" && roundtrip.Find(b.Id)!.Deck.Occurrences!.Single().Id == "obs-cheap-two", "Variation histories were merged together.");
        var again = roundtrip.PreviewTransfer(transferPath);
        Check(again.Added == 0 && again.Merged == 3 && again.Library.Find(a.Id)!.Deck.Occurrences!.Count == 1, "Repeated transfer was not idempotent.");
        var before = receiver.Records.Count;
        var invalid = JsonNode.Parse(File.ReadAllText(transferPath))!;
        invalid["Groups"]![0]!["Variants"]![0]!["Fingerprint"] = "tampered";
        var invalidPath = Path.Combine(folder, "invalid.json"); File.WriteAllText(invalidPath, invalid.ToJsonString());
        var rejected = false; try { receiver.PreviewTransfer(invalidPath); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && receiver.Records.Count == before, "Malformed transfer partially changed the receiver.");
        var leftGroup = new DeckLibrary(useLegacyVariationPolicy:true); leftGroup.Merge([a, b]); leftGroup.EnsureVariationGroups();
        var rightGroup = new DeckLibrary(useLegacyVariationPolicy:true); rightGroup.Merge([b, c]); rightGroup.EnsureVariationGroups();
        var conflictPath = Path.Combine(folder, "group-conflict.gwent-library.json"); rightGroup.ExportTransfer(conflictPath);
        rejected = false; try { leftGroup.PreviewTransfer(conflictPath); } catch (InvalidDataException) { rejected = true; }
        Check(rejected && leftGroup.Records.Count == 2 && leftGroup.VariationGroups.Count == 1, "An import joined a similarity chain or partially modified local groups.");
        Check(roundtrip.VariationGroups.Select(g => string.Join(',', g.Members)).SequenceEqual(loaded.VariationGroups.Select(g => string.Join(',', g.Members))), "Transfer changed variation ordering.");
        // Known stratagem enrichment updates the member fingerprint without destroying its stable group ID.
        var enriched = new DeckLibrary(useLegacyVariationPolicy:true); enriched.Merge([a]); enriched.EnsureVariationGroups(); var oldGroup = enriched.VariationGroups[0].Id;
        var stratagem = new CardDefinition("strategy", "Stratagem", "Neutral", CardKind.Stratagem, 0);
        enriched.Merge([a with { Id = "enriched", Stratagem = stratagem }]); enriched.Save(Path.Combine(folder, "enriched.json"));
        Check(enriched.VariationGroupFor(a.Id)?.Id == oldGroup, "Metadata enrichment orphaned the variation membership.");
        var corpus = DeckLibrary.Load(Path.Combine(root, "GwentCompanion/cache/deck-library.json"));
        var catalog = GwentOneCardCatalog.Load(Path.Combine(root, "GwentCompanion/cache/gwent-one-cards.json"));
        corpus.EnsureVariationGroups(catalog); var fullPath = Path.Combine(folder, "corpus.gwent-library.json"); corpus.ExportTransfer(fullPath);
        var restored = new DeckLibrary(useLegacyVariationPolicy:true).PreviewTransfer(fullPath).Library;
        Check(restored.Records.Count == corpus.Records.Count && restored.VariationGroups.Count == corpus.VariationGroups.Count &&
            restored.Records.Sum(r => r.Deck.Occurrences?.Count ?? 0) == corpus.Records.Sum(r => r.Deck.Occurrences?.Count ?? 0), "Real library transfer lost compositions, groups or histories.");
        Console.WriteLine($"Real library: {corpus.Records.Count} compositions → {corpus.VariationGroups.Count} headings; {corpus.VariationGroups.Count(g => g.Members.Length > 1)} multi-variant groups, maximum {corpus.VariationGroups.Max(g => g.Members.Length)} variants; transfer {new FileInfo(fullPath).Length / 1048576d:F1} MiB.");
        Console.WriteLine("PASS provision-weighted variations: boundary, expensive swaps, copies, patch prices, headers, no chaining, persistent groups, account-safe transfer, distinct histories, idempotency, invalid-import isolation, real corpus round trip.");
    }
}
