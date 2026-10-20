using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Encodes a player's private hand, public round state, and chosen legal move.</summary>
public static class MoveFeatureEncoder
{
    public const int WishRankInteractionStart = 41;
    public const int WishMatchesPassToLeftStart = WishRankInteractionStart + 13;
    public const int FeatureCount = WishMatchesPassToLeftStart + 13;

    private static readonly string[] FeatureNameArray = CreateFeatureNames();

    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(FeatureNameArray);

    public static double[] Encode(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        LegalMove move,
        PolicyRoundContext? roundContext = null)
    {
        seat.ToIndex();
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(move);
        if (roundContext is not null && roundContext.Seat != seat)
        {
            throw new ArgumentException("The round context must belong to the acting seat.", nameof(roundContext));
        }

        var trick = snapshot.Trick;
        var playedCards = move.Combination?.Cards ?? Array.Empty<Card>();
        var playedSet = playedCards.ToHashSet();
        var remainingHand = hand.Where(card => !playedSet.Contains(card)).ToArray();
        var rankCounts = remainingHand
            .Where(card => card.Rank is >= CardRank.Two and <= CardRank.Ace)
            .GroupBy(card => card.Rank)
            .ToDictionary(group => group.Key, group => group.Count());
        var phoenixCount = remainingHand.Count(card => card.Rank == CardRank.Phoenix);
        var pairRanks = rankCounts.Count(pair => pair.Value >= 2);
        var tripleRanks = rankCounts.Count(pair => pair.Value >= 3);
        var hasFullHouse = HasFullHousePotential(rankCounts, phoenixCount);
        var hasHighFullHouse = HasHighFullHousePotential(rankCounts, phoenixCount);
        var highestTriplet = rankCounts
            .Where(pair => pair.Value >= 3)
            .Select(pair => (int)pair.Key)
            .DefaultIfEmpty(0)
            .Max();
        var wishedRank = move.WishForMahJong;
        var wishCount = wishedRank is { } rank ? rankCounts.GetValueOrDefault(rank) : 0;
        var partner = seat.Partner();
        var opponents = Enum.GetValues<PlayerSeat>()
            .Where(other => other != seat && other != partner)
            .ToArray();
        var topIsPartner = trick.CurrentTopSeat == partner;
        var topIsOpponent = trick.CurrentTopSeat is { } topSeat && opponents.Contains(topSeat);
        var teamPoints = snapshot.CollectedPointsBySeat[(int)seat] +
                         snapshot.CollectedPointsBySeat[(int)partner];

        var features = new double[FeatureCount];
        features[0] = 1.0;
        features[1] = move.IsPass ? 1.0 : 0.0;
        features[2] = playedCards.Count / 14.0;
        features[3] = move.Combination?.IsBomb == true ? 1.0 : 0.0;
        features[4] = Math.Clamp((double)(move.Combination?.Strength ?? 0m) / 16.0, 0.0, 2.0);
        features[5] = CardScoring.Total(playedCards) / 100.0;
        features[6] = Count(playedCards, CardRank.Dragon);
        features[7] = Count(playedCards, CardRank.Phoenix);
        features[8] = Count(playedCards, CardRank.Ace);
        features[9] = Count(playedCards, CardRank.MahJong);
        features[10] = Count(playedCards, CardRank.Dog);
        features[11] = remainingHand.Length / 14.0;
        features[12] = pairRanks / 7.0;
        features[13] = tripleRanks / 4.0;
        features[14] = hasFullHouse ? 1.0 : 0.0;
        features[15] = rankCounts.Values.Any(count => count == 4) ? 1.0 : 0.0;
        features[16] = remainingHand.Count(card => card.Rank is CardRank.Dragon or CardRank.Phoenix or CardRank.Ace) / 3.0;
        features[17] = wishedRank is { } wish ? (int)wish / 14.0 : 0.0;
        features[18] = wishCount / 3.0;
        features[19] = wishCount >= 2 ? 1.0 : 0.0;
        features[20] = wishCount >= 3 ? 1.0 : 0.0;
        features[21] = trick.CurrentTop is null ? 1.0 : 0.0;
        features[22] = topIsPartner ? 1.0 : 0.0;
        features[23] = topIsOpponent ? 1.0 : 0.0;
        features[24] = snapshot.HandSizes[(int)partner] / 14.0;
        features[25] = opponents.Min(opponent => snapshot.HandSizes[(int)opponent]) / 14.0;
        features[26] = trick.Plays.Sum(play => CardScoring.Total(play.Combination.Cards)) / 100.0;
        features[27] = teamPoints / 100.0;
        features[28] = move.IsOutOfTurnBomb ? 1.0 : 0.0;
        features[29] = trick.PendingWish is { } pending && playedCards.Any(card => card.Rank == pending)
            ? 1.0
            : 0.0;
        features[30] = LongestStraightPotential(remainingHand) / 14.0;
        features[31] = CardScoring.Total(remainingHand) / 100.0;
        features[32] = highestTriplet / 14.0;

        if (move.Combination is { } combination)
        {
            features[33 + (int)combination.Type] = 1.0;
        }

        if (wishedRank is { } requestedRank && hasHighFullHouse)
        {
            features[WishRankInteractionStart + (int)requestedRank - (int)CardRank.Two] = 1.0;
        }

        if (wishedRank is { } passedWishRank && roundContext?.PassSelection.ToLeft.Rank == passedWishRank)
        {
            features[WishMatchesPassToLeftStart + (int)passedWishRank - (int)CardRank.Two] = 1.0;
        }

        return features;
    }

    private static string[] CreateFeatureNames()
    {
        var names = new List<string>
        {
            "Bias",
            "IsPass",
            "CardsPlayed",
            "IsBomb",
            "CombinationStrength",
            "PlayedPoints",
            "PlayedDragon",
            "PlayedPhoenix",
            "PlayedAce",
            "PlayedMahJong",
            "PlayedDog",
            "RemainingHandSize",
            "PairRanksAfterPlay",
            "TripleRanksAfterPlay",
            "FullHousePotentialAfterPlay",
            "FourOfAKindPotentialAfterPlay",
            "HighCardsRetained",
            "MahJongWishRank",
            "WishRankCopiesRetained",
            "WishRankPairRetained",
            "WishRankTripleRetained",
            "IsLead",
            "CurrentTopIsPartner",
            "CurrentTopIsOpponent",
            "PartnerHandSize",
            "NearestOpponentHandSize",
            "PointsOnTable",
            "CollectedTeamPoints",
            "IsOutOfTurnBomb",
            "FulfillsPendingWish",
            "RemainingStraightPotential",
            "RemainingScoringPoints",
            "HighestRetainedTripleRank",
            "CombinationSingle",
            "CombinationPair",
            "CombinationConsecutivePairs",
            "CombinationTriple",
            "CombinationFullHouse",
            "CombinationStraight",
            "CombinationFourOfAKindBomb",
            "CombinationStraightFlushBomb"
        };

        for (var rank = (int)CardRank.Two; rank <= (int)CardRank.Ace; rank++)
        {
            names.Add($"WishAgainstHighFullHouse_{(CardRank)rank}");
        }

        for (var rank = (int)CardRank.Two; rank <= (int)CardRank.Ace; rank++)
        {
            names.Add($"WishMatchesPassToLeft_{(CardRank)rank}");
        }

        return names.ToArray();
    }

    private static bool HasFullHousePotential(IReadOnlyDictionary<CardRank, int> counts, int phoenixCount)
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

    private static bool HasHighFullHousePotential(IReadOnlyDictionary<CardRank, int> counts, int phoenixCount) =>
        counts.Any(pair => pair.Key >= CardRank.Jack && pair.Value >= 3) &&
        HasFullHousePotential(counts, phoenixCount);

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
