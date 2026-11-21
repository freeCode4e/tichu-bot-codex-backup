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
    public void PerspectiveReplayShowsPrivateSignalsAndHidesOpponentHands()
    {
        var match = new RandomSelfPlaySimulator(targetScore: 100).PlayMatch(gameId: 17, matchSeed: 73_019);
        var round = match.Rounds[0];
        var perspective = PlayerSeat.Bottom;
        var temporaryDirectory = CreateTemporaryDirectory();
        try
        {
            var jsonOptions = new JsonSerializerOptions();
            jsonOptions.Converters.Add(new CompactCardJsonConverter());
            File.WriteAllText(
                Path.Combine(temporaryDirectory, "matches.jsonl"),
                JsonSerializer.Serialize(match, jsonOptions));

            var trace = SelfPlayReplayTrace.FormatFromJournal(
                temporaryDirectory,
                match.GameId,
                round.RoundIndex,
                perspective);

            Assert.Contains("Your 14 dealt cards before passing:", trace);
            Assert.Contains("You passed: right", trace);
            Assert.Contains("You received: right", trace);
            Assert.Contains("Public action history:", trace);
            Assert.Contains("Passed this trick:", trace);
            Assert.Contains("Trick 2 begins", trace);
            Assert.Contains("Trick collected by", trace);
            Assert.Equal(
                round.Collections.Count,
                trace.Split("Trick collected by", StringSplitOptions.None).Length - 1);
            foreach (var action in round.Actions.Where(action => action.Seat != perspective))
            {
                Assert.Contains($"  {action.Seat}'s hand: hidden ({action.HandBefore.Count} cards)", trace);
                Assert.DoesNotContain(
                    $"  {action.Seat}'s hand: {FormatReplayCards(action.HandBefore)}",
                    trace);
            }

            Assert.DoesNotContain("Deck order", trace);
        }
        finally
        {
            Directory.Delete(temporaryDirectory, recursive: true);
        }
    }

    [Fact]
    public void SummaryMatchModePreservesSeededScoresAndEveryPolicyDecision()
    {
        const int matchSeed = 73_019;
        var simulator = new RandomSelfPlaySimulator(targetScore: 100);
        var recordedTrace = new Dictionary<PlayerSeat, List<string>>();
        var summaryTrace = new Dictionary<PlayerSeat, List<string>>();
        var recordedContexts = new Dictionary<PlayerSeat, List<PolicyRoundContext>>();
        var summaryContexts = new Dictionary<PlayerSeat, List<PolicyRoundContext>>();

        var recorded = simulator.PlayMatch(
            gameId: 3,
            matchSeed,
            CreateTracingPolicyFactory(matchSeed, recordedTrace, recordedContexts));
        var summary = simulator.PlayMatchSummary(
            gameId: 3,
            matchSeed,
            CreateTracingPolicyFactory(matchSeed, summaryTrace, summaryContexts));

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
            Assert.Equal(recordedContexts[seat], summaryContexts[seat]);
            Assert.Equal(recorded.Rounds.Count, recordedContexts[seat].Count);
            for (var roundIndex = 0; roundIndex < recorded.Rounds.Count; roundIndex++)
            {
                var passBySeat = recorded.Rounds[roundIndex].Passes.ToDictionary(pass => pass.Seat);
                var context = recordedContexts[seat][roundIndex];
                Assert.Equal(passBySeat[seat].ToPartner, context.PassSelection.ToPartner);
                Assert.Equal(passBySeat[seat.Partner()].ToPartner, context.ReceivedFromPartner);
                Assert.Equal(passBySeat[seat.ToTheRight()].ToLeft, context.ReceivedFromRightOpponent);
                Assert.Equal(passBySeat[seat.ToTheLeft()].ToRight, context.ReceivedFromLeftOpponent);
            }
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
        Assert.All(
            noWishFeatures.Skip(MoveFeatureEncoder.WishRankInteractionStart)
                .Take(MoveFeatureEncoder.KnownPassedLeftCanBeatSingleIndex - MoveFeatureEncoder.WishRankInteractionStart),
            value => Assert.Equal(0.0, value));
    }

    [Fact]
    public void PartnerPassSignalPreservesTheReceivedAceAndGuidesKingWhenOpponentsCannotHoldAces()
    {
        var receivedAce = new Card(CardRank.Ace, CardSuit.Jade);
        var alternateAce = new Card(CardRank.Ace, CardSuit.Sword);
        var acePassedToPartner = new Card(CardRank.Ace, CardSuit.Pagoda);
        var aceAlreadyPlayed = new Card(CardRank.Ace, CardSuit.Star);
        var king = new Card(CardRank.King, CardSuit.Jade);
        var topCard = new Card(CardRank.Seven, CardSuit.Pagoda);
        var hand = new[] { receivedAce, alternateAce, king };
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(
                new Card(CardRank.Two, CardSuit.Jade),
                acePassedToPartner,
                new Card(CardRank.Three, CardSuit.Jade)),
            receivedAce);
        var snapshot = CreateSnapshotWithTopAndPlayedCards(
            topCard,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                new[] { aceAlreadyPlayed },
                Array.Empty<Card>(),
                new[] { topCard }
            },
            new[] { hand.Length, 4, 4, 5 });
        var alternateAceMove = new LegalMove(CardCombination.ClassifyAll(new[] { alternateAce }).Single());
        var receivedAceMove = new LegalMove(CardCombination.ClassifyAll(new[] { receivedAce }).Single());
        var pairOfAcesMove = new LegalMove(CardCombination.ClassifyAll(new[] { receivedAce, alternateAce }).Single());
        var kingMove = new LegalMove(CardCombination.ClassifyAll(new[] { king }).Single());

        var preserveFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, snapshot, alternateAceMove, context);
        var spendFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, snapshot, receivedAceMove, context);
        var pairFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, snapshot, pairOfAcesMove, context);
        var kingFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, snapshot, kingMove, context);

        Assert.Equal(1.0, preserveFeatures[MoveFeatureEncoder.PreservesPartnerPassedCardIndex]);
        Assert.Equal(0.0, spendFeatures[MoveFeatureEncoder.PreservesPartnerPassedCardIndex]);
        Assert.Equal(0.0, pairFeatures[MoveFeatureEncoder.PreservesPartnerPassedCardIndex]);
        Assert.Equal(1.0, kingFeatures[MoveFeatureEncoder.KingSingleSafeFromOpponentAcesIndex]);
        Assert.Equal(1.0, kingFeatures[MoveFeatureEncoder.CurrentTopIsLeftOpponentIndex]);
        Assert.Equal(0.0, kingFeatures[MoveFeatureEncoder.CurrentTopIsRightOpponentIndex]);
        Assert.Equal(4 / 14.0, kingFeatures[MoveFeatureEncoder.RightOpponentHandSizeIndex]);
        Assert.Equal(5 / 14.0, kingFeatures[MoveFeatureEncoder.LeftOpponentHandSizeIndex]);
        Assert.Equal(7 / 16.0, kingFeatures[MoveFeatureEncoder.CurrentTopStrengthIndex]);
        Assert.Equal(1 / 14.0, kingFeatures[MoveFeatureEncoder.CurrentTopCardCountIndex]);
        Assert.Equal(1.0, kingFeatures[
            MoveFeatureEncoder.CurrentTopCombinationTypeStart + (int)CombinationType.Single]);

        var preserveWeights = new double[MoveFeatureEncoder.FeatureCount];
        preserveWeights[MoveFeatureEncoder.PreservesPartnerPassedCardIndex] = 1_000.0;
        var preservePolicy = new LinearPreferencePolicy(preserveWeights, recordEpisode: false);
        preservePolicy.BeginEpisode(seed: 1);
        preservePolicy.BeginRound(context);
        Assert.Equal(alternateAce, Assert.Single(preservePolicy.ChooseMove(PlayerSeat.Bottom, hand, snapshot).Combination!.Cards));

        var responseSnapshot = CreateSnapshotWithTopAndPlayedCards(
            topCard,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                new[] { aceAlreadyPlayed },
                Array.Empty<Card>(),
                new[] { topCard }
            },
            new[] { 2, 4, 4, 5 });
        var twoAceHand = new[] { receivedAce, alternateAce };
        var discouragePreservationWeights = new double[MoveFeatureEncoder.FeatureCount];
        discouragePreservationWeights[MoveFeatureEncoder.PreservesPartnerPassedCardIndex] = -1_000.0;
        for (var seed = 0; seed < 100; seed++)
        {
            var policy = new LinearPreferencePolicy(discouragePreservationWeights, recordEpisode: false);
            policy.BeginEpisode(seed);
            policy.BeginRound(context);
            var chosenMove = policy.ChooseMove(PlayerSeat.Bottom, twoAceHand, responseSnapshot);
            Assert.False(chosenMove.Combination is { Type: CombinationType.Single, Cards.Count: 1 } chosenSingle &&
                         chosenSingle.Cards[0] == receivedAce);
        }

        var kingWeights = new double[MoveFeatureEncoder.FeatureCount];
        kingWeights[MoveFeatureEncoder.KingSingleSafeFromOpponentAcesIndex] = 1_000.0;
        var kingPolicy = new LinearPreferencePolicy(kingWeights, recordEpisode: false);
        kingPolicy.BeginEpisode(seed: 2);
        kingPolicy.BeginRound(context);
        Assert.Equal(king, Assert.Single(kingPolicy.ChooseMove(PlayerSeat.Bottom, hand, snapshot).Combination!.Cards));

        var unknownAceSnapshot = CreateSnapshotWithTopAndPlayedCards(
            topCard,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                new[] { topCard }
            },
            new[] { hand.Length, 4, 4, 5 });
        var unknownAceFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom, hand, unknownAceSnapshot, kingMove, context);
        Assert.Equal(0.0, unknownAceFeatures[MoveFeatureEncoder.KingSingleSafeFromOpponentAcesIndex]);
    }

    [Fact]
    public void KnownPassedPhoenixCanBeatAKingButNotDragon()
    {
        var topCard = new Card(CardRank.Eight, CardSuit.Jade);
        var king = new Card(CardRank.King, CardSuit.Sword);
        var dragon = new Card(CardRank.Dragon);
        var hand = new[] { king, dragon };
        var snapshot = CreateSnapshotWithTopAndPlayedCards(
            topCard,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                new[] { topCard }
            },
            new[] { hand.Length, 4, 4, 5 });
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(
                new Card(CardRank.Two, CardSuit.Jade),
                new Card(CardRank.Three, CardSuit.Jade),
                new Card(CardRank.Phoenix)));

        var kingFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom,
            hand,
            snapshot,
            new LegalMove(CardCombination.ClassifyAll(new[] { king }).Single()),
            context);
        var dragonFeatures = MoveFeatureEncoder.Encode(
            PlayerSeat.Bottom,
            hand,
            snapshot,
            new LegalMove(CardCombination.ClassifyAll(new[] { dragon }).Single()),
            context);

        Assert.Equal(1.0, kingFeatures[MoveFeatureEncoder.KnownPassedLeftCanBeatSingleIndex]);
        Assert.Equal(0.0, dragonFeatures[MoveFeatureEncoder.KnownPassedLeftCanBeatSingleIndex]);
    }

    [Fact]
    public void PartnerPassedAceIsPreservedInPairsButDoesNotBlockAUniqueBomb()
    {
        var receivedAce = new Card(CardRank.Ace, CardSuit.Jade);
        var secondAce = new Card(CardRank.Ace, CardSuit.Sword);
        var thirdAce = new Card(CardRank.Ace, CardSuit.Pagoda);
        var fourthAce = new Card(CardRank.Ace, CardSuit.Star);
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(
                new Card(CardRank.Three, CardSuit.Jade),
                new Card(CardRank.Four, CardSuit.Sword),
                new Card(CardRank.Five, CardSuit.Pagoda)),
            receivedAce);
        var topPairCards = new[]
        {
            new Card(CardRank.Seven, CardSuit.Jade),
            new Card(CardRank.Seven, CardSuit.Star)
        };
        var topPair = CardCombination.ClassifyAll(topPairCards).Single();
        var pairSnapshot = CreateSnapshotWithTopCombinationAndPlayedCards(
            topPair,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                topPairCards
            },
            new[] { 3, 4, 4, 5 });
        var pairWeights = new double[MoveFeatureEncoder.FeatureCount];
        pairWeights[MoveFeatureEncoder.FeatureNames.ToList().IndexOf("CombinationPair")] = 2_000.0;
        pairWeights[MoveFeatureEncoder.PreservesPartnerPassedCardIndex] = -1_000.0;

        for (var seed = 0; seed < 100; seed++)
        {
            var policy = new LinearPreferencePolicy(pairWeights, recordEpisode: false);
            policy.BeginEpisode(seed);
            policy.BeginRound(context);
            var chosenMove = policy.ChooseMove(
                PlayerSeat.Bottom,
                new[] { receivedAce, secondAce, thirdAce },
                pairSnapshot);

            Assert.Equal(CombinationType.Pair, chosenMove.Combination?.Type);
            Assert.DoesNotContain(receivedAce, chosenMove.Combination!.Cards);
        }

        var topTwos = new[]
        {
            new Card(CardRank.Two, CardSuit.Jade),
            new Card(CardRank.Two, CardSuit.Sword),
            new Card(CardRank.Two, CardSuit.Pagoda),
            new Card(CardRank.Two, CardSuit.Star)
        };
        var topFourOfAKind = CardCombination.ClassifyAll(topTwos).Single();
        var bombSnapshot = CreateSnapshotWithTopCombinationAndPlayedCards(
            topFourOfAKind,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                topTwos
            },
            new[] { 4, 4, 4, 4 });
        var bombWeights = new double[MoveFeatureEncoder.FeatureCount];
        bombWeights[3] = 1_000.0;
        var bombPolicy = new LinearPreferencePolicy(bombWeights, recordEpisode: false);
        bombPolicy.BeginEpisode(seed: 1);
        bombPolicy.BeginRound(context);
        var chosenBomb = bombPolicy.ChooseMove(
            PlayerSeat.Bottom,
            new[] { receivedAce, secondAce, thirdAce, fourthAce },
            bombSnapshot);

        Assert.True(chosenBomb.Combination?.IsBomb);
        Assert.Contains(receivedAce, chosenBomb.Combination!.Cards);
    }

    [Fact]
    public void MoveChoiceKeepsPartnerPassedAceAndUsesEquivalentOpponentPassedAce()
    {
        var partnerPassedAce = new Card(CardRank.Ace, CardSuit.Jade);
        var opponentPassedAce = new Card(CardRank.Ace, CardSuit.Sword);
        var ownAce = new Card(CardRank.Ace, CardSuit.Pagoda);
        var topCard = new Card(CardRank.Seven, CardSuit.Star);
        var hand = new[] { partnerPassedAce, opponentPassedAce, ownAce };
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(
                new Card(CardRank.Two, CardSuit.Jade),
                new Card(CardRank.Three, CardSuit.Sword),
                new Card(CardRank.Four, CardSuit.Pagoda)),
            ReceivedFromPartner: partnerPassedAce,
            ReceivedFromRightOpponent: opponentPassedAce);
        var snapshot = CreateSnapshotWithTopAndPlayedCards(
            topCard,
            new IReadOnlyList<Card>[]
            {
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                new[] { topCard },
                Array.Empty<Card>()
            },
            new[] { hand.Length, 4, 4, 4 });
        var weights = new double[MoveFeatureEncoder.FeatureCount];
        weights[MoveFeatureEncoder.FeatureNames.ToList().IndexOf("PlayedAce")] = 1_000.0;

        for (var seed = 0; seed < 100; seed++)
        {
            var policy = new LinearPreferencePolicy(weights, recordEpisode: false);
            policy.BeginEpisode(seed);
            policy.BeginRound(context);

            var chosen = policy.ChooseMove(PlayerSeat.Bottom, hand, snapshot);

            Assert.Equal(new[] { opponentPassedAce }, chosen.Combination!.Cards);
        }
    }

    [Fact]
    public void TichuCallPlanAnalysisCountsLeadRiskAndKeepsAnAceSplitPlan()
    {
        var deck = TichuDeck.CreateStandard();
        var hand = new[]
        {
            new Card(CardRank.Dragon),
            new Card(CardRank.Phoenix),
            new Card(CardRank.Ace, CardSuit.Jade),
            new Card(CardRank.Ace, CardSuit.Sword),
            new Card(CardRank.Two, CardSuit.Jade)
        };
        var passedRight = new Card(CardRank.Three, CardSuit.Jade);
        var passedPartner = new Card(CardRank.Ace, CardSuit.Pagoda);
        var passedLeft = new Card(CardRank.Four, CardSuit.Jade);
        var outgoing = new[] { passedRight, passedPartner, passedLeft };
        var excluded = hand.Concat(outgoing).ToHashSet();
        var playedByActor = deck.Where(card => !excluded.Contains(card)).Take(9).ToArray();
        excluded.UnionWith(playedByActor);
        var unknownCards = deck.Where(card => !excluded.Contains(card)).ToArray();
        Assert.Equal(39, unknownCards.Length);

        var snapshot = new RoundSnapshot(
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
            new[] { 5, 14, 14, 14 },
            new[] { 0, 0, 0, 0 })
        {
            PlayedCardsBySeat = new IReadOnlyList<Card>[]
            {
                playedByActor,
                Array.Empty<Card>(),
                Array.Empty<Card>(),
                Array.Empty<Card>()
            }
        };
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(passedRight, passedPartner, passedLeft),
            ReceivedFromPartner: hand[0],
            ReceivedFromRightOpponent: hand[1],
            ReceivedFromLeftOpponent: hand[3]);

        var analysis = TichuCallPlanAnalyzer.Analyze(
            PlayerSeat.Bottom,
            hand,
            snapshot,
            context,
            sampleCount: 64,
            additionalPlayLimit: 1);

        Assert.Equal(3, analysis.MinimumPlayCount);
        Assert.Equal(39, analysis.UnknownCardCount);
        Assert.Equal(new[] { passedRight }, analysis.KnownPassedCardsBySeat[PlayerSeat.Right]);
        Assert.Equal(new[] { passedPartner }, analysis.KnownPassedCardsBySeat[PlayerSeat.Top]);
        Assert.Equal(new[] { passedLeft }, analysis.KnownPassedCardsBySeat[PlayerSeat.Left]);
        Assert.Contains(analysis.CandidatePlans, plan =>
            plan.PlayCount == 4 &&
            plan.Plays.Count(play => play.Combination.Type == CombinationType.Single &&
                                     play.Combination.Cards[0].Rank == CardRank.Ace) == 2);

        var dragonRisk = Assert.Single(analysis.LeadEstimates, lead =>
            lead.Combination.Type == CombinationType.Single && lead.Combination.Cards[0].Rank == CardRank.Dragon);
        var twoRisk = Assert.Single(analysis.LeadEstimates, lead =>
            lead.Combination.Type == CombinationType.Single && lead.Combination.Cards[0] == hand[4]);
        Assert.True(dragonRisk.OpponentCanBeatProbability < twoRisk.OpponentCanBeatProbability);

        var phoenixRisk = Assert.Single(analysis.LeadEstimates, lead =>
            lead.Combination.Type == CombinationType.Single && lead.Combination.Cards[0].Rank == CardRank.Phoenix);
        Assert.Equal(14, phoenixRisk.SingleRankResponseCoverage);
        Assert.Empty(analysis.SequenceEstimates);
        Assert.NotNull(analysis.SequenceReplaySkippedReason);
    }

    [Fact]
    public void TichuPlanAnalyzerComparesOrdersOnPairedInitialDeals()
    {
        var cards = TichuDeck.CreateStandard().ToArray();
        var random = new Random(79);
        for (var index = cards.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (cards[index], cards[other]) = (cards[other], cards[index]);
        }

        var hand = cards.Take(14).ToArray();
        var outgoing = cards.Skip(14).Take(3).ToArray();
        var snapshot = new RoundSnapshot(
            new TrickSnapshot(
                null,
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
            new[] { 14, 14, 14, 14 },
            new[] { 0, 0, 0, 0 })
        {
            PlayedCardsBySeat = Enum.GetValues<PlayerSeat>()
                .Select(_ => (IReadOnlyList<Card>)Array.Empty<Card>())
                .ToArray()
        };
        var context = new PolicyRoundContext(
            PlayerSeat.Bottom,
            new CardPassSelection(outgoing[0], outgoing[1], outgoing[2]));

        var analysis = TichuCallPlanAnalyzer.Analyze(
            PlayerSeat.Bottom,
            hand,
            snapshot,
            context,
            sampleCount: 2,
            additionalPlayLimit: 1,
            maximumPlans: 16);

        Assert.NotEmpty(analysis.SequenceEstimates);
        Assert.All(analysis.SequenceEstimates, sequence => Assert.Equal(2, sequence.SampleCount));
        Assert.Contains(analysis.SequenceEstimates, sequence => sequence.OrderingName == "partition order");
        Assert.Contains(analysis.SequenceEstimates, sequence => sequence.PartnerModel == "random partner");
        Assert.Contains(analysis.SequenceEstimates, sequence => sequence.PartnerModel == "caller-support partner");
        Assert.Contains("Ordered sequence replays: 2 paired sampled deals per order", TichuCallPlanAnalysis.Format(analysis));
    }

    [Theory]
    [InlineData(1, MoveFeatureEncoder.LegacyFeatureCount)]
    [InlineData(2, MoveFeatureEncoder.PreviousFeatureCount)]
    [InlineData(3, MoveFeatureEncoder.PreviousCurrentFeatureCount)]
    public void PolicySnapshotMigratesEarlierMoveFeatureSchemas(int schemaVersion, int featureCount)
    {
        var checkpoint = new LinearPolicyTrainingCheckpoint(
            new PolicyTrainingConfiguration(
                CheckpointSchemaVersion: 1,
                ModelSchemaVersion: schemaVersion,
                FeatureCount: featureCount,
                BaseSeed: 1,
                TargetScore: 1_000,
                DecisionSeconds: 120,
                LearningRate: LinearPolicyTrainer.DefaultLearningRate)
            {
                DeclarationFeatureCount = DeclarationFeatureEncoder.PreviousFeatureCount
            },
            CompletedGames: 4,
            Wins: 2,
            Losses: 2,
            Draws: 0,
            AverageReward: 0.0,
            RewardBaseline: 0.0,
            Weights: Enumerable.Range(0, featureCount).Select(index => (double)index).ToArray(),
            UpdatedAt: DateTimeOffset.UtcNow)
        {
            DeclarationWeights = Enumerable.Range(0, DeclarationFeatureEncoder.PreviousFeatureCount)
                .Select(index => (double)index)
                .ToArray(),
            PassWeights = new double[PassFeatureEncoder.FeatureCount]
        };

        var snapshot = LinearPolicySnapshot.FromCheckpoint(checkpoint);

        Assert.Equal(MoveFeatureEncoder.FeatureCount, snapshot.Weights.Count);
        Assert.Equal(Enumerable.Range(0, featureCount).Select(index => (double)index), snapshot.Weights.Take(featureCount));
        Assert.All(snapshot.Weights.Skip(featureCount), weight => Assert.Equal(0.0, weight));
        Assert.Equal(DeclarationFeatureEncoder.FeatureCount, snapshot.DeclarationWeights.Count);
        Assert.Equal(
            Enumerable.Range(0, DeclarationFeatureEncoder.PreviousFeatureCount).Select(index => (double)index),
            snapshot.DeclarationWeights.Take(DeclarationFeatureEncoder.PreviousFeatureCount));
        Assert.All(
            snapshot.DeclarationWeights.Skip(DeclarationFeatureEncoder.PreviousFeatureCount),
            weight => Assert.Equal(0.0, weight));
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
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.TichuCurrentTopOwnedByRightOpponentIndex]);
        Assert.Equal(0.0, features[DeclarationFeatureEncoder.TichuCurrentTopOwnedByLeftOpponentIndex]);
        Assert.Equal(1.0, features[DeclarationFeatureEncoder.TichuPartnerHandSizeIndex]);
        Assert.Equal(13 / 14.0, features[DeclarationFeatureEncoder.TichuRightOpponentHandSizeIndex]);
        Assert.Equal(1.0, features[DeclarationFeatureEncoder.TichuLeftOpponentHandSizeIndex]);
        Assert.Equal(0.5, features[DeclarationFeatureEncoder.TichuCurrentTopStrengthIndex]);
        Assert.Equal(1 / 14.0, features[DeclarationFeatureEncoder.TichuCurrentTopCardCountIndex]);
        Assert.Equal(
            1.0,
            features[DeclarationFeatureEncoder.TichuCurrentTopCombinationTypeStart + (int)CombinationType.Single]);

        var rightOpponentSnapshot = playSnapshot with
        {
            Trick = playSnapshot.Trick with
            {
                CurrentTopSeat = PlayerSeat.Right,
                Plays = new[] { new TrickPlay(PlayerSeat.Right, topCombination) }
            }
        };
        var rightOpponentFeatures = DeclarationFeatureEncoder.Encode(
            hand,
            isGrandDecision: false,
            context with { PlaySnapshot = rightOpponentSnapshot });
        Assert.Equal(0.0, rightOpponentFeatures[DeclarationFeatureEncoder.TichuCurrentTrickLedByPartnerIndex]);
        Assert.Equal(1.0, rightOpponentFeatures[DeclarationFeatureEncoder.TichuCurrentTopOwnedByRightOpponentIndex]);
        Assert.Equal(0.0, rightOpponentFeatures[DeclarationFeatureEncoder.TichuCurrentTopOwnedByLeftOpponentIndex]);

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

    private static string FormatReplayCard(Card card)
    {
        if (!card.IsOrdinary)
        {
            return card.Rank.ToString();
        }

        var rank = card.Rank switch
        {
            CardRank.Two => "2",
            CardRank.Three => "3",
            CardRank.Four => "4",
            CardRank.Five => "5",
            CardRank.Six => "6",
            CardRank.Seven => "7",
            CardRank.Eight => "8",
            CardRank.Nine => "9",
            CardRank.Ten => "10",
            CardRank.Jack => "J",
            CardRank.Queen => "Q",
            CardRank.King => "K",
            CardRank.Ace => "A",
            _ => throw new ArgumentOutOfRangeException(nameof(card))
        };
        return $"{rank}-{card.Suit}";
    }

    private static string FormatReplayCards(IEnumerable<Card> cards) =>
        string.Join(
            " ",
            cards.OrderBy(card => (int)card.Rank * 4 + (int)(card.Suit ?? CardSuit.Jade))
                .Select(FormatReplayCard));

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
        IDictionary<PlayerSeat, List<string>> traces,
        IDictionary<PlayerSeat, List<PolicyRoundContext>> contexts) => seat =>
    {
        var trace = new List<string>();
        traces.Add(seat, trace);
        var seatContexts = new List<PolicyRoundContext>();
        contexts.Add(seat, seatContexts);
        var policySeed = unchecked(matchSeed + ((int)seat + 1) * 104_729);
        return new TracingPolicy(new RandomBaselinePolicy(new Random(policySeed)), trace, seatContexts);
    };

    private sealed class TracingPolicy(
        RandomBaselinePolicy baseline,
        List<string> trace,
        List<PolicyRoundContext> contexts) : ITichuPolicy
    {
        public void BeginRound(PolicyRoundContext context)
        {
            baseline.BeginRound(context);
            contexts.Add(context);
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

    private static RoundSnapshot CreateSnapshotWithTopAndPlayedCards(
        Card topCard,
        IReadOnlyList<Card>[] playedCardsBySeat,
        int[] handSizes)
    {
        var topCombination = CardCombination.ClassifyAll(new[] { topCard }).Single();
        return CreateSnapshotWithTopCombinationAndPlayedCards(topCombination, playedCardsBySeat, handSizes);
    }

    private static RoundSnapshot CreateSnapshotWithTopCombinationAndPlayedCards(
        CardCombination topCombination,
        IReadOnlyList<Card>[] playedCardsBySeat,
        int[] handSizes)
    {
        return new RoundSnapshot(
            new TrickSnapshot(
                PlayerSeat.Bottom,
                topCombination,
                PlayerSeat.Left,
                null,
                new[] { new TrickPlay(PlayerSeat.Left, topCombination) },
                Enum.GetValues<PlayerSeat>(),
                Array.Empty<PlayerSeat>(),
                Array.Empty<PlayerSeat>(),
                CanCollect: false,
                IsDoubleVictory: false,
                IsRoundEnded: false),
            handSizes,
            new[] { 0, 0, 0, 0 })
        {
            PlayedCardsBySeat = playedCardsBySeat
        };
    }
}
