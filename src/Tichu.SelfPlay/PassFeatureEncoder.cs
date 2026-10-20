using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Encodes a candidate pass, its recipient, and the hand structure it leaves behind.</summary>
public static class PassFeatureEncoder
{
    private const int RankFeatureStart = 6;
    private const int RankCount = (int)CardRank.Dog - (int)CardRank.MahJong + 1;
    private const int RecipientRankFeatureStart = RankFeatureStart + RankCount;
    private const int RecipientCount = 3;
    private const int HandFeatureStart = RecipientRankFeatureStart + RankCount * RecipientCount;

    public const int FeatureCount = HandFeatureStart + 12;

    private static readonly string[] FeatureNameArray = CreateFeatureNames();

    public static IReadOnlyList<string> FeatureNames { get; } = Array.AsReadOnly(FeatureNameArray);

    public static double[] Encode(
        PlayerSeat seat,
        IReadOnlyList<Card> remainingHand,
        Card card,
        PlayerSeat recipient)
    {
        seat.ToIndex();
        recipient.ToIndex();
        ArgumentNullException.ThrowIfNull(remainingHand);
        if (!remainingHand.Contains(card))
        {
            throw new ArgumentException("The candidate pass card must be in the remaining hand.", nameof(card));
        }

        var recipientIndex = recipient == seat.Partner()
            ? 1
            : recipient == seat.ToTheRight()
                ? 0
                : recipient == seat.ToTheLeft()
                    ? 2
                    : throw new ArgumentException("A pass recipient must be the partner or one of the opponents.", nameof(recipient));

        var remaining = remainingHand.Where(candidate => candidate != card).ToArray();
        var rankCopies = card.IsOrdinary
            ? remainingHand.Count(candidate => candidate.Rank == card.Rank)
            : 0;
        var sameSuitAdjacenciesBefore = CountSameSuitAdjacencies(remainingHand);
        var sameSuitAdjacenciesAfter = CountSameSuitAdjacencies(remaining);

        var features = new double[FeatureCount];
        features[0] = 1.0;
        features[1 + recipientIndex] = 1.0;
        features[4] = CardScoring.Value(card) / 25.0;
        features[5] = card.IsOrdinary ? 0.0 : 1.0;
        var rankIndex = (int)card.Rank - (int)CardRank.MahJong;
        features[RankFeatureStart + rankIndex] = 1.0;
        features[RecipientRankFeatureStart + recipientIndex * RankCount + rankIndex] = 1.0;

        features[HandFeatureStart] = remaining.Length / 14.0;
        features[HandFeatureStart + 1] = rankCopies / 4.0;
        features[HandFeatureStart + 2] = Math.Max(0, rankCopies - 1) / 3.0;
        features[HandFeatureStart + 3] = rankCopies == 2 ? 1.0 : 0.0;
        features[HandFeatureStart + 4] = rankCopies >= 3 ? 1.0 : 0.0;
        features[HandFeatureStart + 5] = sameSuitAdjacenciesBefore / 48.0;
        features[HandFeatureStart + 6] = sameSuitAdjacenciesAfter / 48.0;
        features[HandFeatureStart + 7] = LongestStraightPotential(remainingHand) / 14.0;
        features[HandFeatureStart + 8] = LongestStraightPotential(remaining) / 14.0;
        features[HandFeatureStart + 9] = CardScoring.Total(remaining) / 100.0;
        features[HandFeatureStart + 10] = remaining.Count(candidate =>
            candidate.Rank is CardRank.Dragon or CardRank.Phoenix or CardRank.Ace) / 3.0;
        features[HandFeatureStart + 11] = remaining.Count(candidate => candidate.Rank == CardRank.Dog) > 0 ? 1.0 : 0.0;
        return features;
    }

    private static string[] CreateFeatureNames()
    {
        var names = new List<string>
        {
            "Bias",
            "RecipientRight",
            "RecipientPartner",
            "RecipientLeft",
            "CardPointValue",
            "IsSpecialCard"
        };

        for (var rank = (int)CardRank.MahJong; rank <= (int)CardRank.Dog; rank++)
        {
            names.Add($"CardRank_{(CardRank)rank}");
        }

        foreach (var recipient in new[] { "Right", "Partner", "Left" })
        {
            for (var rank = (int)CardRank.MahJong; rank <= (int)CardRank.Dog; rank++)
            {
                names.Add($"CardRank_{(CardRank)rank}_To{recipient}");
            }
        }

        names.AddRange(
        [
            "RemainingHandSizeAfterPass",
            "SameRankCopiesBeforePass",
            "SameRankCopiesAfterPass",
            "BreaksPair",
            "PassesFromTripleOrQuad",
            "SameSuitAdjacentPairsBeforePass",
            "SameSuitAdjacentPairsAfterPass",
            "LongestStraightBeforePass",
            "LongestStraightAfterPass",
            "RemainingPointsAfterPass",
            "RemainingTopCardsAfterPass",
            "DogRetainedAfterPass"
        ]);
        return names.ToArray();
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

    private static int CountSameSuitAdjacencies(IEnumerable<Card> hand)
    {
        var ranksBySuit = hand
            .Where(card => card.IsOrdinary)
            .GroupBy(card => card.Suit)
            .Select(group => group.Select(card => (int)card.Rank).ToHashSet());
        return ranksBySuit.Sum(ranks => ranks.Count(rank => ranks.Contains(rank + 1)));
    }
}
