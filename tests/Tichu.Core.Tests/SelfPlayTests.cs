using System.Text.Json;
using System.Text.Json.Nodes;
using Tichu.Core;
using Tichu.SelfPlay;

namespace Tichu.Core.Tests;

public sealed class SelfPlayTests
{
    [Fact]
    public void CompactCardJsonConverterRoundTripsTheFullDeckAsSmallIds()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new CompactCardJsonConverter());
        var deck = TichuDeck.CreateStandard();

        var json = JsonSerializer.Serialize(deck, options);
        var decoded = JsonSerializer.Deserialize<Card[]>(json, options);

        Assert.Equal(deck, decoded);
        Assert.StartsWith("[0,1,2,3", json);
        Assert.EndsWith(",52,53,54,55]", json);
    }

    [Fact]
    public void SampledBaselineLeadsAreLegalAndFulfillAvailableMahJongWishes()
    {
        for (var seed = 0; seed < 32; seed++)
        {
            var hand = TichuDealer.CreateShuffled(seed).FourteenCardHands[PlayerSeat.Bottom];
            var policy = new RandomBaselinePolicy(new Random(seed));

            var move = policy.ChooseMoveFromHand(hand);

            Assert.False(move.IsPass);
            Assert.True(MoveRules.ValidatePlay(hand, move.Combination!).IsLegal);
            Assert.Equal(
                move.Combination!.Cards.Any(card => card.Rank == CardRank.MahJong),
                move.WishForMahJong.HasValue);
        }

        var handWithWish = new[]
        {
            new Card(CardRank.Five, CardSuit.Jade),
            new Card(CardRank.Seven, CardSuit.Jade),
            new Card(CardRank.Nine, CardSuit.Sword)
        };
        var wishedMove = new RandomBaselinePolicy(new Random(1))
            .ChooseMoveFromHand(handWithWish, pendingWish: CardRank.Five);

        Assert.Single(wishedMove.Combination!.Cards);
        Assert.Equal(CardRank.Five, wishedMove.Combination.Cards[0].Rank);
    }

    [Fact]
    public void MatchGenerationIsDeterministicAndProducesCompleteRoundTrajectories()
    {
        var simulator = new RandomSelfPlaySimulator(targetScore: 100);

        var first = simulator.PlayMatch(gameId: 0, matchSeed: 73_019);
        var second = simulator.PlayMatch(gameId: 0, matchSeed: 73_019);

        Assert.NotEmpty(first.Rounds);
        Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(second));
        var jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new CompactCardJsonConverter());
        var decoded = JsonSerializer.Deserialize<SelfPlayMatchRecord>(
            JsonSerializer.Serialize(first, jsonOptions),
            jsonOptions);
        Assert.NotNull(decoded);
        Assert.Equal(first.Rounds[0].DeckOrder, decoded.Rounds[0].DeckOrder);
        Assert.Equal(first.Rounds[0].Actions[0].HandBefore, decoded.Rounds[0].Actions[0].HandBefore);
        Assert.True(first.BottomTopScore >= first.TargetScore || first.RightLeftScore >= first.TargetScore);

        foreach (var round in first.Rounds)
        {
            Assert.Equal(TichuDeck.CardCount, round.DeckOrder.Count);
            Assert.Equal(TichuDeck.CardCount, round.DeckOrder.Distinct().Count());
            Assert.Equal(4, round.Passes.Count);
            Assert.NotEmpty(round.Actions);
            Assert.Contains(
                round.Score.BottomTopCardPoints + round.Score.RightLeftCardPoints,
                new[] { 100, 200 });

            foreach (var action in round.Actions)
            {
                Assert.Equal(action.HandBefore.Count, action.HandBefore.Distinct().Count());
                Assert.All(action.PlayedCards, card => Assert.Contains(card, action.HandBefore));
                Assert.Equal(action.IsPass, action.PlayedCards.Count == 0);
            }
        }
    }

    [Fact]
    public void SummaryMatchModePreservesSeededScoresAndEveryPolicyDecision()
    {
        const int matchSeed = 73_019;
        var simulator = new RandomSelfPlaySimulator(targetScore: 100);
        var recordedTrace = new Dictionary<PlayerSeat, List<string>>();
        var summaryTrace = new Dictionary<PlayerSeat, List<string>>();

        var recorded = simulator.PlayMatch(
            gameId: 3,
            matchSeed,
            CreateTracingPolicyFactory(matchSeed, recordedTrace));
        var summary = simulator.PlayMatchSummary(
            gameId: 3,
            matchSeed,
            CreateTracingPolicyFactory(matchSeed, summaryTrace));

        Assert.Equal(recorded.GameId, summary.GameId);
        Assert.Equal(recorded.MatchSeed, summary.MatchSeed);
        Assert.Equal(recorded.TargetScore, summary.TargetScore);
        Assert.Equal(recorded.BottomTopScore, summary.BottomTopScore);
        Assert.Equal(recorded.RightLeftScore, summary.RightLeftScore);
        Assert.Equal(recorded.Winner, summary.Winner);
        Assert.Equal(Enum.GetValues<PlayerSeat>(), recordedTrace.Keys.Order().ToArray());
        Assert.Equal(Enum.GetValues<PlayerSeat>(), summaryTrace.Keys.Order().ToArray());
        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            Assert.Equal(recordedTrace[seat], summaryTrace[seat]);
        }
    }

    [Fact]
    public void JsonlStoreResumesAfterPartialFinalLineAndPreservesCompletedMatches()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var journalPath = Path.Combine(directory, "matches.jsonl");
            using (var store = new JsonlSelfPlayStore(directory, baseSeed: 41, targetScore: 1_000))
            {
                store.Save(CreateMatch(gameId: 0, seed: 41));
                Assert.Equal(1, store.CompletedCount);
            }

            File.AppendAllText(journalPath, "{\"partial\":");
            using (var resumed = new JsonlSelfPlayStore(directory, baseSeed: 41, targetScore: 1_000))
            {
                Assert.Equal(1, resumed.CompletedCount);
                Assert.True(resumed.IsCompleted(0));
                Assert.False(resumed.IsCompleted(1));
            }

            File.AppendAllText(journalPath, "{\"torn\":}\n");
            using (var resumed = new JsonlSelfPlayStore(directory, baseSeed: 41, targetScore: 1_000))
            {
                Assert.Equal(1, resumed.CompletedCount);
                resumed.Save(CreateMatch(gameId: 1, seed: 42));
            }

            var records = File.ReadAllLines(journalPath)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("GameId").GetInt64())
                .ToArray();
            Assert.Equal(new long[] { 0, 1 }, records);
            Assert.Equal(2, JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "checkpoint.json")))
                .RootElement.GetProperty("CompletedGames").GetInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonlStoreRejectsAResumeWithDifferentRunSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            using (var store = new JsonlSelfPlayStore(directory, baseSeed: 41, targetScore: 1_000))
            {
                store.Save(CreateMatch(gameId: 0, seed: 41));
            }

            Assert.Throws<InvalidOperationException>(
                () => new JsonlSelfPlayStore(directory, baseSeed: 42, targetScore: 1_000));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FeatureEncoderCapturesWishRankAgainstAHighFullHouseAndRetainedCopies()
    {
        var jackCards = new[]
        {
            new Card(CardRank.Jack, CardSuit.Jade),
            new Card(CardRank.Jack, CardSuit.Sword),
            new Card(CardRank.Jack, CardSuit.Pagoda)
        };
        var queens = new[]
        {
            new Card(CardRank.Queen, CardSuit.Jade),
            new Card(CardRank.Queen, CardSuit.Sword)
        };
        var mahJong = new Card(CardRank.MahJong);
        var hand = jackCards.Concat(queens).Append(mahJong).ToArray();
        var snapshot = CreateSnapshot();
        var combination = CardCombination.ClassifyAll(new[] { mahJong }).Single();
        var move = new LegalMove(combination, CardRank.Queen);
        var roundContext = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(
                new Card(CardRank.Two, CardSuit.Jade),
                new Card(CardRank.Four, CardSuit.Star),
                new Card(CardRank.Queen, CardSuit.Pagoda)));

        var features = MoveFeatureEncoder.Encode(PlayerSeat.Bottom, hand, snapshot, move, roundContext);

        Assert.Equal(MoveFeatureEncoder.FeatureCount, features.Length);
        Assert.Equal(MoveFeatureEncoder.FeatureCount, MoveFeatureEncoder.FeatureNames.Count);
        Assert.Equal(1.0, features[14]); // A Jack full house remains available after the Mah Jong lead.
        Assert.Equal(2.0 / 3.0, features[18]); // Two Queens remain to answer the wish.
        Assert.Equal(1.0, features[19]);
        Assert.Equal(1.0, features[MoveFeatureEncoder.WishRankInteractionStart + (int)CardRank.Queen - (int)CardRank.Two]);
        Assert.Equal(
            1.0,
            features[MoveFeatureEncoder.WishMatchesPassToLeftStart + (int)CardRank.Queen - (int)CardRank.Two]);

        var noWishFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom,
            hand,
            snapshot,
            new LegalMove(combination));
        Assert.All(noWishFeatures.Skip(MoveFeatureEncoder.WishRankInteractionStart), value => Assert.Equal(0.0, value));
    }

    [Fact]
    public void LinearPolicyUpdatesItsWeightsFromACompletedEpisode()
    {
        var hand = new[]
        {
            new Card(CardRank.Two, CardSuit.Jade),
            new Card(CardRank.Three, CardSuit.Sword)
        };
        var policy = new LinearPreferencePolicy();
        policy.BeginEpisode(seed: 4_289);

        var chosenMove = policy.ChooseMove(PlayerSeat.Bottom, hand, CreateSnapshot());
        Assert.All(chosenMove.Combination!.Cards, card => Assert.Contains(card, hand));
        policy.CompleteEpisode(reward: 1.0, learningRate: 0.02);

        Assert.True(policy.LastEpisodeDecisionCount > 0);
        Assert.Contains(policy.Weights, weight => weight != 0.0);
        Assert.True(policy.RewardBaseline > 0.0);
    }

    [Fact]
    public void PassFeaturesDistinguishRecipientsAndTrackBrokenHandStructure()
    {
        var hand = new[]
        {
            new Card(CardRank.Two, CardSuit.Jade),
            new Card(CardRank.Three, CardSuit.Jade),
            new Card(CardRank.Four, CardSuit.Jade),
            new Card(CardRank.Five, CardSuit.Jade),
            new Card(CardRank.Five, CardSuit.Sword),
            new Card(CardRank.Six, CardSuit.Pagoda),
            new Card(CardRank.Seven, CardSuit.Star),
            new Card(CardRank.Eight, CardSuit.Jade),
            new Card(CardRank.Nine, CardSuit.Sword),
            new Card(CardRank.Ten, CardSuit.Pagoda),
            new Card(CardRank.Jack, CardSuit.Star),
            new Card(CardRank.Queen, CardSuit.Jade),
            new Card(CardRank.Dragon),
            new Card(CardRank.Dog)
        };
        var passedCard = hand[1];
        var partnerFeatures = PassFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, passedCard, PlayerSeat.Top);
        var rightFeatures = PassFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, passedCard, PlayerSeat.Right);
        var featureNames = PassFeatureEncoder.FeatureNames;
        int Feature(string name) => featureNames.ToList().IndexOf(name);

        Assert.Equal(PassFeatureEncoder.FeatureCount, partnerFeatures.Length);
        Assert.Equal(PassFeatureEncoder.FeatureCount, featureNames.Count);
        Assert.Equal(1.0, partnerFeatures[Feature("CardRank_Three_ToPartner")]);
        Assert.Equal(1.0, rightFeatures[Feature("CardRank_Three_ToRight")]);
        Assert.Equal(0.0, partnerFeatures[Feature("CardRank_Three_ToRight")]);
        Assert.True(
            partnerFeatures[Feature("SameSuitAdjacentPairsAfterPass")] <
            partnerFeatures[Feature("SameSuitAdjacentPairsBeforePass")]);
        Assert.Throws<ArgumentException>(() => PassFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, passedCard, PlayerSeat.Bottom));
    }

    [Fact]
    public void LinearPolicyLearnsThreeDistinctLegalPasses()
    {
        var hand = TichuDeck.CreateStandard().Take(14).ToArray();
        var policy = new LinearPreferencePolicy();
        policy.BeginEpisode(seed: 84_172);

        var selection = policy.ChoosePass(PlayerSeat.Bottom, hand);

        var passedCards = new[] { selection.ToRight, selection.ToPartner, selection.ToLeft };
        Assert.Equal(3, passedCards.Distinct().Count());
        Assert.All(passedCards, card => Assert.Contains(card, hand));
        policy.CompleteEpisode(reward: 1.0, learningRate: 0.02);

        Assert.Equal(3, policy.LastEpisodeDecisionCount);
        Assert.Contains(policy.PassWeights, weight => weight != 0.0);
        Assert.All(policy.Weights, weight => Assert.Equal(0.0, weight));
        Assert.All(policy.DeclarationWeights, weight => Assert.Equal(0.0, weight));
    }

    [Fact]
    public void LinearPolicyLearnsGrandAndTichuCallsFromTheAvailableHand()
    {
        var grandStraight = new[]
        {
            new Card(CardRank.MahJong),
            new Card(CardRank.Two, CardSuit.Jade),
            new Card(CardRank.Three, CardSuit.Sword),
            new Card(CardRank.Four, CardSuit.Pagoda),
            new Card(CardRank.Five, CardSuit.Star),
            new Card(CardRank.Six, CardSuit.Jade),
            new Card(CardRank.Seven, CardSuit.Sword),
            new Card(CardRank.Eight, CardSuit.Pagoda)
        };
        var threeAces = CreateThreeAceTichuHand();

        Assert.False(new RandomBaselinePolicy(new Random(0)).ShouldCallGrand(grandStraight));
        Assert.False(new RandomBaselinePolicy(new Random(0)).ShouldCallTichu(threeAces));

        var handDrivenWeights = new double[DeclarationFeatureEncoder.FeatureCount];
        handDrivenWeights[DeclarationFeatureEncoder.GrandStraightPotentialIndex] = 100.0;
        handDrivenWeights[DeclarationFeatureEncoder.TichuAceCountIndex] = 100.0;
        var handDrivenPolicy = new LinearPreferencePolicy(declarationWeights: handDrivenWeights);
        handDrivenPolicy.BeginEpisode(seed: 17);
        Assert.True(handDrivenPolicy.ShouldCallGrand(grandStraight));
        Assert.True(handDrivenPolicy.ShouldCallTichu(threeAces));

        var learningPolicy = new LinearPreferencePolicy();
        learningPolicy.BeginEpisode(seed: 17);
        _ = learningPolicy.ShouldCallGrand(grandStraight);
        _ = learningPolicy.ShouldCallTichu(threeAces);
        learningPolicy.CompleteEpisode(reward: 1.0, learningRate: 0.02);

        Assert.Equal(2, learningPolicy.LastEpisodeDecisionCount);
        Assert.Contains(learningPolicy.DeclarationWeights, weight => weight != 0.0);
        Assert.All(learningPolicy.Weights, weight => Assert.Equal(0.0, weight));
    }

    [Fact]
    public void LinearPolicyKeepsExploratoryTichuCallsRareAcrossRepeatedOpportunities()
    {
        var weakHand = new[]
        {
            new Card(CardRank.Two, CardSuit.Jade),
            new Card(CardRank.Three, CardSuit.Sword),
            new Card(CardRank.Four, CardSuit.Pagoda),
            new Card(CardRank.Five, CardSuit.Star),
            new Card(CardRank.Six, CardSuit.Jade),
            new Card(CardRank.Seven, CardSuit.Sword),
            new Card(CardRank.Eight, CardSuit.Pagoda),
            new Card(CardRank.Nine, CardSuit.Star),
            new Card(CardRank.Ten, CardSuit.Jade),
            new Card(CardRank.Jack, CardSuit.Sword),
            new Card(CardRank.Queen, CardSuit.Pagoda),
            new Card(CardRank.King, CardSuit.Star),
            new Card(CardRank.Dog),
            new Card(CardRank.MahJong)
        };
        var phase = new GrandDecisionPhase(
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            TimeSpan.FromMinutes(2));
        var context = new PolicyDeclarationContext(PlayerSeat.Bottom, phase.AdvanceTo(phase.StartedAt));
        var exploratoryCalls = 0;

        for (var seed = 0; seed < 200; seed++)
        {
            var policy = new LinearPreferencePolicy(recordEpisode: false);
            policy.BeginEpisode(seed);
            for (var opportunity = 0; opportunity < 20; opportunity++)
            {
                exploratoryCalls += policy.ShouldCallTichu(weakHand, context) ? 1 : 0;
            }
        }

        Assert.True(exploratoryCalls < 30, $"Observed {exploratoryCalls} exploratory calls across 4,000 opportunities.");
    }

    [Fact]
    public void DeclarationFeaturesIncludeEarlierGrandAndTichuCallsAndOpenOptions()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var phase = new GrandDecisionPhase(start, TimeSpan.FromMinutes(2));
        var partnerGrand = phase.TryCallGrand(PlayerSeat.Top, start.AddSeconds(1), confirmPartnerCall: true);
        Assert.True(partnerGrand.Accepted);
        Assert.True(phase.TryDeclineGrand(PlayerSeat.Right, start.AddSeconds(2)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Right, start.AddSeconds(3)).Accepted);
        Assert.True(phase.TryCallTichu(PlayerSeat.Right, start.AddSeconds(4)).Accepted);
        Assert.True(phase.TryDeclineGrand(PlayerSeat.Bottom, start.AddSeconds(5)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Bottom, start.AddSeconds(6)).Accepted);

        var grandContext = new PolicyDeclarationContext(
            PlayerSeat.Bottom,
            partnerGrand.Snapshot);
        var grandFeatures = DeclarationFeatureEncoder.Encode(
            new[] { new Card(CardRank.Two, CardSuit.Jade) },
            isGrandDecision: true,
            grandContext);
        Assert.Equal(1.0, grandFeatures[DeclarationFeatureEncoder.GrandPartnerCalledIndex]);
        Assert.Equal(1.0, grandFeatures[DeclarationFeatureEncoder.GrandOpponentUndecidedCountIndex]);

        var context = new PolicyDeclarationContext(PlayerSeat.Bottom, phase.AdvanceTo(start.AddSeconds(6)));
        var features = DeclarationFeatureEncoder.Encode(
            new[] { new Card(CardRank.Two, CardSuit.Jade) },
            isGrandDecision: false,
            context);

        Assert.Equal(1.0, features[DeclarationFeatureEncoder.GrandPartnerCalledIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.GrandOpponentCallCountIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.PartnerTichuCalledIndex]);
        Assert.Equal(0.5, features[DeclarationFeatureEncoder.OpponentTichuCallCountIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.GrandPartnerUndecidedIndex]);
        Assert.Equal(0.5, features[DeclarationFeatureEncoder.GrandOpponentUndecidedCountIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.PartnerTichuAvailableIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.OpponentTichuAvailableCountIndex]);

        var conditionedWeights = new double[DeclarationFeatureEncoder.FeatureCount];
        conditionedWeights[DeclarationFeatureEncoder.TichuCallBiasIndex] = -100.0;
        conditionedWeights[DeclarationFeatureEncoder.GrandPartnerCalledIndex] = 200.0;
        var conditionedPolicy = new LinearPreferencePolicy(declarationWeights: conditionedWeights);
        conditionedPolicy.BeginEpisode(seed: 91);
        Assert.True(conditionedPolicy.ShouldCallTichu(CreateThreeAceTichuHand(), context));

        var emptyPhase = new GrandDecisionPhase(start, TimeSpan.FromMinutes(2));
        var noEarlierCall = new PolicyDeclarationContext(PlayerSeat.Bottom, emptyPhase.AdvanceTo(start));
        var unconditionedPolicy = new LinearPreferencePolicy(declarationWeights: conditionedWeights);
        unconditionedPolicy.BeginEpisode(seed: 91);
        Assert.False(unconditionedPolicy.ShouldCallTichu(CreateThreeAceTichuHand(), noEarlierCall));
    }

    [Fact]
    public void TichuDeclarationFeaturesReflectAVisibleWishAndCurrentPlay()
    {
        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var phase = new GrandDecisionPhase(start, TimeSpan.FromMinutes(2));
        var topCard = new Card(CardRank.Eight, CardSuit.Jade);
        var topCombination = CardCombination.ClassifyAll(new[] { topCard }).Single();
        var playSnapshot = new RoundSnapshot(
            new TrickSnapshot(
                PlayerSeat.Right,
                topCombination,
                PlayerSeat.Top,
                CardRank.Queen,
                new[] { new TrickPlay(PlayerSeat.Top, topCombination) },
                Enum.GetValues<PlayerSeat>(),
                Array.Empty<PlayerSeat>(),
                Array.Empty<PlayerSeat>(),
                CanCollect: false,
                IsDoubleVictory: false,
                IsRoundEnded: false),
            new[] { 14, 13, 14, 14 },
            new[] { 0, 0, 0, 0 });
        var hand = new[]
        {
            new Card(CardRank.Queen, CardSuit.Jade),
            new Card(CardRank.Queen, CardSuit.Sword),
            new Card(CardRank.Two, CardSuit.Jade)
        };
        var context = new PolicyDeclarationContext(
            PlayerSeat.Bottom,
            phase.AdvanceTo(start),
            playSnapshot);

        var features = DeclarationFeatureEncoder.Encode(hand, isGrandDecision: false, context);

        Assert.Equal(DeclarationFeatureEncoder.FeatureCount, features.Length);
        Assert.Equal(DeclarationFeatureEncoder.FeatureCount, DeclarationFeatureEncoder.FeatureNames.Count);
        Assert.Equal(1.0, features[DeclarationFeatureEncoder.TichuAnyCardPlayedIndex]);
        Assert.Equal(1.0, features[DeclarationFeatureEncoder.TichuPendingWishIndex]);
        Assert.Equal(
            ((int)CardRank.Queen - (int)CardRank.Two) / (double)((int)CardRank.Ace - (int)CardRank.Two),
            features[DeclarationFeatureEncoder.TichuPendingWishRankIndex]);
        Assert.Equal(0.5, features[DeclarationFeatureEncoder.TichuWishCardCountIndex]);
        Assert.Equal(0.25, features[DeclarationFeatureEncoder.TichuCurrentTrickPlayCountIndex]);
        Assert.Equal(1.0, features[DeclarationFeatureEncoder.TichuCurrentTrickLedByPartnerIndex]);

        var wishConditionedWeights = new double[DeclarationFeatureEncoder.FeatureCount];
        wishConditionedWeights[DeclarationFeatureEncoder.TichuCallBiasIndex] = -100.0;
        wishConditionedWeights[DeclarationFeatureEncoder.TichuPendingWishIndex] = 200.0;
        var wishConditionedPolicy = new LinearPreferencePolicy(declarationWeights: wishConditionedWeights);
        wishConditionedPolicy.BeginEpisode(seed: 45);
        Assert.True(wishConditionedPolicy.ShouldCallTichu(hand, context));

        var noWishContext = context with
        {
            PlaySnapshot = playSnapshot with
            {
                Trick = playSnapshot.Trick with { PendingWish = null }
            }
        };
        var noWishPolicy = new LinearPreferencePolicy(declarationWeights: wishConditionedWeights);
        noWishPolicy.BeginEpisode(seed: 45);
        Assert.False(noWishPolicy.ShouldCallTichu(hand, noWishContext));
    }

    [Fact]
    public void SimulatorCallsTichuFromThePostPassHand()
    {
        const int matchSeed = 9_731;
        var deal = TichuDealer.CreateShuffled(matchSeed);
        var sentToPartner = deal.FourteenCardHands[PlayerSeat.Bottom]
            .First(card => card.Rank is >= CardRank.Two and <= CardRank.Ace);
        var tichuSawReceivedCard = false;
        var tichuSawPrePlayState = false;
        var match = new RandomSelfPlaySimulator(targetScore: 50).PlayMatch(
            gameId: 0,
            matchSeed,
            policyFactory: seat =>
            {
                var policySeed = unchecked(matchSeed + ((int)seat + 1) * 104_729);
                Func<IReadOnlyList<Card>, CardPassSelection>? pass = seat == PlayerSeat.Bottom
                    ? hand =>
                    {
                        var otherCards = hand.Where(card => card != sentToPartner).Take(2).ToArray();
                        return new CardPassSelection(otherCards[0], sentToPartner, otherCards[1]);
                    }
                    : null;
                Func<IReadOnlyList<Card>, PolicyDeclarationContext, bool>? callTichu = seat == PlayerSeat.Top
                    ? (hand, context) =>
                    {
                        tichuSawReceivedCard = hand.Contains(sentToPartner);
                        tichuSawPrePlayState = context.PlaySnapshot is { Trick.Plays.Count: 0 };
                        return tichuSawReceivedCard;
                    }
                    : null;
                var policy = new ScriptedTichuPolicy(
                    seat,
                    new RandomBaselinePolicy(new Random(policySeed)),
                    callTichu,
                    pass);
                return policy;
            });

        Assert.Contains(match.Rounds[0].Passes, pass =>
            pass.Seat == PlayerSeat.Bottom && pass.ToPartner == sentToPartner);
        Assert.Contains(match.Rounds[0].Calls, call =>
            call.Seat == PlayerSeat.Top && call.Call == TichuCall.Small);
        Assert.True(tichuSawReceivedCard);
        Assert.True(tichuSawPrePlayState);
        var topCall = Assert.Single(match.Rounds[0].Calls, call =>
            call.Seat == PlayerSeat.Top && call.Call == TichuCall.Small);
        Assert.Equal(0, topCall.BeforeActionIndex);
        Assert.Contains(sentToPartner, topCall.HandBeforeCall!);
        Assert.Null(topCall.PendingWish);
    }

    [Fact]
    public void SimulatorAllowsWaitingUntilTheMahJongWishBeforeCallingTichu()
    {
        const int matchSeed = 6_142;
        var deal = TichuDealer.CreateShuffled(matchSeed);
        var expectedPasses = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => new RandomBaselinePolicy(new Random(unchecked(matchSeed + ((int)seat + 1) * 104_729)))
                .ChoosePass(deal.FourteenCardHands[seat]));
        var openingSeat = TichuRoundSetup.Prepare(deal, expectedPasses).OpeningSeat;
        var tichuSeat = openingSeat.ToTheRight();
        var sawWishBeforeCall = false;
        var playsOnWishAtCall = 0;
        var ownHandStillUnplayedAtCall = false;
        var match = new RandomSelfPlaySimulator(targetScore: 50).PlayMatch(
            gameId: 0,
            matchSeed,
            policyFactory: seat =>
            {
                var policySeed = unchecked(matchSeed + ((int)seat + 1) * 104_729);
                Func<IReadOnlyList<Card>, PolicyDeclarationContext, bool>? callTichu = seat == tichuSeat
                    ? (_, context) =>
                    {
                        if (context.PlaySnapshot?.Trick.PendingWish != CardRank.Queen)
                        {
                            return false;
                        }

                        sawWishBeforeCall = true;
                        playsOnWishAtCall = context.PlaySnapshot.Trick.Plays.Count;
                        ownHandStillUnplayedAtCall =
                            context.PlaySnapshot.HandSizes[(int)tichuSeat] == 14;
                        return true;
                    }
                    : null;
                return new ScriptedTichuPolicy(
                    seat,
                    new RandomBaselinePolicy(new Random(policySeed)),
                    callTichu,
                    wishOnMahJongLead: seat == openingSeat ? CardRank.Queen : null);
            });

        var delayedCall = Assert.Single(match.Rounds[0].Calls, call =>
            call.Seat == tichuSeat && call.Call == TichuCall.Small);
        Assert.True(sawWishBeforeCall);
        Assert.True(playsOnWishAtCall > 0);
        Assert.True(ownHandStillUnplayedAtCall);
        Assert.Equal(1, delayedCall.BeforeActionIndex);
        Assert.Equal(CardRank.Queen, delayedCall.PendingWish);
        Assert.Equal(14, delayedCall.HandBeforeCall!.Count);

        var jsonOptions = new JsonSerializerOptions();
        jsonOptions.Converters.Add(new CompactCardJsonConverter());
        var serializedRound = JsonSerializer.Serialize(match.Rounds[0], jsonOptions);
        var decodedRound = JsonSerializer.Deserialize<SelfPlayRoundRecord>(serializedRound, jsonOptions);
        Assert.NotNull(decodedRound);
        var decodedCall = Assert.Single(decodedRound.Calls, call =>
            call.Seat == tichuSeat && call.Call == TichuCall.Small);
        Assert.Equal(delayedCall.BeforeActionIndex, decodedCall.BeforeActionIndex);
        Assert.Equal(delayedCall.PendingWish, decodedCall.PendingWish);
        Assert.Equal(delayedCall.HandBeforeCall, decodedCall.HandBeforeCall);
    }

    [Fact]
    public void SimulatorAllowsPostPassTichuAfterPartnerCallsGrand()
    {
        var policies = new Dictionary<PlayerSeat, DelayedPartnerGrandTichuPolicy>();
        var matchSeed = 8_217;
        var match = new RandomSelfPlaySimulator(targetScore: 50).PlayMatch(
            gameId: 0,
            matchSeed,
            policyFactory: seat =>
            {
                var policySeed = unchecked(matchSeed + ((int)seat + 1) * 104_729);
                var policy = new DelayedPartnerGrandTichuPolicy(
                    seat,
                    new RandomBaselinePolicy(new Random(policySeed)));
                policies.Add(seat, policy);
                return policy;
            });

        Assert.Contains(match.Rounds[0].Calls, call =>
            call.Seat == PlayerSeat.Top && call.Call == TichuCall.Grand);
        Assert.Contains(match.Rounds[0].Calls, call =>
            call.Seat == PlayerSeat.Bottom && call.Call == TichuCall.Small);
        Assert.True(policies[PlayerSeat.Bottom].SawPartnerGrandWhenCallingTichu);
    }

    [Fact]
    public void LinearPolicyCanChooseToDeclineAnAvailableOutOfTurnBomb()
    {
        var hand = new[]
        {
            new Card(CardRank.Nine, CardSuit.Jade),
            new Card(CardRank.Nine, CardSuit.Sword),
            new Card(CardRank.Nine, CardSuit.Pagoda),
            new Card(CardRank.Nine, CardSuit.Star)
        };
        var bomb = CardCombination.ClassifyAll(hand)
            .Single(combination => combination.Type == CombinationType.FourOfAKindBomb);
        var weights = new double[MoveFeatureEncoder.FeatureCount];
        weights[1] = 1_000.0; // Prefer the pass-shaped decline choice.
        var policy = new LinearPreferencePolicy(weights);
        policy.BeginEpisode(seed: 314);

        var chosen = policy.ChooseOutOfTurnBomb(
            PlayerSeat.Bottom,
            hand,
            CreateSnapshot(),
            new[] { new LegalMove(bomb, IsOutOfTurnBomb: true) });

        Assert.Null(chosen);
        policy.CompleteEpisode(reward: 1.0, learningRate: 0.02);
        Assert.Equal(1, policy.LastEpisodeDecisionCount);
    }

    [Fact]
    public void LinearPolicyTrainerSavesEveryMatchAndResumesFromItsCheckpoint()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var trainer = new LinearPolicyTrainer();
            var firstRun = trainer.Train(
                totalGames: 1,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory);

            Assert.Equal(1, firstRun.CompletedGames);
            Assert.True(File.Exists(Path.Combine(directory, "policy-checkpoint.json")));
            Assert.Equal(MoveFeatureEncoder.FeatureCount, firstRun.Weights.Length);
            Assert.Equal(DeclarationFeatureEncoder.FeatureCount, firstRun.DeclarationWeights.Length);
            Assert.Equal(PassFeatureEncoder.FeatureCount, firstRun.PassWeights.Length);

            var checkpointPath = Path.Combine(directory, "policy-checkpoint.json");
            // Migrate the previous 31-feature declaration model with public call state.
            var previousPublicStateJson = JsonNode.Parse(File.ReadAllText(checkpointPath))!.AsObject();
            previousPublicStateJson["Configuration"]![nameof(PolicyTrainingConfiguration.DeclarationFeatureCount)] = 31;
            var previousPublicStateWeights = previousPublicStateJson[nameof(LinearPolicyTrainingCheckpoint.DeclarationWeights)]!
                .AsArray()
                .Take(31)
                .Select(weight => weight!.GetValue<double>())
                .ToArray();
            previousPublicStateJson[nameof(LinearPolicyTrainingCheckpoint.DeclarationWeights)] =
                JsonSerializer.SerializeToNode(previousPublicStateWeights);
            File.WriteAllText(
                checkpointPath,
                previousPublicStateJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var resumedFromPublicState = trainer.Train(
                totalGames: 2,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory);
            Assert.Equal(2, resumedFromPublicState.CompletedGames);
            Assert.Equal(DeclarationFeatureEncoder.FeatureCount, resumedFromPublicState.DeclarationWeights.Length);

            // Also migrate the earlier hand-only declaration model's 23 weights.
            var previousDeclarationJson = JsonNode.Parse(File.ReadAllText(checkpointPath))!.AsObject();
            previousDeclarationJson["Configuration"]![nameof(PolicyTrainingConfiguration.DeclarationFeatureCount)] = 23;
            var previousDeclarationWeights = previousDeclarationJson[nameof(LinearPolicyTrainingCheckpoint.DeclarationWeights)]!
                .AsArray()
                .Take(23)
                .Select(weight => weight!.GetValue<double>())
                .ToArray();
            previousDeclarationJson[nameof(LinearPolicyTrainingCheckpoint.DeclarationWeights)] =
                JsonSerializer.SerializeToNode(previousDeclarationWeights);
            File.WriteAllText(
                checkpointPath,
                previousDeclarationJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var resumedRun = trainer.Train(
                totalGames: 3,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory);

            Assert.Equal(3, resumedRun.CompletedGames);
            Assert.Equal(DeclarationFeatureEncoder.FeatureCount, resumedRun.DeclarationWeights.Length);
            Assert.True(resumedRun.Wins >= resumedFromPublicState.Wins);
            Assert.True(resumedRun.Losses >= resumedFromPublicState.Losses);
            Assert.True(resumedRun.Draws >= resumedFromPublicState.Draws);
            Assert.True(resumedRun.Wins + resumedRun.Losses + resumedRun.Draws == 3);

            // Also retain compatibility with checkpoints predating declaration learning.
            var oldCheckpointJson = JsonNode.Parse(File.ReadAllText(checkpointPath))!.AsObject();
            oldCheckpointJson.Remove(nameof(LinearPolicyTrainingCheckpoint.DeclarationWeights));
            oldCheckpointJson.Remove(nameof(LinearPolicyTrainingCheckpoint.PassWeights));
            oldCheckpointJson["Configuration"]!.AsObject()
                .Remove(nameof(PolicyTrainingConfiguration.DeclarationFeatureCount));
            oldCheckpointJson["Configuration"]!.AsObject()
                .Remove(nameof(PolicyTrainingConfiguration.PassFeatureCount));
            File.WriteAllText(checkpointPath, oldCheckpointJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

            var resumedFromOriginal = trainer.Train(
                totalGames: 4,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory);
            Assert.Equal(4, resumedFromOriginal.CompletedGames);
            Assert.Equal(DeclarationFeatureEncoder.FeatureCount, resumedFromOriginal.DeclarationWeights.Length);
            Assert.Equal(PassFeatureEncoder.FeatureCount, resumedFromOriginal.PassWeights.Length);

            var evaluation = new LinearPolicyEvaluator().Evaluate(
                totalGames: 2,
                baseSeed: 93_817,
                targetScore: 50,
                decisionSeconds: 120,
                weights: resumedFromOriginal.Weights,
                declarationWeights: resumedFromOriginal.DeclarationWeights,
                passWeights: resumedFromOriginal.PassWeights);
            Assert.Equal(2, evaluation.CompletedGames);
            Assert.Equal(2, evaluation.Wins + evaluation.Losses + evaluation.Draws);

            Assert.Throws<InvalidOperationException>(() => trainer.Train(
                totalGames: 4,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory,
                learningRate: 0.03));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FrozenPolicySeedsTheChallengerAndRemainsTheFixedOpponent()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var frozen = new LinearPolicySnapshot(
                new double[MoveFeatureEncoder.FeatureCount],
                new double[DeclarationFeatureEncoder.FeatureCount],
                new double[PassFeatureEncoder.FeatureCount]);
            var frozenWeights = frozen.Weights.ToArray();
            var store = new PolicyTrainingStore(
                directory,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                learningRate: LinearPolicyTrainer.DefaultLearningRate,
                opponentFingerprint: frozen.Fingerprint);

            var initialCheckpoint = store.LoadOrCreate(frozen);
            Assert.Equal(0, initialCheckpoint.CompletedGames);
            Assert.Equal(frozen.Weights, initialCheckpoint.Weights);
            Assert.Equal(frozen.DeclarationWeights, initialCheckpoint.DeclarationWeights);
            Assert.Equal(frozen.PassWeights, initialCheckpoint.PassWeights);
            Assert.Equal(frozen.Fingerprint, initialCheckpoint.Configuration.OpponentFingerprint);

            var trained = new LinearPolicyTrainer().Train(
                totalGames: 1,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                outputDirectory: directory,
                frozenOpponent: frozen);

            Assert.Equal(1, trained.CompletedGames);
            Assert.Equal(frozen.Fingerprint, trained.Configuration.OpponentFingerprint);
            Assert.Equal(frozenWeights, frozen.Weights);
            Assert.Throws<InvalidOperationException>(() => new PolicyTrainingStore(
                directory,
                baseSeed: 73,
                targetScore: 50,
                decisionSeconds: 120,
                learningRate: LinearPolicyTrainer.DefaultLearningRate,
                opponentFingerprint: "different-frozen-opponent").LoadOrCreate());

            var evaluation = new LinearPolicyEvaluator().Evaluate(
                totalGames: 2,
                baseSeed: 93_817,
                targetScore: 50,
                decisionSeconds: 120,
                weights: trained.Weights,
                declarationWeights: trained.DeclarationWeights,
                passWeights: trained.PassWeights,
                frozenOpponent: frozen);
            Assert.Equal(2, evaluation.CompletedGames);
            Assert.Equal(2, evaluation.Wins + evaluation.Losses + evaluation.Draws);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "tichu-selfplay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static Card[] CreateThreeAceTichuHand()
    {
        var fullDeck = TichuDeck.CreateStandard();
        return fullDeck.Where(card => card.Rank == CardRank.Ace).Take(3)
            .Concat(fullDeck.Where(card => card.Rank is >= CardRank.Two and <= CardRank.Ten).Take(11))
            .ToArray();
    }

    private static SelfPlayMatchRecord CreateMatch(long gameId, int seed) => new(
        RandomSelfPlaySimulator.RecordSchemaVersion,
        gameId,
        seed,
        1_000,
        1_000,
        0,
        SelfPlayWinner.BottomTop,
        Array.Empty<SelfPlayRoundRecord>());

    private static Func<PlayerSeat, ITichuPolicy> CreateTracingPolicyFactory(
        int matchSeed,
        IDictionary<PlayerSeat, List<string>> traces) => seat =>
    {
        var trace = new List<string>();
        traces.Add(seat, trace);
        var policySeed = unchecked(matchSeed + ((int)seat + 1) * 104_729);
        return new TracingPolicy(new RandomBaselinePolicy(new Random(policySeed)), trace);
    };

    private sealed class TracingPolicy(RandomBaselinePolicy baseline, List<string> trace) : ITichuPolicy
    {
        public void BeginRound(PolicyRoundContext context)
        {
            baseline.BeginRound(context);
            trace.Add($"round:{context.Seat}:{FormatCards(new[] { context.PassSelection.ToRight, context.PassSelection.ToPartner, context.PassSelection.ToLeft })}");
        }

        public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
        {
            var result = baseline.ShouldCallGrand(hand, context);
            trace.Add($"grand:{FormatCards(hand)}:{result}:{FormatDeclarationContext(context)}");
            return result;
        }

        public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
        {
            var result = baseline.ShouldCallTichu(hand, context);
            trace.Add($"tichu:{FormatCards(hand)}:{result}:{FormatDeclarationContext(context)}");
            return result;
        }

        public CardPassSelection ChoosePass(IReadOnlyList<Card> hand)
        {
            var result = baseline.ChoosePass(hand);
            trace.Add($"pass:{FormatCards(hand)}:{FormatCards(new[] { result.ToRight, result.ToPartner, result.ToLeft })}");
            return result;
        }

        public LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
        {
            var result = baseline.ChooseMove(seat, hand, snapshot);
            trace.Add($"move:{seat}:{FormatCards(hand)}:{snapshot.Trick.PendingWish}:{FormatMove(result)}");
            return result;
        }

        public LegalMove? ChooseOutOfTurnBomb(
            PlayerSeat seat,
            IReadOnlyList<Card> hand,
            RoundSnapshot snapshot,
            IReadOnlyList<LegalMove> bombs)
        {
            var result = baseline.ChooseOutOfTurnBomb(seat, hand, snapshot, bombs);
            trace.Add($"bomb:{seat}:{FormatCards(hand)}:{FormatMoves(bombs)}:{FormatMove(result)}");
            return result;
        }

        public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner)
        {
            var result = baseline.ChooseDragonRecipient(dragonWinner);
            trace.Add($"dragon:{dragonWinner}:{result}");
            return result;
        }

        private static string FormatMove(LegalMove? move) => move is null
            ? "none"
            : $"{move.IsPass}:{move.IsOutOfTurnBomb}:{FormatCards(move.Combination?.Cards ?? Array.Empty<Card>())}:{move.WishForMahJong}";

        private static string FormatMoves(IEnumerable<LegalMove> moves) =>
            string.Join(";", moves.Select(FormatMove));

        private static string FormatDeclarationContext(PolicyDeclarationContext context) =>
            string.Join(";", context.Snapshot.Seats.Select(decision =>
                $"{decision.Seat}:{decision.Status}:{decision.TichuStatus}"));

        private static string FormatCards(IEnumerable<Card> cards) =>
            string.Join(",", cards.Select(card => $"{(int)card.Rank}:{(card.Suit is { } suit ? (int)suit : -1)}"));
    }

    private sealed class ScriptedTichuPolicy(
        PlayerSeat seat,
        RandomBaselinePolicy baseline,
        Func<IReadOnlyList<Card>, PolicyDeclarationContext, bool>? callTichu = null,
        Func<IReadOnlyList<Card>, CardPassSelection>? choosePass = null,
        CardRank? wishOnMahJongLead = null) : ITichuPolicy
    {
        public void BeginRound(PolicyRoundContext context) => baseline.BeginRound(context);

        public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context) => false;

        public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context) =>
            callTichu?.Invoke(hand, context) ?? false;

        public CardPassSelection ChoosePass(IReadOnlyList<Card> hand) =>
            choosePass?.Invoke(hand) ?? baseline.ChoosePass(hand);

        public LegalMove ChooseMove(PlayerSeat player, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
        {
            if (wishOnMahJongLead is { } wish &&
                snapshot.Trick.CurrentTurnSeat == seat &&
                snapshot.Trick.Plays.Count == 0 &&
                hand.Any(card => card.Rank == CardRank.MahJong))
            {
                var mahJong = hand.Single(card => card.Rank == CardRank.MahJong);
                var mahJongSingle = CardCombination.ClassifyAll(new[] { mahJong }).Single();
                return new LegalMove(mahJongSingle, wish);
            }

            return baseline.ChooseMove(player, hand, snapshot);
        }

        public LegalMove? ChooseOutOfTurnBomb(
            PlayerSeat player,
            IReadOnlyList<Card> hand,
            RoundSnapshot snapshot,
            IReadOnlyList<LegalMove> bombs) =>
            baseline.ChooseOutOfTurnBomb(player, hand, snapshot, bombs);

        public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner) =>
            baseline.ChooseDragonRecipient(dragonWinner);
    }

    private sealed class DelayedPartnerGrandTichuPolicy(
        PlayerSeat seat,
        RandomBaselinePolicy baseline) : ITichuPolicy
    {
        public bool SawPartnerGrandWhenCallingTichu { get; private set; }

        public void BeginRound(PolicyRoundContext context) => baseline.BeginRound(context);

        public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context) =>
            seat == PlayerSeat.Top;

        public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
        {
            if (seat != PlayerSeat.Bottom ||
                context.Snapshot.For(PlayerSeat.Top).Status != GrandDecisionStatus.Called)
            {
                return false;
            }

            SawPartnerGrandWhenCallingTichu = true;
            return true;
        }

        public CardPassSelection ChoosePass(IReadOnlyList<Card> hand) => baseline.ChoosePass(hand);

        public LegalMove ChooseMove(PlayerSeat player, IReadOnlyList<Card> hand, RoundSnapshot snapshot) =>
            baseline.ChooseMove(player, hand, snapshot);

        public LegalMove? ChooseOutOfTurnBomb(
            PlayerSeat player,
            IReadOnlyList<Card> hand,
            RoundSnapshot snapshot,
            IReadOnlyList<LegalMove> bombs) =>
            baseline.ChooseOutOfTurnBomb(player, hand, snapshot, bombs);

        public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner) =>
            baseline.ChooseDragonRecipient(dragonWinner);
    }

    private static RoundSnapshot CreateSnapshot() => new(
        new TrickSnapshot(
            PlayerSeat.Bottom,
            null,
            null,
            null,
            Array.Empty<TrickPlay>(),
            Enum.GetValues<PlayerSeat>(),
            Array.Empty<PlayerSeat>(),
            Array.Empty<PlayerSeat>(),
            CanCollect: false,
            IsDoubleVictory: false,
            IsRoundEnded: false),
        new[] { 6, 14, 14, 14 },
        new[] { 0, 0, 0, 0 });
}
