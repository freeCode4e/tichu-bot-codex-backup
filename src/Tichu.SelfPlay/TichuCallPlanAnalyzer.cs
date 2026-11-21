using System.Numerics;
using System.Text;
using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>
/// Diagnostic analysis for Tichu calls. It compares hand partitions and samples
/// hidden hands consistent with the acting player's own passes and public plays.
/// It does not change the policy's call decision.
/// </summary>
public static class TichuCallPlanAnalyzer
{
    public const int DefaultSampleCount = 128;

    public static TichuCallPlanAnalysis Analyze(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        PolicyRoundContext roundContext,
        int sampleCount = DefaultSampleCount,
        int additionalPlayLimit = 1,
        int maximumPlans = 128)
    {
        seat.ToIndex();
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(roundContext);
        if (roundContext.Seat != seat)
        {
            throw new ArgumentException("The round context must belong to the acting seat.", nameof(roundContext));
        }

        if (sampleCount <= 0 || additionalPlayLimit < 0 || maximumPlans <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "Analysis limits must be positive (additional plays may be zero).");
        }

        var handCards = hand.ToArray();
        var allLeadMoves = MoveGenerator.Generate(handCards)
            .Where(move => move.Combination is not null && move.WishForMahJong is null)
            .Select(move => move.Combination!)
            .DistinctBy(combination => CombinationKey.Create(combination, handCards))
            .Select(combination => new LeadOption(combination, CreateMask(combination, handCards)))
            .ToArray();
        if (allLeadMoves.Length == 0)
        {
            throw new InvalidOperationException("The hand has no legal leading combinations.");
        }

        var minimumPlayCounts = FindMinimumPlayCounts(handCards.Length, allLeadMoves);
        var minimumPlayCount = minimumPlayCounts[^1];
        var maximumPlayCount = Math.Min(handCards.Length, minimumPlayCount + additionalPlayLimit);
        var candidatePlans = EnumeratePlans(
            handCards.Length,
            allLeadMoves,
            minimumPlayCounts,
            maximumPlayCount,
            maximumPlans);
        var sampling = CreateSamplingContext(seat, handCards, snapshot, roundContext);
        var samples = CreateHiddenHandSamples(seat, handCards, sampling, sampleCount);
        var singleLeads = allLeadMoves
            .Where(option => option.Combination.Type == CombinationType.Single)
            .Select(option => option.Combination)
            .ToArray();
        var singleRiskByKey = EstimateOpponentHitRisks(seat, singleLeads, handCards, samples, sampleCount);
        var shortlistedPlans = candidatePlans
            .Select(plan => new RankedPlan(
                plan,
                CreatePlanCandidate(plan, singleRiskByKey, handCards, unknownCombinationRisk: 0.5)))
            .OrderByDescending(item => item.Estimate.LikelyWinnerLeadCount >= item.Estimate.LowSingleCount)
            .ThenByDescending(item => item.Estimate.ExpectedUnopposedLeads)
            .ThenBy(item => item.Estimate.PlayCount)
            .ThenBy(item => item.Estimate.LowSingleCount)
            .Take(12)
            .ToArray();
        var leadsByKey = shortlistedPlans
            .SelectMany(item => item.Combinations)
            .Concat(singleLeads)
            .DistinctBy(combination => CombinationKey.Create(combination, handCards))
            .ToArray();
        var riskByKey = EstimateOpponentHitRisks(seat, leadsByKey, handCards, samples, sampleCount);
        var leadEstimates = leadsByKey
            .Select(combination => new TichuLeadEstimate(
                combination,
                riskByKey[CombinationKey.Create(combination, handCards)],
                CountSingleResponseRanks(combination)))
            .OrderBy(estimate => estimate.OpponentCanBeatProbability)
            .ThenBy(estimate => estimate.Combination.CardCount)
            .ToArray();
        var knownPassedCards = Enum.GetValues<PlayerSeat>().ToDictionary(
            otherSeat => otherSeat,
            otherSeat => (IReadOnlyList<Card>)Array.AsReadOnly(
                sampling.KnownPassedCardsBySeat[otherSeat.ToIndex()].ToArray()));

        var evaluatedPlans = shortlistedPlans
            .Select(item => CreatePlanCandidate(item.Combinations, riskByKey, handCards, unknownCombinationRisk: 0.5))
            .OrderByDescending(plan => plan.LikelyWinnerLeadCount >= plan.LowSingleCount)
            .ThenByDescending(plan => plan.ExpectedUnopposedLeads)
            .ThenBy(plan => plan.PlayCount)
            .ThenBy(plan => plan.LowSingleCount)
            .Take(12)
            .ToArray();
        var sequenceEstimates = TichuPlanSequenceEvaluator.Evaluate(
            seat,
            snapshot,
            roundContext,
            samples,
            evaluatedPlans.Take(2)
                .Select(plan => plan.Plays.Select(play => play.Combination).ToArray())
                .ToArray());

        return new TichuCallPlanAnalysis(
            seat,
            minimumPlayCount,
            sampleCount,
            sampling.UnknownCardCount,
            knownPassedCards,
            roundContext.ReceivedFromPartner,
            roundContext.ReceivedFromRightOpponent,
            roundContext.ReceivedFromLeftOpponent,
            Array.AsReadOnly(leadEstimates),
            evaluatedPlans)
        {
            CandidateSearchWasLimited = candidatePlans.Count >= maximumPlans,
            SequenceEstimates = sequenceEstimates,
            SequenceReplaySkippedReason = sequenceEstimates.Count == 0
                ? TichuPlanSequenceEvaluator.GetUnsupportedReason(snapshot)
                : null
        };
    }

    private static int[] FindMinimumPlayCounts(int handSize, IReadOnlyList<LeadOption> options)
    {
        var stateCount = 1 << handSize;
        var minimumPlays = Enumerable.Repeat(int.MaxValue, stateCount).ToArray();
        minimumPlays[0] = 0;
        var optionsByCard = Enumerable.Range(0, handSize)
            .Select(index => options.Where(option => (option.Mask & (1 << index)) != 0).ToArray())
            .ToArray();

        for (var remainingMask = 1; remainingMask < stateCount; remainingMask++)
        {
            var anchor = BitOperations.TrailingZeroCount((uint)remainingMask);
            foreach (var option in optionsByCard[anchor])
            {
                if ((option.Mask & remainingMask) != option.Mask)
                {
                    continue;
                }

                var remainder = remainingMask ^ option.Mask;
                minimumPlays[remainingMask] = Math.Min(
                    minimumPlays[remainingMask],
                    minimumPlays[remainder] + 1);
            }
        }

        return minimumPlays;
    }

    private static IReadOnlyList<CardCombination[]> EnumeratePlans(
        int handSize,
        IReadOnlyList<LeadOption> options,
        IReadOnlyList<int> minimumPlayCounts,
        int maximumPlayCount,
        int maximumPlans)
    {
        var optionsByCard = Enumerable.Range(0, handSize)
            .Select(index => options
                .Where(option => (option.Mask & (1 << index)) != 0)
                .OrderBy(option => option.Combination.CardCount)
                .ThenBy(option => option.Combination.Type)
                .ThenBy(option => option.Combination.Strength)
                .ToArray())
            .ToArray();
        var plans = new List<CardCombination[]>(Math.Min(maximumPlans, 128));
        var currentPlan = new List<CardCombination>(maximumPlayCount);
        Visit((1 << handSize) - 1);
        return Array.AsReadOnly(plans.ToArray());

        void Visit(int remainingMask)
        {
            if (plans.Count >= maximumPlans)
            {
                return;
            }

            if (remainingMask == 0)
            {
                plans.Add(currentPlan.ToArray());
                return;
            }

            if (currentPlan.Count + minimumPlayCounts[remainingMask] > maximumPlayCount)
            {
                return;
            }

            var anchor = BitOperations.TrailingZeroCount((uint)remainingMask);
            foreach (var option in optionsByCard[anchor])
            {
                if ((option.Mask & remainingMask) != option.Mask || currentPlan.Count >= maximumPlayCount)
                {
                    continue;
                }

                currentPlan.Add(option.Combination);
                Visit(remainingMask ^ option.Mask);
                currentPlan.RemoveAt(currentPlan.Count - 1);
                if (plans.Count >= maximumPlans)
                {
                    return;
                }
            }
        }
    }

    private static DealSamplingContext CreateSamplingContext(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        PolicyRoundContext roundContext)
    {
        if (snapshot.HandSizes.Count != 4 || snapshot.PlayedCardsBySeat.Count != 4 ||
            snapshot.HandSizes[seat.ToIndex()] != hand.Count)
        {
            throw new ArgumentException("The snapshot must contain all four current hand sizes and played-card histories.", nameof(snapshot));
        }

        var playedCardsBySeat = snapshot.PlayedCardsBySeat.Select(cards => cards.ToArray()).ToArray();
        var playedCards = playedCardsBySeat.SelectMany(cards => cards).ToArray();
        var fixedCardsBySeat = Enumerable.Range(0, 4).Select(_ => new List<Card>()).ToArray();
        var knownPassedCardsBySeat = Enumerable.Range(0, 4).Select(_ => new List<Card>()).ToArray();
        var playedSet = playedCards.ToHashSet();
        var knownOutgoingPasses = new[]
        {
            (Recipient: seat.ToTheRight(), Card: roundContext.PassSelection.ToRight),
            (Recipient: seat.Partner(), Card: roundContext.PassSelection.ToPartner),
            (Recipient: seat.ToTheLeft(), Card: roundContext.PassSelection.ToLeft)
        };

        foreach (var (recipient, card) in knownOutgoingPasses)
        {
            if (!card.IsValid)
            {
                throw new ArgumentException("The round context contains an invalid outgoing pass card.", nameof(roundContext));
            }

            if (playedSet.Contains(card))
            {
                continue;
            }

            if (hand.Contains(card) || snapshot.HandSizes[recipient.ToIndex()] == 0)
            {
                throw new ArgumentException("An unplayed outgoing pass must still be in its recipient's hand.", nameof(roundContext));
            }

            fixedCardsBySeat[recipient.ToIndex()].Add(card);
            knownPassedCardsBySeat[recipient.ToIndex()].Add(card);
        }

        var allKnownCards = hand.Concat(playedCards).Concat(fixedCardsBySeat.SelectMany(cards => cards)).ToArray();
        if (allKnownCards.Any(card => !card.IsValid) || allKnownCards.Distinct().Count() != allKnownCards.Length)
        {
            throw new ArgumentException("The hand, public plays, and known outgoing passes must be distinct physical cards.");
        }

        var deck = TichuDeck.CreateStandard();
        var unknownCards = deck.Where(card => !allKnownCards.Contains(card)).ToArray();
        var openSlots = new int[4];
        foreach (var otherSeat in Enum.GetValues<PlayerSeat>())
        {
            if (otherSeat == seat)
            {
                continue;
            }

            openSlots[otherSeat.ToIndex()] = snapshot.HandSizes[otherSeat.ToIndex()] - fixedCardsBySeat[otherSeat.ToIndex()].Count;
            if (openSlots[otherSeat.ToIndex()] < 0)
            {
                throw new ArgumentException("Known passed cards exceed a recipient's current hand size.", nameof(snapshot));
            }
        }

        var expectedUnknownCount = openSlots.Sum();
        if (unknownCards.Length != expectedUnknownCount || hand.Count + playedCards.Length + fixedCardsBySeat.Sum(cards => cards.Count) + unknownCards.Length != TichuDeck.CardCount)
        {
            throw new ArgumentException("The snapshot does not describe a complete 56-card round state.", nameof(snapshot));
        }

        return new DealSamplingContext(fixedCardsBySeat, knownPassedCardsBySeat, unknownCards, openSlots);
    }

    private static IReadOnlyList<Card[][]> CreateHiddenHandSamples(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        DealSamplingContext sampling,
        int sampleCount)
    {
        var random = new Random(CreateStableSeed(seat, hand, sampling));
        var samples = new Card[sampleCount][][];
        for (var sampleIndex = 0; sampleIndex < sampleCount; sampleIndex++)
        {
            var shuffled = sampling.UnknownCards.ToArray();
            for (var index = shuffled.Length - 1; index > 0; index--)
            {
                var otherIndex = random.Next(index + 1);
                (shuffled[index], shuffled[otherIndex]) = (shuffled[otherIndex], shuffled[index]);
            }

            var sampleHands = new Card[4][];
            sampleHands[seat.ToIndex()] = hand.ToArray();
            var cursor = 0;
            foreach (var otherSeat in Enum.GetValues<PlayerSeat>())
            {
                if (otherSeat == seat)
                {
                    continue;
                }

                var handCards = sampling.FixedCardsBySeat[otherSeat.ToIndex()].ToList();
                var slots = sampling.OpenSlots[otherSeat.ToIndex()];
                handCards.AddRange(shuffled.Skip(cursor).Take(slots));
                cursor += slots;
                sampleHands[otherSeat.ToIndex()] = handCards.ToArray();
            }

            samples[sampleIndex] = sampleHands;
        }

        return Array.AsReadOnly(samples);
    }

    private static IReadOnlyDictionary<CombinationKey, double> EstimateOpponentHitRisks(
        PlayerSeat seat,
        IReadOnlyList<CardCombination> leads,
        IReadOnlyList<Card> hand,
        IReadOnlyList<Card[][]> samples,
        int sampleCount)
    {
        var hitCounts = leads.ToDictionary(combination => CombinationKey.Create(combination, hand), _ => 0);
        var opponents = Enum.GetValues<PlayerSeat>()
            .Where(otherSeat => otherSeat != seat && otherSeat != seat.Partner())
            .ToArray();

        foreach (var sample in samples)
        {
            foreach (var lead in leads)
            {
                var opponentsCanBeat = opponents.Any(opponentSeat =>
                    MoveGenerator.Generate(sample[opponentSeat.ToIndex()], lead)
                        .Any(response => response.Combination is not null));
                if (opponentsCanBeat)
                {
                    hitCounts[CombinationKey.Create(lead, hand)]++;
                }
            }
        }

        return hitCounts.ToDictionary(pair => pair.Key, pair => (pair.Value + 1.0) / (sampleCount + 2.0));
    }

    private static TichuPlanCandidate CreatePlanCandidate(
        IReadOnlyList<CardCombination> plan,
        IReadOnlyDictionary<CombinationKey, double> opponentHitRisks,
        IReadOnlyList<Card> hand,
        double unknownCombinationRisk)
    {
        var plays = plan.Select(combination => new TichuPlanPlayEstimate(
                combination,
                opponentHitRisks.GetValueOrDefault(CombinationKey.Create(combination, hand), unknownCombinationRisk),
                CountSingleResponseRanks(combination)))
            .ToArray();
        var lowSingles = plan.Count(combination => combination.Type == CombinationType.Single && IsLowSingle(combination.Cards[0]));
        var expectedOpponentHits = plays.Sum(play => play.OpponentCanBeatProbability);
        var expectedUnopposedLeads = plays.Length - expectedOpponentHits;

        return new TichuPlanCandidate(
            Array.AsReadOnly(plays),
            lowSingles,
            plan.Count(combination => combination.Type == CombinationType.Single && IsHighSingle(combination.Cards[0])),
            expectedUnopposedLeads,
            expectedOpponentHits,
            plays.Count(play => play.OpponentCanBeatProbability <= 0.25),
            plays.Count(play => play.OpponentCanBeatProbability >= 0.75));
    }

    private static bool IsLowSingle(Card card) =>
        card.Rank == CardRank.MahJong || card.Rank is >= CardRank.Two and <= CardRank.Ten;

    private static bool IsHighSingle(Card card) =>
        card.Rank is CardRank.King or CardRank.Ace or CardRank.Phoenix or CardRank.Dragon;

    private static int CountSingleResponseRanks(CardCombination combination)
    {
        if (combination.Type != CombinationType.Single)
        {
            return 0;
        }

        return TichuDeck.CreateStandard()
            .GroupBy(card => card.Rank)
            .Select(group => group.First())
            .Count(card =>
            {
                var possibleLead = CardCombination.ClassifyAll(new[] { card }).Single();
                return card != combination.Cards[0] && combination.Beats(possibleLead);
            });
    }

    private static int CreateStableSeed(PlayerSeat seat, IReadOnlyList<Card> hand, DealSamplingContext sampling)
    {
        var seed = 17 + seat.ToIndex();
        foreach (var card in hand.OrderBy(CardSortKey))
        {
            seed = unchecked(seed * 31 + CardSortKey(card));
        }

        for (var seatIndex = 0; seatIndex < sampling.FixedCardsBySeat.Length; seatIndex++)
        {
            foreach (var card in sampling.FixedCardsBySeat[seatIndex].OrderBy(CardSortKey))
            {
                seed = unchecked(seed * 31 + seatIndex * 257 + CardSortKey(card));
            }
        }

        foreach (var card in sampling.UnknownCards.OrderBy(CardSortKey))
        {
            seed = unchecked(seed * 31 + CardSortKey(card));
        }

        return seed;
    }

    private static int CardSortKey(Card card) => (int)card.Rank * 4 + (int)(card.Suit ?? CardSuit.Jade);

    private static int CreateMask(CardCombination combination, IReadOnlyList<Card> hand)
    {
        var mask = 0;
        foreach (var card in combination.Cards)
        {
            var index = IndexOf(hand, card);
            if (index < 0)
            {
                throw new ArgumentException("A leading combination must use cards from the supplied hand.", nameof(combination));
            }

            mask |= 1 << index;
        }

        return mask;
    }

    private static int IndexOf(IReadOnlyList<Card> cards, Card card)
    {
        for (var index = 0; index < cards.Count; index++)
        {
            if (cards[index] == card)
            {
                return index;
            }
        }

        return -1;
    }

    private sealed record LeadOption(CardCombination Combination, int Mask);

    private sealed record RankedPlan(CardCombination[] Combinations, TichuPlanCandidate Estimate);

    private sealed record DealSamplingContext(
        List<Card>[] FixedCardsBySeat,
        List<Card>[] KnownPassedCardsBySeat,
        Card[] UnknownCards,
        int[] OpenSlots)
    {
        public int UnknownCardCount => UnknownCards.Length;
    }

    private readonly record struct CombinationKey(
        int Mask,
        CombinationType Type,
        decimal Strength,
        CardRank? PhoenixAsRank)
    {
        public static CombinationKey Create(CardCombination combination, IReadOnlyList<Card> hand) =>
            new(CreateMask(combination, hand), combination.Type, combination.Strength, combination.PhoenixAsRank);
    }
}

public sealed record TichuCallPlanAnalysis(
    PlayerSeat Seat,
    int MinimumPlayCount,
    int SampleCount,
    int UnknownCardCount,
    IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> KnownPassedCardsBySeat,
    Card? ReceivedFromPartner,
    Card? ReceivedFromRightOpponent,
    Card? ReceivedFromLeftOpponent,
    IReadOnlyList<TichuLeadEstimate> LeadEstimates,
    IReadOnlyList<TichuPlanCandidate> CandidatePlans)
{
    public bool CandidateSearchWasLimited { get; init; }

    public IReadOnlyList<TichuPlanSequenceEstimate> SequenceEstimates { get; init; } =
        Array.Empty<TichuPlanSequenceEstimate>();

    public string? SequenceReplaySkippedReason { get; init; }

    public static string Format(TichuCallPlanAnalysis analysis)
    {
        ArgumentNullException.ThrowIfNull(analysis);
        var output = new StringBuilder();
        output.AppendLine($"Tichu plan analysis for {analysis.Seat}");
        output.AppendLine($"Minimum combinations to shed: {analysis.MinimumPlayCount}");
        output.AppendLine(
            $"Lead risks sampled across {analysis.SampleCount} unseen deals " +
            $"({analysis.UnknownCardCount} cards unknown to the actor).");
        output.AppendLine(
            $"Received: partner {FormatOptionalCard(analysis.ReceivedFromPartner)}, " +
            $"right opponent {FormatOptionalCard(analysis.ReceivedFromRightOpponent)}, " +
            $"left opponent {FormatOptionalCard(analysis.ReceivedFromLeftOpponent)}.");

        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var knownCards = analysis.KnownPassedCardsBySeat[seat];
            if (knownCards.Count > 0)
            {
                output.AppendLine($"Known still held by {seat}: {string.Join(", ", knownCards.Select(FormatCard))}");
            }
        }

        output.AppendLine("Lead estimate = chance neither opponent can beat that lead, bombs included.");
        output.AppendLine("Hidden hands are sampled uniformly after fixing the actor's unplayed outgoing passes; received pass sources are shown but not yet treated as behavioral evidence.");
        output.AppendLine("Single-lead estimates assess each combination independently.");
        if (analysis.CandidateSearchWasLimited)
        {
            output.AppendLine("Candidate search reached its plan limit; other partitions may also be relevant.");
        }

        output.AppendLine("Single-card lead estimates:");
        foreach (var lead in analysis.LeadEstimates.Where(estimate => estimate.Combination.Type == CombinationType.Single))
        {
            output.AppendLine(
                $"- {string.Join(" ", lead.Combination.Cards.Select(FormatCard))}: " +
                $"{lead.UnopposedProbability:P0} unopposed as a lead, {lead.OpponentCanBeatProbability:P0} hit risk; " +
                $"can answer {lead.SingleRankResponseCoverage} single-card ranks");
        }

        output.AppendLine("Candidate partitions (combination groups, not an action order):");
        for (var index = 0; index < analysis.CandidatePlans.Count; index++)
        {
            var plan = analysis.CandidatePlans[index];
            output.AppendLine(
                $"{index + 1}. {plan.PlayCount} plays; {plan.LowSingleCount} low singles; " +
                $"{plan.ExpectedUnopposedLeads:F2} expected unopposed leads; " +
                $"{plan.LikelyWinnerLeadCount} likely winners / {plan.LikelyLoserLeadCount} likely losers; " +
                $"likely winners cover low singles: {plan.LikelyWinnersCoverLowSingles.ToString().ToLowerInvariant()}");
            output.AppendLine($"   {string.Join(" + ", plan.Plays.Select(FormatPlay))}");
        }

        if (analysis.SequenceEstimates.Count == 0)
        {
            if (analysis.SequenceReplaySkippedReason is { } reason)
            {
                output.AppendLine($"Ordered sequence replay skipped: {reason}");
            }
        }
        else
        {
            output.AppendLine(
                $"Ordered sequence replays: {analysis.SequenceEstimates[0].SampleCount} paired sampled deals per order. " +
                "The caller follows the listed order when leading, uses planned combinations to answer opponents, " +
                "and plays over the partner only to go out. Each order uses both a random partner and a caller-support " +
                "partner; opponents use the random baseline. Only the caller's Tichu is scored.");
            foreach (var sequence in analysis.SequenceEstimates)
            {
                var plannedOrder = string.Join(" -> ", sequence.PlannedOrder.Select(combination =>
                    string.Join(" ", combination.Cards.Select(FormatCard))));
                output.AppendLine(
                    $"- Partition {sequence.PartitionIndex}, {sequence.OrderingName}, {sequence.PartnerModel}: caller first " +
                    $"{sequence.CallerFinishesFirstProbability:P0}, caller second " +
                    $"{sequence.CallerFinishesSecondProbability:P0}, team one-two " +
                    $"{sequence.TeamFinishesOneTwoProbability:P0}, average team point margin " +
                    $"{sequence.AverageTeamPointMargin:+0.0;-0.0;0.0}; order {plannedOrder}");
            }
        }

        return output.ToString().TrimEnd();
    }

    private static string FormatPlay(TichuPlanPlayEstimate play) =>
        $"{string.Join(" ", play.Combination.Cards.Select(FormatCard))} " +
        $"({play.OpponentCanBeatProbability:P0} lead hit risk" +
        (play.Combination.Type == CombinationType.Single
            ? $", answers {play.SingleRankResponseCoverage} single ranks)"
            : ")");

    private static string FormatOptionalCard(Card? card) => card is { } value ? FormatCard(value) : "unknown";

    private static string FormatCard(Card card) => card.IsOrdinary
        ? $"{FormatRank(card.Rank)}-{card.Suit}"
        : card.Rank.ToString();

    private static string FormatRank(CardRank rank) => rank switch
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
        _ => rank.ToString()
    };
}

public sealed record TichuLeadEstimate(
    CardCombination Combination,
    double OpponentCanBeatProbability,
    int SingleRankResponseCoverage)
{
    public double UnopposedProbability => 1.0 - OpponentCanBeatProbability;

    public bool IsLikelyWinner => OpponentCanBeatProbability <= 0.25;

    public bool IsLikelyLoser => OpponentCanBeatProbability >= 0.75;
}

public sealed record TichuPlanCandidate(
    IReadOnlyList<TichuPlanPlayEstimate> Plays,
    int LowSingleCount,
    int HighSingleCount,
    double ExpectedUnopposedLeads,
    double ExpectedOpponentHits,
    int LikelyWinnerLeadCount,
    int LikelyLoserLeadCount)
{
    public int PlayCount => Plays.Count;

    public bool LikelyWinnersCoverLowSingles => LikelyWinnerLeadCount >= LowSingleCount;
}

public sealed record TichuPlanPlayEstimate(
    CardCombination Combination,
    double OpponentCanBeatProbability,
    int SingleRankResponseCoverage);
