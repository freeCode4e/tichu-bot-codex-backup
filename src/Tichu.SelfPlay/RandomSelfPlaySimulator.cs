using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Runs deterministic matches with a simple stochastic baseline policy.</summary>
public sealed class RandomSelfPlaySimulator
{
    public const int RecordSchemaVersion = 3;
    private const int MaxActionsPerRound = 5_000;

    private readonly int _targetScore;
    private readonly int _maxRounds;
    private readonly TimeSpan _decisionLimit;

    public RandomSelfPlaySimulator(
        int targetScore = 1_000,
        int maxRounds = 500,
        TimeSpan? decisionLimit = null)
    {
        if (targetScore <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetScore));
        }

        if (maxRounds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRounds));
        }

        _targetScore = targetScore;
        _maxRounds = maxRounds;
        _decisionLimit = decisionLimit ?? TimeSpan.FromMinutes(2);
        if (_decisionLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionLimit));
        }
    }

    public SelfPlayMatchRecord PlayMatch(
        long gameId,
        int matchSeed,
        Func<PlayerSeat, ITichuPolicy>? policyFactory = null) =>
        SimulateMatch(gameId, matchSeed, policyFactory, captureDetails: true).Record!;

    /// <summary>Plays a match without allocating replay records for its actions and tricks.</summary>
    public SelfPlayMatchSummary PlayMatchSummary(
        long gameId,
        int matchSeed,
        Func<PlayerSeat, ITichuPolicy>? policyFactory = null) =>
        SimulateMatch(gameId, matchSeed, policyFactory, captureDetails: false).Summary;

    private MatchSimulationResult SimulateMatch(
        long gameId,
        int matchSeed,
        Func<PlayerSeat, ITichuPolicy>? policyFactory,
        bool captureDetails)
    {
        if (gameId < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameId));
        }

        var bottomTopScore = 0;
        var rightLeftScore = 0;
        var policies = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => policyFactory?.Invoke(seat) ?? new RandomBaselinePolicy(
                new Random(unchecked(matchSeed + ((int)seat + 1) * 104_729))));
        List<SelfPlayRoundRecord>? rounds = captureDetails ? new List<SelfPlayRoundRecord>() : null;
        for (var roundIndex = 0; roundIndex < _maxRounds; roundIndex++)
        {
            var roundSeed = DeriveRoundSeed(gameId, matchSeed, roundIndex);
            var score = PlayRound(
                roundIndex,
                roundSeed,
                bottomTopScore,
                rightLeftScore,
                policies,
                captureDetails,
                out var roundRecord);
            if (roundRecord is not null)
            {
                rounds!.Add(roundRecord);
            }

            bottomTopScore += score.BottomTopTotal;
            rightLeftScore += score.RightLeftTotal;

            if (bottomTopScore >= _targetScore || rightLeftScore >= _targetScore)
            {
                var winner = bottomTopScore == rightLeftScore &&
                             bottomTopScore >= _targetScore && rightLeftScore >= _targetScore
                    ? SelfPlayWinner.Draw
                    : bottomTopScore >= rightLeftScore
                        ? SelfPlayWinner.BottomTop
                        : SelfPlayWinner.RightLeft;
                var summary = new SelfPlayMatchSummary(
                    gameId,
                    matchSeed,
                    _targetScore,
                    bottomTopScore,
                    rightLeftScore,
                    winner);
                var record = rounds is null
                    ? null
                    : new SelfPlayMatchRecord(
                        RecordSchemaVersion,
                        gameId,
                        matchSeed,
                        _targetScore,
                        bottomTopScore,
                        rightLeftScore,
                        winner,
                        Array.AsReadOnly(rounds.ToArray()));
                return new MatchSimulationResult(record, summary);
            }
        }

        throw new InvalidOperationException(
            $"Match {gameId} did not reach {_targetScore} points within {_maxRounds} rounds.");
    }

    private RoundScoringResult PlayRound(
        int roundIndex,
        int seed,
        int bottomTopScoreBefore,
        int rightLeftScoreBefore,
        IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies,
        bool captureDetails,
        out SelfPlayRoundRecord? roundRecord)
    {
        var deal = TichuDealer.CreateShuffled(seed);
        var phaseStartedAt = DateTimeOffset.UnixEpoch.AddDays(roundIndex);
        var declarationPhase = new GrandDecisionPhase(phaseStartedAt, _decisionLimit);
        var eventSeconds = 1;
        var declarationSnapshot = declarationPhase.AdvanceTo(phaseStartedAt);

        // Grand is decided from the first eight cards, before the exchange.
        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var policy = policies[seat];
            var initialHand = deal.InitialEightHands[seat];
            if (policy.ShouldCallGrand(initialHand, new PolicyDeclarationContext(seat, declarationSnapshot)))
            {
                var grandCall = declarationPhase.TryCallGrand(
                    seat,
                    phaseStartedAt.AddSeconds(eventSeconds++),
                    confirmPartnerCall: true);
                RequireAccepted(grandCall.Status, $"Grand call by {seat}");
                declarationSnapshot = grandCall.Snapshot;
                continue;
            }

            var grandDecline = declarationPhase.TryDeclineGrand(
                seat,
                phaseStartedAt.AddSeconds(eventSeconds++));
            RequireAccepted(grandDecline.Status, $"Grand decline by {seat}");
            declarationSnapshot = grandDecline.Snapshot;
            var fourteenthCard = declarationPhase.MarkFourteenthCardDealt(
                seat,
                phaseStartedAt.AddSeconds(eventSeconds++));
            RequireAccepted(fourteenthCard.Status, $"fourteenth card for {seat}");
            declarationSnapshot = fourteenthCard.Snapshot;
        }

        var decisions = declarationPhase.AdvanceTo(phaseStartedAt.AddSeconds(eventSeconds));
        var calls = Enum.GetValues<PlayerSeat>()
            .Select(seat =>
            {
                var decision = decisions.For(seat);
                if (decision.Status == GrandDecisionStatus.Called)
                {
                    return new SelfPlayCallRecord(
                        seat,
                        TichuCall.Grand,
                        HandBeforeCall: Array.AsReadOnly(deal.InitialEightHands[seat].ToArray()));
                }

                return null;
            })
            .Where(call => call is not null)
            .Select(call => call!)
            .ToList();

        var passSelections = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => policies[seat].ChoosePass(seat, deal.FourteenCardHands[seat]));
        var preparedRound = TichuRoundSetup.Prepare(deal, passSelections);
        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            policies[seat].BeginRound(new PolicyRoundContext(seat, passSelections[seat]));
        }

        var round = preparedRound.CreateRoundState();
        var playPhaseStartedAt = phaseStartedAt.AddSeconds(eventSeconds++);
        declarationSnapshot = declarationPhase.AdvanceTo(playPhaseStartedAt);
        var callsBySeat = calls.ToDictionary(call => call.Seat, call => call.Call);
        var recordedPasses = captureDetails
            ? passSelections.Select(pair => new SelfPlayPassRecord(
                pair.Key,
                pair.Value.ToRight,
                pair.Value.ToPartner,
                pair.Value.ToLeft)).ToArray()
            : null;
        List<SelfPlayActionRecord>? actionRecords = captureDetails ? new List<SelfPlayActionRecord>() : null;
        List<SelfPlayCollectionRecord>? collectionRecords = captureDetails ? new List<SelfPlayCollectionRecord>() : null;
        var firstCardPlayed = new bool[4];
        var actionClock = 0;

        // Tichu uses the exchanged hand and remains open until that player
        // plays their first card, so keep reconsidering it against public play.
        for (var actionCount = 0; actionCount < MaxActionsPerRound; actionCount++)
        {
            var snapshot = round.Snapshot;
            var trick = snapshot.Trick;
            if (trick.IsDoubleVictory)
            {
                break;
            }

            if (trick.IsRoundEnded)
            {
                if (trick.Plays.Count > 0)
                {
                    CollectTrick(round, policies, collectionRecords);
                }

                break;
            }

            ReconsiderAvailableTichuCalls(
                policies,
                round,
                declarationPhase,
                playPhaseStartedAt,
                ref declarationSnapshot,
                ref actionClock,
                actionRecords?.Count ?? actionCount,
                calls,
                callsBySeat);

            if (trick.CurrentTop is not null &&
                TryOutOfTurnBomb(round, snapshot, policies, declarationPhase, playPhaseStartedAt,
                    firstCardPlayed, actionRecords, ref actionClock))
            {
                continue;
            }

            if (trick.IsDoubleVictory)
            {
                break;
            }

            if (trick.IsRoundEnded)
            {
                if (trick.Plays.Count > 0)
                {
                    CollectTrick(round, policies, collectionRecords);
                }

                break;
            }

            if (trick.CanCollect)
            {
                CollectTrick(round, policies, collectionRecords);
                actionClock++;
                continue;
            }

            if (trick.CurrentTurnSeat is not { } seat)
            {
                throw new InvalidOperationException("The active trick has no current seat and is not ready to collect.");
            }

            var hand = round.HandFor(seat);
            var chosen = policies[seat].ChooseMove(seat, hand, snapshot);
            if (actionRecords is not null)
            {
                actionRecords.Add(CreateActionRecord(round, seat, chosen));
            }

            var action = chosen.IsPass
                ? round.TryPass(seat)
                : round.TryPlay(seat, chosen.Combination!, chosen.WishForMahJong);
            RequireAccepted(action.Status, $"move by {seat}");
            if (!chosen.IsPass)
            {
                MarkFirstCardIfNeeded(seat, declarationPhase, playPhaseStartedAt, firstCardPlayed, ref actionClock);
            }

            actionClock++;
        }

        var finalTrick = round.Snapshot.Trick;
        if (!finalTrick.IsRoundEnded)
        {
            throw new InvalidOperationException($"Round {roundIndex} exceeded {MaxActionsPerRound} actions.");
        }

        if (!finalTrick.IsDoubleVictory && finalTrick.Plays.Count > 0)
        {
            CollectTrick(round, policies, collectionRecords);
        }

        var scoringCalls = callsBySeat.Count == 0 ? null : callsBySeat;
        var score = round.ScoreRound(scoringCalls);
        roundRecord = captureDetails
            ? new SelfPlayRoundRecord(
                roundIndex,
                seed,
                bottomTopScoreBefore,
                rightLeftScoreBefore,
                Array.AsReadOnly(deal.DeckOrder.ToArray()),
                Array.AsReadOnly(calls.ToArray()),
                Array.AsReadOnly(recordedPasses!),
                Array.AsReadOnly(actionRecords!.ToArray()),
                Array.AsReadOnly(collectionRecords!.ToArray()),
                Array.AsReadOnly(finalTrick.FinishOrder.ToArray()),
                score)
            : null;
        return score;
    }

    private static bool TryOutOfTurnBomb(
        RoundState round,
        RoundSnapshot snapshot,
        IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies,
        GrandDecisionPhase declarationPhase,
        DateTimeOffset playPhaseStartedAt,
        bool[] firstCardPlayed,
        List<SelfPlayActionRecord>? actionRecords,
        ref int actionClock)
    {
        var trick = snapshot.Trick;
        if (trick.CurrentTop is null || trick.CurrentTopSeat is null || trick.IsRoundEnded || trick.IsDoubleVictory)
        {
            return false;
        }

        var seat = trick.CurrentTopSeat.Value.ToTheRight();
        for (var offset = 0; offset < 4; offset++)
        {
            var candidateSeat = (PlayerSeat)(((int)seat + offset) % 4);
            if (!trick.ActiveSeats.Contains(candidateSeat) || trick.CurrentTurnSeat == candidateSeat)
            {
                continue;
            }

            var candidateHand = round.HandFor(candidateSeat);
            var bombs = MoveGenerator.Generate(
                candidateHand,
                trick.CurrentTop,
                trick.PendingWish,
                isOutOfTurn: true);
            var chosen = policies[candidateSeat].ChooseOutOfTurnBomb(
                candidateSeat,
                candidateHand,
                snapshot,
                bombs);
            if (chosen is null)
            {
                continue;
            }

            if (actionRecords is not null)
            {
                actionRecords.Add(CreateActionRecord(round, candidateSeat, chosen));
            }

            var result = round.TryBomb(candidateSeat, chosen.Combination!);
            RequireAccepted(result.Status, $"out-of-turn bomb by {candidateSeat}");
            MarkFirstCardIfNeeded(candidateSeat, declarationPhase, playPhaseStartedAt, firstCardPlayed, ref actionClock);
            actionClock++;
            return true;
        }

        return false;
    }

    private static void ReconsiderAvailableTichuCalls(
        IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies,
        RoundState round,
        GrandDecisionPhase declarationPhase,
        DateTimeOffset playPhaseStartedAt,
        ref GrandPhaseSnapshot declarationSnapshot,
        ref int actionClock,
        int actionIndex,
        List<SelfPlayCallRecord> calls,
        IDictionary<PlayerSeat, TichuCall> callsBySeat)
    {
        var decisionAt = playPhaseStartedAt.AddMilliseconds(actionClock);
        declarationSnapshot = declarationPhase.AdvanceTo(decisionAt);
        while (true)
        {
            var callWasMade = false;
            foreach (var seat in Enum.GetValues<PlayerSeat>())
            {
                if (!declarationSnapshot.For(seat).CanCallTichu)
                {
                    continue;
                }

                var hand = round.HandFor(seat);
                var playSnapshot = round.Snapshot;
                if (!policies[seat].ShouldCallTichu(
                        hand,
                        new PolicyDeclarationContext(seat, declarationSnapshot, playSnapshot)))
                {
                    continue;
                }

                decisionAt = playPhaseStartedAt.AddMilliseconds(actionClock++);
                var tichuCall = declarationPhase.TryCallTichu(
                    seat,
                    decisionAt);
                RequireAccepted(tichuCall.Status, $"Tichu call by {seat}");
                declarationSnapshot = tichuCall.Snapshot;
                calls.Add(new SelfPlayCallRecord(
                    seat,
                    TichuCall.Small,
                    actionIndex,
                    Array.AsReadOnly(hand.ToArray()),
                    playSnapshot.Trick.PendingWish));
                callsBySeat.Add(seat, TichuCall.Small);
                callWasMade = true;
            }

            if (!callWasMade)
            {
                return;
            }
        }
    }

    private static SelfPlayActionRecord CreateActionRecord(RoundState round, PlayerSeat seat, LegalMove move)
    {
        var snapshot = round.Snapshot;
        var trick = snapshot.Trick;
        var plays = trick.Plays.Select(AsTrickPlayRecord).ToArray();
        var combination = move.Combination;
        return new SelfPlayActionRecord(
            seat,
            Array.AsReadOnly(round.HandFor(seat).ToArray()),
            Array.AsReadOnly(trick.CurrentTop?.Cards.ToArray() ?? Array.Empty<Card>()),
            trick.CurrentTop?.Type,
            trick.CurrentTop?.Strength,
            trick.PendingWish,
            Array.AsReadOnly(plays),
            Array.AsReadOnly(trick.ActiveSeats.ToArray()),
            Array.AsReadOnly(trick.PassedSeats.ToArray()),
            Array.AsReadOnly(snapshot.HandSizes.ToArray()),
            Array.AsReadOnly(snapshot.CollectedPointsBySeat.ToArray()),
            move.IsPass,
            move.IsOutOfTurnBomb,
            Array.AsReadOnly(combination?.Cards.ToArray() ?? Array.Empty<Card>()),
            combination?.Type,
            move.WishForMahJong);
    }

    private static void CollectTrick(
        RoundState round,
        IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies,
        List<SelfPlayCollectionRecord>? collectionRecords)
    {
        var trick = round.Snapshot.Trick;
        var dragonTop = trick.CurrentTop?.Type == CombinationType.Single &&
                        trick.CurrentTop.Cards[0].Rank == CardRank.Dragon;
        var dragonRecipient = dragonTop
            ? policies[trick.CurrentTopSeat!.Value].ChooseDragonRecipient(trick.CurrentTopSeat.Value)
            : (PlayerSeat?)null;
        var collected = round.TryCollect(dragonRecipient);
        RequireAccepted(collected.Status, "trick collection");
        if (collected.Resolution is not { } resolution)
        {
            throw new InvalidOperationException("Accepted trick collection did not return a resolution.");
        }

        if (collectionRecords is not null)
        {
            collectionRecords.Add(new SelfPlayCollectionRecord(
                resolution.WinnerSeat,
                resolution.PointsRecipientSeat,
                Array.AsReadOnly(resolution.Cards.ToArray()),
                Array.AsReadOnly(resolution.Plays.Select(AsTrickPlayRecord).ToArray()),
                resolution.NextLeadSeat));
        }
    }

    private static SelfPlayTrickPlayRecord AsTrickPlayRecord(TrickPlay play) => new(
        play.Seat,
        play.Combination.Type,
        play.Combination.Strength,
        Array.AsReadOnly(play.Combination.Cards.ToArray()));

    private static void MarkFirstCardIfNeeded(
        PlayerSeat seat,
        GrandDecisionPhase declarationPhase,
        DateTimeOffset playPhaseStartedAt,
        bool[] firstCardPlayed,
        ref int actionClock)
    {
        var index = seat.ToIndex();
        if (firstCardPlayed[index])
        {
            return;
        }

        var result = declarationPhase.MarkFirstCardPlayed(
            seat,
            playPhaseStartedAt.AddMilliseconds(actionClock));
        if (result.Status is GrandDecisionActionStatus.BeforePhaseStart or GrandDecisionActionStatus.EventOutOfOrder)
        {
            throw new InvalidOperationException($"First-play declaration event for {seat} was out of order.");
        }

        firstCardPlayed[index] = true;
    }

    private static void RequireAccepted(GrandDecisionActionStatus status, string description)
    {
        if (status != GrandDecisionActionStatus.Accepted)
        {
            throw new InvalidOperationException($"Could not record {description}: {status}.");
        }
    }

    private static void RequireAccepted(TrickActionStatus status, string description)
    {
        if (status != TrickActionStatus.Accepted)
        {
            throw new InvalidOperationException($"Could not apply {description}: {status}.");
        }
    }

    private static int DeriveRoundSeed(long gameId, int matchSeed, int roundIndex) =>
        unchecked(matchSeed + (int)(gameId * 1_000_003L) + roundIndex * 7_919);

    private sealed record MatchSimulationResult(SelfPlayMatchRecord? Record, SelfPlayMatchSummary Summary);
}
