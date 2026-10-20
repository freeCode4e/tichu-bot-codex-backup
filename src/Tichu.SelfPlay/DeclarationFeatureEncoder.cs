using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Encodes a player's hand for the Grand and Tichu call decisions.</summary>
public static class DeclarationFeatureEncoder
{
    public const int GrandCallBiasIndex = 0;
    public const int TichuCallBiasIndex = 1;
    public const int GrandFourOfAKindIndex = 8;
    public const int GrandStraightPotentialIndex = 9;
    public const int TichuAceCountIndex = 13;
    public const int GrandPartnerCalledIndex = 23;
    public const int GrandOpponentCallCountIndex = 24;
    public const int PartnerTichuCalledIndex = 25;
    public const int OpponentTichuCallCountIndex = 26;
    public const int GrandPartnerUndecidedIndex = 27;
    public const int GrandOpponentUndecidedCountIndex = 28;
    public const int PartnerTichuAvailableIndex = 29;
    public const int OpponentTichuAvailableCountIndex = 30;
    public const int TichuAnyCardPlayedIndex = 31;
    public const int TichuPendingWishIndex = 32;
    public const int TichuPendingWishRankIndex = 33;
    public const int TichuWishCardCountIndex = 34;
    public const int TichuCurrentTrickPlayCountIndex = 35;
    public const int TichuCurrentTrickLedByPartnerIndex = 36;
    public const int FeatureCount = 37;

    private static readonly string[] FeatureNameArray =
    {
        "GrandCallBias",
        "TichuCallBias",
        "GrandHasDragon",
        "GrandHasPhoenix",
        "GrandAceCount",
        "GrandHighCardCount",
        "GrandPairRanks",
        "GrandTripleRanks",
        "GrandFourOfAKind",
        "GrandStraightPotential",
        "GrandHasMahJong",
        "TichuHasDragon",
        "TichuHasPhoenix",
        "TichuAceCount",
        "TichuHighCardCount",
        "TichuPairRanks",
        "TichuTripleRanks",
        "TichuFourOfAKind",
        "TichuStraightPotential",
        "TichuHasMahJong",
        "TichuHasDog",
        "TichuFullHousePotential",
        "TichuHandPoints",
        "GrandPartnerAlreadyCalled",
        "GrandOpponentCallCount",
        "PartnerTichuAlreadyCalled",
        "OpponentTichuCallCount",
        "GrandPartnerStillUndecided",
        "GrandOpponentUndecidedCount",
        "PartnerTichuWindowOpen",
        "OpponentTichuWindowsOpen",
        "TichuAnyCardPlayed",
        "TichuPendingWish",
        "TichuPendingWishRank",
        "TichuWishCardCount",
        "TichuCurrentTrickPlayCount",
        "TichuCurrentTrickLedByPartner"
    };

    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(FeatureNameArray);

    public static double[] Encode(
        IReadOnlyList<Card> hand,
        bool isGrandDecision,
        PolicyDeclarationContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(hand);

        var ordinaryCounts = hand
            .Where(card => card.Rank is >= CardRank.Two and <= CardRank.Ace)
            .GroupBy(card => card.Rank)
            .ToDictionary(group => group.Key, group => group.Count());
        var pairRanks = ordinaryCounts.Count(pair => pair.Value >= 2);
        var tripleRanks = ordinaryCounts.Count(pair => pair.Value >= 3);
        var hasFourOfAKind = ordinaryCounts.Values.Any(count => count == 4);
        var highCardCount = hand.Count(card => card.Rank is CardRank.Dragon or CardRank.Phoenix or CardRank.Ace or CardRank.King);
        var aceCount = ordinaryCounts.GetValueOrDefault(CardRank.Ace);
        var features = new double[FeatureCount];

        if (isGrandDecision)
        {
            features[GrandCallBiasIndex] = 1.0;
            features[2] = Count(hand, CardRank.Dragon);
            features[3] = Count(hand, CardRank.Phoenix);
            features[4] = aceCount / 3.0;
            features[5] = highCardCount / 4.0;
            features[6] = pairRanks / 4.0;
            features[7] = tripleRanks / 2.0;
            features[GrandFourOfAKindIndex] = hasFourOfAKind ? 1.0 : 0.0;
            features[GrandStraightPotentialIndex] = LongestStraightPotential(hand) / 8.0;
            features[10] = Count(hand, CardRank.MahJong);
            EncodePublicContext(features, context);
            return features;
        }

        features[TichuCallBiasIndex] = 1.0;
        features[11] = Count(hand, CardRank.Dragon);
        features[12] = Count(hand, CardRank.Phoenix);
        features[TichuAceCountIndex] = aceCount / 3.0;
        features[14] = highCardCount / 6.0;
        features[15] = pairRanks / 7.0;
        features[16] = tripleRanks / 4.0;
        features[17] = hasFourOfAKind ? 1.0 : 0.0;
        features[18] = LongestStraightPotential(hand) / 14.0;
        features[19] = Count(hand, CardRank.MahJong);
        features[20] = Count(hand, CardRank.Dog);
        features[21] = HasFullHousePotential(ordinaryCounts, Count(hand, CardRank.Phoenix)) ? 1.0 : 0.0;
        features[22] = CardScoring.Total(hand) / 100.0;

        EncodePublicContext(features, context);
        EncodePlayContext(features, hand, context);
        return features;
    }

    private static void EncodePlayContext(
        double[] features,
        IReadOnlyList<Card> hand,
        PolicyDeclarationContext? context)
    {
        if (context?.PlaySnapshot is not { } snapshot)
        {
            return;
        }

        features[TichuAnyCardPlayedIndex] = snapshot.HandSizes.Any(size => size < 14) ? 1.0 : 0.0;
        features[TichuCurrentTrickPlayCountIndex] = Math.Min(snapshot.Trick.Plays.Count, 4) / 4.0;
        features[TichuCurrentTrickLedByPartnerIndex] =
            snapshot.Trick.CurrentTopSeat == context.Seat.Partner() ? 1.0 : 0.0;

        if (snapshot.Trick.PendingWish is not { } wish)
        {
            return;
        }

        features[TichuPendingWishIndex] = 1.0;
        features[TichuPendingWishRankIndex] =
            ((double)(int)wish - (int)CardRank.Two) / ((int)CardRank.Ace - (int)CardRank.Two);
        features[TichuWishCardCountIndex] = hand.Count(card => card.Rank == wish) / 4.0;
    }

    private static void EncodePublicContext(double[] features, PolicyDeclarationContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.Seat.ToIndex();
        ArgumentNullException.ThrowIfNull(context.Snapshot);
        var partner = context.Seat.Partner();
        var opponents = Enum.GetValues<PlayerSeat>()
            .Where(seat => seat != context.Seat && seat != partner)
            .Select(context.Snapshot.For)
            .ToArray();
        var partnerDecision = context.Snapshot.For(partner);
        features[GrandPartnerCalledIndex] = partnerDecision.Status == GrandDecisionStatus.Called ? 1.0 : 0.0;
        features[GrandOpponentCallCountIndex] =
            opponents.Count(decision => decision.Status == GrandDecisionStatus.Called) / 2.0;
        features[PartnerTichuCalledIndex] =
            partnerDecision.TichuStatus == TichuDecisionStatus.Called ? 1.0 : 0.0;
        features[OpponentTichuCallCountIndex] =
            opponents.Count(decision => decision.TichuStatus == TichuDecisionStatus.Called) / 2.0;
        features[GrandPartnerUndecidedIndex] =
            partnerDecision.Status == GrandDecisionStatus.Undecided ? 1.0 : 0.0;
        features[GrandOpponentUndecidedCountIndex] =
            opponents.Count(decision => decision.Status == GrandDecisionStatus.Undecided) / 2.0;
        features[PartnerTichuAvailableIndex] =
            partnerDecision.TichuStatus == TichuDecisionStatus.Available ? 1.0 : 0.0;
        features[OpponentTichuAvailableCountIndex] =
            opponents.Count(decision => decision.TichuStatus == TichuDecisionStatus.Available) / 2.0;
    }

    private static bool HasFullHousePotential(
        IReadOnlyDictionary<CardRank, int> counts,
        int phoenixCount)
    {
        foreach (var (tripleRank, tripleCount) in counts)
        {
            foreach (var (pairRank, pairCount) in counts)
            {
                if (tripleRank == pairRank)
                {
                    continue;
                }

                if (tripleCount >= 3 && pairCount >= 2 ||
                    phoenixCount > 0 && (tripleCount >= 3 && pairCount >= 1 || tripleCount >= 2 && pairCount >= 2))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static int LongestStraightPotential(IEnumerable<Card> hand)
    {
        var ranks = hand
            .Where(card => card.Rank is CardRank.MahJong or >= CardRank.Two and <= CardRank.Ace)
            .Select(card => (int)card.Rank)
            .Distinct()
            .Order()
            .ToArray();
        var longest = 0;
        var current = 0;
        var previous = -2;
        foreach (var rank in ranks)
        {
            current = rank == previous + 1 ? current + 1 : 1;
            longest = Math.Max(longest, current);
            previous = rank;
        }

        return longest;
    }

    private static int Count(IEnumerable<Card> cards, CardRank rank) => cards.Count(card => card.Rank == rank);
}
