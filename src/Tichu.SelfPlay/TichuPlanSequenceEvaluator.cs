using System.Globalization;
using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>
/// Replays a small set of ordered Tichu plans against the same sampled deals.
/// The caller follows the requested plan. Each order is tested with a random
/// partner and a simple caller-support partner, while opponents use the random
/// baseline; results compare response models rather than predict expert play.
/// </summary>
internal static class TichuPlanSequenceEvaluator
{
    public const int MaximumDeals = 12;
    private const int MaximumActions = 5_000;

    public static string? GetUnsupportedReason(RoundSnapshot snapshot) =>
        IsSupportedStart(snapshot)
            ? null
            : "ordered replay currently requires the pre-play state with fourteen cards in every hand";

    public static IReadOnlyList<TichuPlanSequenceEstimate> Evaluate(
        PlayerSeat callerSeat,
        RoundSnapshot initialSnapshot,
        PolicyRoundContext callerContext,
        IReadOnlyList<Card[][]> sampledHands,
        IReadOnlyList<CardCombination[]> rankedPlans)
    {
        if (!IsSupportedStart(initialSnapshot))
        {
            return Array.Empty<TichuPlanSequenceEstimate>();
        }

        var dealCount = Math.Min(MaximumDeals, sampledHands.Count);
        var estimates = new List<TichuPlanSequenceEstimate>();
        var partnerModels = new[]
        {
            (Name: "random partner", CallerSupport: false),
            (Name: "caller-support partner", CallerSupport: true)
        };
        foreach (var (plan, planIndex) in rankedPlans.Take(2).Select((plan, index) => (plan, index + 1)))
        {
            foreach (var ordering in CreateOrderings(plan))
            {
                foreach (var partnerModel in partnerModels)
                {
                    var actorFirstCount = 0;
                    var actorSecondCount = 0;
                    var teamOneTwoCount = 0;
                    var teamPointMargin = 0L;
                    for (var sampleIndex = 0; sampleIndex < dealCount; sampleIndex++)
                    {
                        var seed = CreateSimulationSeed(callerSeat, sampleIndex);
                        var outcome = SimulateDeal(
                            callerSeat,
                            callerContext,
                            ordering.Combinations,
                            sampledHands[sampleIndex],
                            seed,
                            partnerModel.CallerSupport);
                        if (outcome.ActorFinishPlace == 1)
                        {
                            actorFirstCount++;
                        }

                        if (outcome.ActorFinishPlace == 2)
                        {
                            actorSecondCount++;
                        }

                        if (outcome.TeamFinishedOneTwo)
                        {
                            teamOneTwoCount++;
                        }

                        teamPointMargin += outcome.TeamPointMargin;
                    }

                    estimates.Add(new TichuPlanSequenceEstimate(
                        planIndex,
                        ordering.Name,
                        partnerModel.Name,
                        Array.AsReadOnly(ordering.Combinations.ToArray()),
                        dealCount,
                        actorFirstCount / (double)dealCount,
                        actorSecondCount / (double)dealCount,
                        teamOneTwoCount / (double)dealCount,
                        teamPointMargin / (double)dealCount));
                }
            }
        }

        return Array.AsReadOnly(estimates.ToArray());
    }

    private static bool IsSupportedStart(RoundSnapshot snapshot) =>
        snapshot.Trick.CurrentTop is null &&
        snapshot.Trick.Plays.Count == 0 &&
        snapshot.HandSizes.Count == 4 &&
        snapshot.HandSizes.All(size => size == 14) &&
        snapshot.PlayedCardsBySeat.Count == 4 &&
        snapshot.PlayedCardsBySeat.All(cards => cards.Count == 0);

    private static IReadOnlyList<NamedOrdering> CreateOrderings(CardCombination[] plan)
    {
        var orderings = new List<NamedOrdering>();
        Add("partition order", plan);
        Add("low lead strength first", plan.OrderBy(LeadStrength).ThenBy(CombinationSortKey));
        Add("King/Phoenix, low singles, Dragon", plan.OrderBy(PressureSequencePriority).ThenBy(CombinationSortKey));
        Add("high lead strength first", plan.OrderByDescending(LeadStrength).ThenBy(CombinationSortKey));
        return Array.AsReadOnly(orderings.ToArray());

        void Add(string name, IEnumerable<CardCombination> combinations)
        {
            var ordered = combinations.ToArray();
            var signature = string.Join("|", ordered.Select(CombinationSignature));
            if (orderings.All(existing => existing.Signature != signature))
            {
                orderings.Add(new NamedOrdering(name, ordered, signature));
            }
        }
    }

    private static int PressureSequencePriority(CardCombination combination)
    {
        if (combination is { Type: CombinationType.Single } && combination.Cards[0].Rank == CardRank.King)
        {
            return 0;
        }

        if (combination is { Type: CombinationType.Single } && combination.Cards[0].Rank == CardRank.Phoenix)
        {
            return 1;
        }

        if (combination is { Type: CombinationType.Single } single && IsLowSingle(single.Cards[0]))
        {
            return 2;
        }

        if (combination.Type != CombinationType.Single)
        {
            return 3;
        }

        if (combination.Cards[0].Rank == CardRank.Dragon)
        {
            return 5;
        }

        return 4;
    }

    private static decimal LeadStrength(CardCombination combination) => combination.Strength;

    private static string CombinationSortKey(CardCombination combination) =>
        $"{(int)combination.Type:D2}:{combination.Strength.ToString("F3", CultureInfo.InvariantCulture)}:{CombinationSignature(combination)}";

    private static string CombinationSignature(CardCombination combination) =>
        $"{(int)combination.Type}:{combination.PhoenixAsRank}:" +
        string.Join(",", combination.Cards.OrderBy(CardSortKey).Select(CardSortKey));

    private static bool IsLowSingle(Card card) =>
        card.Rank == CardRank.MahJong || card.Rank is >= CardRank.Two and <= CardRank.Ten;

    private static SequenceOutcome SimulateDeal(
        PlayerSeat callerSeat,
        PolicyRoundContext callerContext,
        CardCombination[] plannedOrder,
        Card[][] sampledHands,
        int seed,
        bool useCallerSupportPartner)
    {
        var hands = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => (IEnumerable<Card>)sampledHands[seat.ToIndex()]);
        var openingSeat = Enum.GetValues<PlayerSeat>()
            .Single(seat => sampledHands[seat.ToIndex()].Any(card => card.Rank == CardRank.MahJong));
        var round = new RoundState(openingSeat, hands);
        var supportSeat = callerSeat.Partner();
        var callerPassedCardToPartner = callerContext.PassSelection.ToPartner;
        var policies = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => seat == callerSeat
                ? (ITichuPolicy)new PlannedSequencePolicy(callerSeat, plannedOrder, callerContext, unchecked(seed ^ 0x45D9F3B))
                : useCallerSupportPartner && seat == supportSeat
                    ? new CallerSupportPolicy(
                        supportSeat,
                        callerSeat,
                        callerPassedCardToPartner,
                        new Random(unchecked(seed + (seat.ToIndex() + 1) * 104_729)))
                    : new RandomBaselinePolicy(new Random(unchecked(seed + (seat.ToIndex() + 1) * 104_729))));

        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var policyContext = seat == callerSeat
                ? callerContext
                : useCallerSupportPartner && seat == supportSeat
                    ? CreateSupportContext(seat, sampledHands[seat.ToIndex()], callerPassedCardToPartner)
                : CreateBaselineContext(seat, sampledHands[seat.ToIndex()]);
            policies[seat].BeginRound(policyContext);
        }

        for (var actionCount = 0; actionCount < MaximumActions; actionCount++)
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
                    CollectTrick(round, policies);
                }

                break;
            }

            if (trick.CurrentTop is not null && TryOutOfTurnBomb(round, snapshot, policies))
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
                    CollectTrick(round, policies);
                }

                break;
            }

            if (trick.CanCollect)
            {
                CollectTrick(round, policies);
                continue;
            }

            if (trick.CurrentTurnSeat is not { } seat)
            {
                throw new InvalidOperationException("Sequence replay found no active seat for the current trick.");
            }

            var hand = round.HandFor(seat);
            var move = policies[seat].ChooseMove(seat, hand, snapshot);
            var result = move.IsPass
                ? round.TryPass(seat)
                : round.TryPlay(seat, move.Combination!, move.WishForMahJong);
            if (!result.Accepted)
            {
                throw new InvalidOperationException($"Sequence replay rejected the move by {seat}: {result.Status}.");
            }
        }

        var finalTrick = round.Snapshot.Trick;
        if (!finalTrick.IsRoundEnded)
        {
            throw new InvalidOperationException("Sequence replay exceeded its action limit.");
        }

        if (!finalTrick.IsDoubleVictory && finalTrick.Plays.Count > 0)
        {
            CollectTrick(round, policies);
        }

        var finishOrder = finalTrick.FinishOrder;
        var actorIndex = finishOrder.ToList().IndexOf(callerSeat);
        var teamOneTwo = finishOrder.Count >= 2 && finishOrder[0].Partner() == finishOrder[1];
        var score = round.ScoreRound(new Dictionary<PlayerSeat, TichuCall>
        {
            [callerSeat] = TichuCall.Small
        });
        var actorTeamPoints = callerSeat is PlayerSeat.Bottom or PlayerSeat.Top
            ? score.BottomTopTotal
            : score.RightLeftTotal;
        var opponentTeamPoints = callerSeat is PlayerSeat.Bottom or PlayerSeat.Top
            ? score.RightLeftTotal
            : score.BottomTopTotal;

        return new SequenceOutcome(
            actorIndex < 0 ? 0 : actorIndex + 1,
            teamOneTwo,
            actorTeamPoints - opponentTeamPoints);
    }

    private static PolicyRoundContext CreateBaselineContext(PlayerSeat seat, IReadOnlyList<Card> hand) =>
        new(seat, new CardPassSelection(hand[0], hand[1], hand[2]));

    private static PolicyRoundContext CreateSupportContext(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        Card cardReceivedFromCaller) =>
        new(
            seat,
            new CardPassSelection(hand[0], hand[1], hand[2]),
            ReceivedFromPartner: cardReceivedFromCaller);

    private static bool TryOutOfTurnBomb(
        RoundState round,
        RoundSnapshot snapshot,
        IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies)
    {
        var trick = snapshot.Trick;
        if (trick.CurrentTop is null || trick.CurrentTopSeat is null)
        {
            return false;
        }

        var firstCandidate = trick.CurrentTopSeat.Value.ToTheRight();
        for (var offset = 0; offset < 4; offset++)
        {
            var seat = (PlayerSeat)(((int)firstCandidate + offset) % 4);
            if (!trick.ActiveSeats.Contains(seat) || trick.CurrentTurnSeat == seat)
            {
                continue;
            }

            var hand = round.HandFor(seat);
            var bombs = MoveGenerator.Generate(hand, trick.CurrentTop, trick.PendingWish, isOutOfTurn: true);
            var chosen = policies[seat].ChooseOutOfTurnBomb(seat, hand, snapshot, bombs);
            if (chosen is null)
            {
                continue;
            }

            var result = round.TryBomb(seat, chosen.Combination!);
            if (!result.Accepted)
            {
                throw new InvalidOperationException($"Sequence replay rejected the bomb by {seat}: {result.Status}.");
            }

            return true;
        }

        return false;
    }

    private static void CollectTrick(RoundState round, IReadOnlyDictionary<PlayerSeat, ITichuPolicy> policies)
    {
        var trick = round.Snapshot.Trick;
        var isDragon = trick.CurrentTop is { Type: CombinationType.Single } top && top.Cards[0].Rank == CardRank.Dragon;
        var recipient = isDragon
            ? policies[trick.CurrentTopSeat!.Value].ChooseDragonRecipient(trick.CurrentTopSeat.Value)
            : (PlayerSeat?)null;
        var result = round.TryCollect(recipient);
        if (!result.Accepted)
        {
            throw new InvalidOperationException($"Sequence replay could not collect a trick: {result.Status}.");
        }
    }

    private static PolicyRoundContext CreateBaselineContext(PlayerSeat seat, Card[] hand) =>
        CreateBaselineContext(seat, (IReadOnlyList<Card>)hand);

    private static int CreateSimulationSeed(PlayerSeat seat, int sampleIndex) =>
        unchecked(0x2D2816FE + seat.ToIndex() * 1_000_003 + sampleIndex * 83_492_791);

    private static int CardSortKey(Card card) => (int)card.Rank * 4 + (int)(card.Suit ?? CardSuit.Jade);

    private sealed record NamedOrdering(string Name, CardCombination[] Combinations, string Signature);

    private sealed record SequenceOutcome(int ActorFinishPlace, bool TeamFinishedOneTwo, int TeamPointMargin);

    private sealed class CallerSupportPolicy : ITichuPolicy
    {
        private readonly PlayerSeat _supportSeat;
        private readonly PlayerSeat _callerSeat;
        private readonly Card _callerPassedCard;
        private readonly RandomBaselinePolicy _fallback;

        public CallerSupportPolicy(
            PlayerSeat supportSeat,
            PlayerSeat callerSeat,
            Card callerPassedCard,
            Random random)
        {
            _supportSeat = supportSeat;
            _callerSeat = callerSeat;
            _callerPassedCard = callerPassedCard;
            _fallback = new RandomBaselinePolicy(random);
        }

        public void BeginRound(PolicyRoundContext context) => _fallback.BeginRound(context);

        public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context) => false;

        public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context) => false;

        public CardPassSelection ChoosePass(IReadOnlyList<Card> hand) => _fallback.ChoosePass(hand);

        public LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
        {
            if (seat != _supportSeat)
            {
                throw new ArgumentException("The support policy can only act for the caller's partner.", nameof(seat));
            }

            var trick = snapshot.Trick;
            var legalMoves = MoveGenerator.Generate(hand, trick.CurrentTop, trick.PendingWish);
            if (legalMoves.Count == 0)
            {
                throw new InvalidOperationException("No legal move is available to the support policy.");
            }

            var callerHandSize = snapshot.HandSizes[_callerSeat.ToIndex()];
            if (trick.CurrentTop is null)
            {
                if (callerHandSize is > 0 and <= 2)
                {
                    var dog = legalMoves.FirstOrDefault(move => move.Combination is
                        { Type: CombinationType.Single, Cards.Count: 1 } combination &&
                        combination.Cards[0].Rank == CardRank.Dog);
                    if (dog is not null)
                    {
                        return dog;
                    }
                }

                if (callerHandSize is > 0 and <= 4 && OwnsSeveralLikelyLeads(hand))
                {
                    var weakSingle = legalMoves
                        .Where(IsLowOrdinarySingle)
                        .OrderBy(move => move.Combination!.Strength)
                        .ThenBy(move => CardScoring.Total(move.Combination!.Cards))
                        .FirstOrDefault();
                    if (weakSingle is not null)
                    {
                        return weakSingle;
                    }
                }

                return _fallback.ChooseMove(legalMoves);
            }

            if (trick.CurrentTopSeat == _callerSeat)
            {
                return legalMoves.FirstOrDefault(move => move.IsPass) ?? _fallback.ChooseMove(legalMoves);
            }

            if (trick.CurrentTopSeat == _supportSeat)
            {
                return legalMoves.FirstOrDefault(move => move.IsPass) ?? _fallback.ChooseMove(legalMoves);
            }

            if (callerHandSize > 0 && trick.CurrentTop is { Type: CombinationType.Single } topSingle)
            {
                var topCard = topSingle.Cards[0];
                var opponentBeforeSupport = trick.CurrentTopSeat == _supportSeat.ToTheLeft();
                if (opponentBeforeSupport &&
                    topCard.Rank is >= CardRank.Queen and <= CardRank.Ace &&
                    topSingle.Strength < (int)CardRank.Ace)
                {
                    var ace = SelectAceResponse(legalMoves);
                    if (ace is not null)
                    {
                        return ace;
                    }
                }

                if (trick.PassedSeats.Contains(_callerSeat) && topCard.Rank <= CardRank.King)
                {
                    var affordableResponse = legalMoves
                        .Where(move => IsOrdinarySingleResponse(move, topSingle.Strength, CardRank.King))
                        .OrderBy(move => move.Combination!.Strength)
                        .ThenBy(move => CardScoring.Total(move.Combination!.Cards))
                        .FirstOrDefault();
                    if (affordableResponse is not null)
                    {
                        return affordableResponse;
                    }
                }

                if (topCard.Rank is >= CardRank.Two and <= CardRank.Ten)
                {
                    var shedLoser = legalMoves
                        .Where(move => IsOrdinarySingleResponse(move, topSingle.Strength, CardRank.Ten))
                        .OrderBy(move => move.Combination!.Strength)
                        .ThenBy(move => CardScoring.Total(move.Combination!.Cards))
                        .FirstOrDefault();
                    if (shedLoser is not null)
                    {
                        return shedLoser;
                    }
                }
            }

            if (callerHandSize is > 0 and <= 2 && trick.CurrentTop?.Type == CombinationType.Pair)
            {
                var lowestPair = legalMoves
                    .Where(move => move.Combination?.Type == CombinationType.Pair)
                    .OrderBy(move => move.Combination!.Strength)
                    .FirstOrDefault();
                if (lowestPair is not null)
                {
                    return lowestPair;
                }
            }

            return legalMoves.FirstOrDefault(move => move.IsPass) ?? _fallback.ChooseMove(legalMoves);
        }

        public LegalMove? ChooseOutOfTurnBomb(
            PlayerSeat seat,
            IReadOnlyList<Card> hand,
            RoundSnapshot snapshot,
            IReadOnlyList<LegalMove> bombs)
        {
            var topSeat = snapshot.Trick.CurrentTopSeat;
            if (seat != _supportSeat || topSeat is null || topSeat == _supportSeat ||
                topSeat == _callerSeat || snapshot.HandSizes[topSeat.Value.ToIndex()] > 2)
            {
                return null;
            }

            return bombs.OrderBy(move => move.Combination!.Strength).FirstOrDefault();
        }

        public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner) =>
            _fallback.ChooseDragonRecipient(dragonWinner);

        private LegalMove? SelectAceResponse(IReadOnlyList<LegalMove> moves) => moves
            .Where(move => move.Combination is { Type: CombinationType.Single, Cards.Count: 1 } combination &&
                           combination.Cards[0].Rank == CardRank.Ace)
            .OrderBy(move => move.Combination!.Cards[0] == _callerPassedCard)
            .FirstOrDefault();

        private static bool IsLowOrdinarySingle(LegalMove move) =>
            move.Combination is { Type: CombinationType.Single, Cards.Count: 1 } combination &&
            combination.Cards[0].Rank is >= CardRank.Two and <= CardRank.Ten;

        private static bool IsOrdinarySingleResponse(
            LegalMove move,
            decimal currentStrength,
            CardRank maximumRank) =>
            move.Combination is { Type: CombinationType.Single, Cards.Count: 1 } combination &&
            combination.Cards[0].Rank is >= CardRank.Two &&
            combination.Cards[0].Rank <= maximumRank &&
            combination.Strength > currentStrength;

        private static bool OwnsSeveralLikelyLeads(IReadOnlyList<Card> hand) =>
            hand.Count(card => card.Rank is CardRank.Ace or CardRank.Phoenix or CardRank.Dragon) >= 2 ||
            hand.Any(card => card.Rank == CardRank.Dog) &&
            hand.Count(card => card.Rank is CardRank.Ace or CardRank.Phoenix or CardRank.Dragon) >= 1;
    }

    private sealed class PlannedSequencePolicy : ITichuPolicy
    {
        private readonly PlayerSeat _callerSeat;
        private readonly CardCombination[] _plannedOrder;
        private readonly PolicyRoundContext _roundContext;
        private readonly bool[] _usedPlans;
        private readonly Random _random;

        public PlannedSequencePolicy(
            PlayerSeat callerSeat,
            CardCombination[] plannedOrder,
            PolicyRoundContext roundContext,
            int randomSeed)
        {
            _callerSeat = callerSeat;
            _plannedOrder = plannedOrder.ToArray();
            _roundContext = roundContext;
            _usedPlans = new bool[plannedOrder.Length];
            _random = new Random(randomSeed);
        }

        public void BeginRound(PolicyRoundContext context)
        {
            if (context.Seat != _callerSeat)
            {
                return;
            }
        }

        public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context) => false;

        public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context) => false;

        public CardPassSelection ChoosePass(IReadOnlyList<Card> hand) =>
            new(hand[0], hand[1], hand[2]);

        public LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
        {
            if (seat != _callerSeat)
            {
                throw new ArgumentException("The planned policy can only act for its caller.", nameof(seat));
            }

            var moves = MoveGenerator.Generate(hand, snapshot.Trick.CurrentTop, snapshot.Trick.PendingWish);
            if (moves.Count == 0)
            {
                throw new InvalidOperationException("No legal move is available during sequence replay.");
            }

            var matching = GetMatchingPlanMoves(moves, hand);
            var chosen = SelectPlanMove(matching, snapshot.Trick, hand.Count);
            if (chosen is null)
            {
                chosen = SelectFallbackMove(moves, snapshot.Trick, _callerSeat, hand.Count);
            }

            chosen = PreferPassedCardIdentity(chosen, moves, hand);
            var selectedPlan = FindPlanIndex(chosen, hand);
            if (selectedPlan >= 0)
            {
                _usedPlans[selectedPlan] = true;
            }
            else if (chosen.Combination is { } combination)
            {
                for (var index = 0; index < _plannedOrder.Length; index++)
                {
                    if (combination.Cards.Any(_plannedOrder[index].Cards.Contains))
                    {
                        _usedPlans[index] = true;
                    }
                }
            }

            return chosen;
        }

        public LegalMove? ChooseOutOfTurnBomb(
            PlayerSeat seat,
            IReadOnlyList<Card> hand,
            RoundSnapshot snapshot,
            IReadOnlyList<LegalMove> bombs)
        {
            if (seat != _callerSeat)
            {
                throw new ArgumentException("The planned policy can only act for its caller.", nameof(seat));
            }

            var matching = GetMatchingPlanMoves(bombs, hand)
                .Where(item => item.Move.Combination?.IsBomb == true)
                .OrderBy(item => item.Move.Combination!.Strength)
                .ToArray();
            if (matching.Length == 0)
            {
                return null;
            }

            var selected = PreferPassedCardIdentity(matching[0].Move, bombs, hand);
            var selectedPlan = FindPlanIndex(selected, hand);
            if (selectedPlan >= 0)
            {
                _usedPlans[selectedPlan] = true;
            }

            return selected;
        }

        public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner)
        {
            var opponents = Enum.GetValues<PlayerSeat>()
                .Where(other => other != dragonWinner && other != dragonWinner.Partner())
                .ToArray();
            return opponents[_random.Next(opponents.Length)];
        }

        private IReadOnlyList<PlanMove> GetMatchingPlanMoves(
            IReadOnlyList<LegalMove> moves,
            IReadOnlyList<Card> hand)
        {
            var matching = new List<PlanMove>();
            for (var index = 0; index < _plannedOrder.Length; index++)
            {
                if (_usedPlans[index])
                {
                    continue;
                }

                var planned = _plannedOrder[index];
                foreach (var move in moves)
                {
                    if (move.Combination is { } combination && SamePlannedCombination(planned, combination))
                    {
                        matching.Add(new PlanMove(index, move));
                    }
                }
            }

            return Array.AsReadOnly(matching.ToArray());
        }

        private LegalMove? SelectPlanMove(IReadOnlyList<PlanMove> matching, TrickSnapshot trick, int handSize)
        {
            if (matching.Count == 0)
            {
                return null;
            }

            if (trick.CurrentTop is null)
            {
                return matching.OrderBy(item => item.PlanIndex).First().Move;
            }

            var topSeat = trick.CurrentTopSeat;
            if (topSeat == _callerSeat.Partner())
            {
                return matching
                    .Where(item => item.Move.Combination!.CardCount == handSize)
                    .OrderBy(item => item.PlanIndex)
                    .Select(item => item.Move)
                    .FirstOrDefault();
            }

            return matching
                .OrderBy(item => item.Move.Combination!.IsBomb)
                .ThenBy(item => item.Move.Combination!.Strength)
                .ThenBy(item => CardScoring.Total(item.Move.Combination!.Cards))
                .ThenBy(item => item.PlanIndex)
                .Select(item => item.Move)
                .FirstOrDefault();
        }

        private static LegalMove SelectFallbackMove(
            IReadOnlyList<LegalMove> moves,
            TrickSnapshot trick,
            PlayerSeat callerSeat,
            int handSize)
        {
            if (trick.CurrentTopSeat == callerSeat.Partner())
            {
                var goOut = moves.FirstOrDefault(move => move.Combination?.CardCount == handSize);
                if (goOut is not null)
                {
                    return goOut;
                }

                var pass = moves.FirstOrDefault(move => move.IsPass);
                if (pass is not null)
                {
                    return pass;
                }
            }

            return moves
                .OrderBy(move => move.IsPass ? 1 : 0)
                .ThenBy(move => move.Combination?.IsBomb == true)
                .ThenBy(move => move.Combination?.Strength ?? decimal.MaxValue)
                .ThenBy(move => move.Combination is null ? int.MaxValue : CardScoring.Total(move.Combination.Cards))
                .First();
        }

        private LegalMove PreferPassedCardIdentity(
            LegalMove move,
            IReadOnlyList<LegalMove> legalMoves,
            IReadOnlyList<Card> hand)
        {
            foreach (var card in new[] { _roundContext.ReceivedFromPartner }.OfType<Card>())
            {
                if (hand.Contains(card) && move.Combination?.Cards.Contains(card) == true)
                {
                    var alternative = legalMoves.FirstOrDefault(candidate =>
                        candidate.Combination is not null &&
                        FindPlanIndex(candidate, hand) >= 0 &&
                        ReplacesCardWithEquivalentCard(move, candidate, card));
                    if (alternative is not null)
                    {
                        move = alternative;
                    }
                }
            }

            foreach (var card in new[]
                     {
                         _roundContext.ReceivedFromRightOpponent,
                         _roundContext.ReceivedFromLeftOpponent
                     }.OfType<Card>().Distinct())
            {
                if (hand.Contains(card) && move.Combination?.Cards.Contains(card) != true)
                {
                    var alternative = legalMoves.FirstOrDefault(candidate =>
                        candidate.Combination is not null &&
                        FindPlanIndex(candidate, hand) >= 0 &&
                        ReplacesCardWithEquivalentCard(candidate, move, card));
                    if (alternative is not null)
                    {
                        move = alternative;
                    }
                }
            }

            return move;
        }

        private int FindPlanIndex(LegalMove move, IReadOnlyList<Card> hand)
        {
            if (move.Combination is not { } combination)
            {
                return -1;
            }

            for (var index = 0; index < _plannedOrder.Length; index++)
            {
                if (!_usedPlans[index] && SamePlannedCombination(_plannedOrder[index], combination))
                {
                    return index;
                }
            }

            return -1;
        }

        private static bool SamePlannedCombination(CardCombination planned, CardCombination candidate) =>
            planned.Type == candidate.Type &&
            planned.PhoenixAsRank == candidate.PhoenixAsRank &&
            planned.Cards.Count == candidate.Cards.Count &&
            planned.Cards.All(candidate.Cards.Contains);

        private static bool ReplacesCardWithEquivalentCard(LegalMove move, LegalMove alternative, Card card)
        {
            if (move.Combination is not { } combination ||
                alternative.Combination is not { } alternativeCombination ||
                !combination.Cards.Contains(card) ||
                alternativeCombination.Cards.Contains(card) ||
                combination.Type != alternativeCombination.Type ||
                combination.CardCount != alternativeCombination.CardCount ||
                combination.Strength != alternativeCombination.Strength ||
                combination.PhoenixAsRank != alternativeCombination.PhoenixAsRank ||
                move.WishForMahJong != alternative.WishForMahJong)
            {
                return false;
            }

            var cardsWithoutCard = combination.Cards.Where(other => other != card).ToHashSet();
            return alternativeCombination.Cards.Any(replacement =>
                replacement.Rank == card.Rank &&
                cardsWithoutCard.SetEquals(alternativeCombination.Cards.Where(other => other != replacement)));
        }

        private sealed record PlanMove(int PlanIndex, LegalMove Move);
    }
}

public sealed record TichuPlanSequenceEstimate(
    int PartitionIndex,
    string OrderingName,
    string PartnerModel,
    IReadOnlyList<CardCombination> PlannedOrder,
    int SampleCount,
    double CallerFinishesFirstProbability,
    double CallerFinishesSecondProbability,
    double TeamFinishesOneTwoProbability,
    double AverageTeamPointMargin);
