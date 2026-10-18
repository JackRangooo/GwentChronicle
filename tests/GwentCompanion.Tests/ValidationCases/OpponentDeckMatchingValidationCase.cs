using GwentCompanion.Core.Data;
using GwentCompanion.Core.Domain;
using GwentCompanion.Core.Inference;

internal sealed class OpponentDeckMatchingValidationCase : IContributorValidationCase
{
    public string Id => "opponent-deck-matching";
    public string Kind => "data-integrity";
    public string Summary => "Rank distinct cached deck compositions from observed starting cards without treating a partial match as a full deck fact.";

    public Task RunAsync(ContributorValidationContext context)
    {
        void Check(bool ok, string message) => ContributorValidationContext.Check(ok, message);

        var leaderA = new CardDefinition("leader-a", "Leader A", "Syndicate", CardKind.Leader, 0);
        var leaderB = new CardDefinition("leader-b", "Leader B", "Syndicate", CardKind.Leader, 0);
        var gold = new CardDefinition("seen-gold", "Observed Gold", "Syndicate", CardKind.Unit, 8, IsGold: true);
        var bronze = new CardDefinition("seen-bronze", "Observed Bronze", "Syndicate", CardKind.Unit, 4);
        var created = new CardDefinition("created", "Created Card", "Syndicate", CardKind.Unit, 7);
        var generated = new CardDefinition("generated", "Generated Token", "Syndicate", CardKind.Unit, 1, CanBeInStartingDeck: false);
        var cards = new[] { leaderA, leaderB, gold, bronze, created, generated };
        var observations = new[]
        {
            Seen(gold, 1), Seen(bronze, 2), Seen(bronze, 1), Seen(created, 1, CardProvenance.Created),
            Seen(generated, 1),
        };

        DeckDefinition Deck(string id, string leader, bool twoBronze, int fillerStart, string? patch = null,
            string faction = "Syndicate")
        {
            var deckCards = new List<DeckCard> { new(gold), new(bronze, twoBronze ? 2 : 1) };
            var remaining = 25 - deckCards.Sum(item => item.Count);
            for (var i = 0; i < remaining; i++)
                deckCards.Add(new(new CardDefinition($"fill-{fillerStart + i}", $"Filler {fillerStart + i}", faction,
                    CardKind.Unit, 4), 1));
            return new DeckDefinition(id, id, faction, leader, 0, deckCards,
                Patches: patch is null ? null : [new(patch, false, "validation")]);
        }

        var best = Deck("best", "Leader A", true, 0, "14.10");
        var sameComposition = best with { Id = "best-link", Name = "duplicate cached link" };
        var partial = Deck("partial", "Leader A", false, 100, "14.9");
        var zeroOverlap = new DeckDefinition("zero", "zero", "Syndicate", "Leader A", 0,
            Enumerable.Range(0, 25).Select(i => new DeckCard(new($"zero-{i}", $"Zero {i}", "Syndicate", CardKind.Unit, 4))).ToArray());
        var candidates = new[] { best, sameComposition, partial, zeroOverlap };
        // These small fixtures isolate ranking behavior; production calls retain the strict default gates.
        var report = OpponentDeckMatcher.Match(candidates, observations, "Syndicate", "leader-a", "14.10", catalog: cards,
            requireRecommendationThresholds: false);
        Check(report.Matches.Count == 2 && report.Matches[0].Deck.Id == "best" && report.Matches[1].Deck.Id == "partial",
            "The matcher did not return the two highest-ranked distinct compositions.");
        Check(report.Matches[0].ScorePercent == 100 && report.Matches[0].MissingObservedCards.Count == 0,
            "A partial observation match should score 100% when all observed cards are accounted for.");
        Check(report.Matches[1].MatchedCopies == 2 && report.Matches[1].MissingObservedCards.Single().Copies == 1,
            "A deck with one fewer observed bronze copy did not report its missing copy.");
        Check(report.Evidence.Single(item => item.Card.Id == bronze.Id).Copies == 2 && report.DuplicateDecksCollapsed == 1,
            "Repeated sightings were summed or duplicate composition links were not collapsed.");
        Check(report.CompatibleDistinctDeckCount == 3 && report.Matches.All(item => item.Deck.Id != "zero"),
            "Compatible deck count or zero-overlap filtering is incorrect.");
        Check(observations.Length == 5 && observations[1].ObservedCopies == 2 && candidates.Length == 4,
            "The matcher mutated an input observation or deck collection.");

        var factionMismatch = OpponentDeckMatcher.Match(candidates, observations, "Monsters", "Leader A", catalog: cards);
        var leaderMismatch = OpponentDeckMatcher.Match(candidates, observations, "Syndicate", "Leader B", catalog: cards);
        Check(!factionMismatch.HasMatches && factionMismatch.NoMatchReason?.Contains("strict", StringComparison.Ordinal) == true,
            "A known faction mismatch was relaxed or not explained.");
        Check(!leaderMismatch.HasMatches && leaderMismatch.NoMatchReason?.Contains("strict filters", StringComparison.Ordinal) == true,
            "A known starting-leader mismatch was relaxed or not explained.");

        var noEvidence = OpponentDeckMatcher.Match(candidates, [Seen(created, 1, CardProvenance.Created), Seen(generated, 1)], catalog: cards);
        Check(!noEvidence.HasEvidence && !noEvidence.HasMatches,
            "Generated or non-ownable cards were used as starting-deck evidence.");

        var player = new MatchPlayer("Syndicate", "Leader A", null,
            [new(gold.Id, 1, MatchCardEvidence.Observed, CardProvenance.ProbableStartingDeck, 254)],
            [new(bronze.Id, 2, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, 254)],
            [new(bronze.Id, 2, MatchCardEvidence.ManualHypothesis, CardProvenance.ProbableStartingDeck, 254)]);
        var historical = OpponentDeckMatcher.Match(candidates, player, cards);
        Check(historical.Evidence.Count == 1 && historical.Evidence[0].Card.Id == gold.Id,
            "The historical adapter used selected-reference or manual-hypothesis cards as observations.");

        var tieA = Deck("tie-a", "Leader A", false, 200, "14.10");
        var tieB = Deck("tie-b", "Leader A", false, 300, "14.9");
        var tieFirst = OpponentDeckMatcher.Match([tieB, tieA], [Seen(gold, 1)], "Syndicate", "Leader A", "14.10", catalog: cards,
            requireRecommendationThresholds: false);
        var tieSecond = OpponentDeckMatcher.Match([tieA, tieB], [Seen(gold, 1)], "Syndicate", "Leader A", "14.10", catalog: cards,
            requireRecommendationThresholds: false);
        Check(tieFirst.TopTieCount == 2 && tieFirst.Matches[0].Deck.Id == "tie-a" &&
              tieSecond.Matches[0].Deck.Id == tieFirst.Matches[0].Deck.Id,
            "Current-patch preference or deterministic tie ordering failed.");
        Check(tieFirst.Matches[0].ScorePercent == 100,
            "A single observed card accounted for by a candidate did not yield full observed-evidence coverage.");
        Check(OpponentDeckMatcher.Match([tieA,tieB],[Seen(gold,1)],"Unknown faction","Unknown leader",catalog:cards,
                requireRecommendationThresholds:false).Matches.Count==2,
            "Unknown identity placeholders were applied as impossible hard filters.");

        RecommendationThresholds(Check);
        return Task.CompletedTask;
    }

    private static void RecommendationThresholds(Action<bool, string> check)
    {
        var evidenceCards = Enumerable.Range(0, 15).Select(i => new CardDefinition(
            $"gate-{i}", $"Gate {i}", "Syndicate", CardKind.Unit, i < 11 ? 20 : 1)).ToArray();
        DeckDefinition Complete(string id, IEnumerable<DeckCard> included)
        {
            var includedCards = included.ToList();
            var remaining = 25 - includedCards.Sum(item => item.Count);
            includedCards.AddRange(Enumerable.Range(0, remaining).Select(i => new DeckCard(
                new CardDefinition($"{id}-filler-{i}", $"Filler {i}", "Syndicate", CardKind.Unit, 4))));
            return new(id, id, "Syndicate", "Leader A", 0, includedCards);
        }

        var complete = Complete("complete", evidenceCards.Select(card => new DeckCard(card)));
        var eleven = OpponentDeckMatcher.Match([complete], evidenceCards.Take(11).Select(card => Seen(card, 1)));
        check(eleven.TotalObservedCopies == 11 && !eleven.HasMatches &&
              eleven.NoMatchReason?.Contains("at least 12", StringComparison.Ordinal) == true,
            "Eleven observed copies must not produce a recommendation, even with full coverage.");
        var twelve = OpponentDeckMatcher.Match([complete], evidenceCards.Take(12).Select(card => Seen(card, 1)));
        check(twelve.TotalObservedCopies == 12 && twelve.Matches.Single().ObservedCoveragePercent == 100,
            "Exactly twelve observed copies should allow a fully covered candidate.");

        // Both decoys score above 80% by provisions and outrank qualifying decks, but cover only 11/15 copies.
        var weightedDecoyA = Complete("weighted-a", evidenceCards.Take(11).Select(card => new DeckCard(card)));
        var weightedDecoyB = Complete("weighted-b", evidenceCards.Take(10).Append(evidenceCards[14]).Select(card => new DeckCard(card)));
        var qualifyingCards = evidenceCards.Take(9).Concat(evidenceCards.Skip(12)).Select(card => new DeckCard(card)).ToArray();
        var qualifyingA = Complete("qualifying-a", qualifyingCards);
        var qualifyingB = Complete("qualifying-b", qualifyingCards);
        var qualifyingC = Complete("qualifying-c", qualifyingCards);
        var duplicate = qualifyingA with { Id = "qualifying-a-duplicate" };
        var candidates = new[] { weightedDecoyA, weightedDecoyB, qualifyingC, qualifyingB, qualifyingA, duplicate };
        var observations = evidenceCards.Select(card => Seen(card, 1)).ToArray();
        var rankingOnly = OpponentDeckMatcher.Match(candidates, observations, requireRecommendationThresholds: false);
        check(rankingOnly.Matches.All(item => item.ScorePercent >= 80 && item.ObservedCoveragePercent < 80),
            "The weighted-score decoys do not exercise the distinction between weighted score and copy coverage.");
        var gated = OpponentDeckMatcher.Match(candidates, observations, limit: 10);
        check(gated.Matches.Count == 2 && gated.Matches.All(item => item.ObservedCoveragePercent == 80) &&
              gated.Matches.All(item => item.Deck.Id.StartsWith("qualifying-", StringComparison.Ordinal)) &&
              gated.TopTieCount == 3 && gated.DuplicateDecksCollapsed == 1,
            "Exactly 80% copy coverage must qualify before selecting at most two distinct recommendations.");
        var lowCoverage = OpponentDeckMatcher.Match([weightedDecoyA, weightedDecoyB], observations);
        check(!lowCoverage.HasMatches && lowCoverage.NoMatchReason?.Contains("80%", StringComparison.Ordinal) == true,
            "Low copy coverage must produce an explicit no-recommendation reason despite a high provision score.");
        var wrongFaction = OpponentDeckMatcher.Match([complete], observations, "Monsters", "Leader A");
        var wrongLeader = OpponentDeckMatcher.Match([complete], observations, "Syndicate", "Leader B");
        check(!wrongFaction.HasMatches && !wrongLeader.HasMatches &&
              wrongLeader.NoMatchReason?.Contains("strict filters", StringComparison.Ordinal) == true,
            "Recommendation gates must preserve exact faction and starting-leader filtering.");

        var gold = new CardDefinition("gate-gold", "Gate Gold", "Syndicate", CardKind.Unit, 10, IsGold: true);
        var token = new CardDefinition("gate-token", "Token", "Syndicate", CardKind.Unit, 1, CanBeInStartingDeck: false);
        var normalizedCards = evidenceCards.Take(5).Select(card => new DeckCard(card, 2)).Append(new DeckCard(gold)).ToArray();
        var normalizedDeck = Complete("normalized", normalizedCards.Append(new DeckCard(evidenceCards[5])));
        var inflated = evidenceCards.Take(5).SelectMany(card => new[] { Seen(card, 99), Seen(card, 2), Seen(card, 1) })
            .Concat([Seen(gold, 99), Seen(gold, 1), Seen(token, 99), Seen(evidenceCards[6], 99, CardProvenance.Created)])
            .ToArray();
        var normalized = OpponentDeckMatcher.Match([normalizedDeck], inflated);
        check(normalized.TotalObservedCopies == 11 && !normalized.HasMatches && normalized.Evidence.Count == 6 &&
              normalized.Evidence.Single(item => item.Card.Id == gold.Id).Copies == 1 &&
              normalized.Evidence.Where(item => !item.Card.IsGold).All(item => item.Copies == 2),
            "Repeated sightings, illegal copy counts, created cards or tokens inflated the twelve-copy threshold.");
        var normalizedTwelve = OpponentDeckMatcher.Match([normalizedDeck], inflated.Append(Seen(evidenceCards[5], 1)));
        check(normalizedTwelve.TotalObservedCopies == 12 && normalizedTwelve.Matches.Single().MatchedCopies == 12,
            "The evidence gate must count normalized card copies rather than distinct card rows.");

        var actual = evidenceCards.Take(11).Select(card => new MatchCard(
            card.Id, 1, MatchCardEvidence.Observed, CardProvenance.ProbableStartingDeck, 254)).ToArray();
        var player = new MatchPlayer("Syndicate", "Leader A", null, actual,
            [new(evidenceCards[11].Id, 1, MatchCardEvidence.SelectedReference, CardProvenance.Unknown, 254)],
            [new(evidenceCards[12].Id, 1, MatchCardEvidence.ManualHypothesis, CardProvenance.ProbableStartingDeck, 254)]);
        var historical = OpponentDeckMatcher.Match([complete], player, evidenceCards);
        check(historical.TotalObservedCopies == 11 && !historical.HasMatches,
            "Selected references or manual hypotheses unlocked the historical recommendation gate.");
        var observedTwelve = player with { Observations = actual.Append(new MatchCard(evidenceCards[11].Id, 1,
            MatchCardEvidence.Remembered, CardProvenance.ProbableStartingDeck, 254)).ToArray() };
        check(OpponentDeckMatcher.Match([complete], observedTwelve, evidenceCards).Matches.Count == 1,
            "The historical adapter failed to allow twelve actual observed/remembered starting-card copies.");
    }

    private static ObservedCard Seen(CardDefinition card, int copies, CardProvenance provenance = CardProvenance.ProbableStartingDeck) =>
        new(card, provenance, 1, DateTimeOffset.UnixEpoch, ObservedCopies: copies);
}
