namespace Tichu.Core;

public enum CombinationType : byte
{
    Single,
    Pair,
    ConsecutivePairs,
    Triple,
    FullHouse,
    Straight,
    FourOfAKindBomb,
    StraightFlushBomb
}

/// <summary>
/// A validated play shape. Strength is its comparison rank; for a straight it is
/// the high card, and for a full house it is the rank of the triple.
/// A leading Phoenix single has strength 1.5. Resolve it against the current
/// single before storing it as a played response.
/// </summary>
public sealed class CardCombination
{
    private CardCombination(CombinationType type, Card[] cards, decimal strength, CardRank? phoenixAsRank = null)
    {
        Type = type;
        Cards = Array.AsReadOnly(cards);
        Strength = strength;
        PhoenixAsRank = phoenixAsRank;
    }

    public CombinationType Type { get; }

    public IReadOnlyList<Card> Cards { get; }

    public int CardCount => Cards.Count;

    public decimal Strength { get; }

    /// <summary>The normal rank assigned to Phoenix in this interpretation, if any.</summary>
    public CardRank? PhoenixAsRank { get; }

    public bool IsBomb => Type is CombinationType.FourOfAKindBomb or CombinationType.StraightFlushBomb;

    /// <summary>
    /// Classifies a set of cards as one legal Tichu combination. Phoenix may
    /// substitute for a normal rank in pairs, triples, full houses, pair runs,
    /// and straights, but never in a bomb.
    /// </summary>
    public static bool TryClassify(IEnumerable<Card> cards, out CardCombination? combination)
    {
        var interpretations = ClassifyAll(cards);
        combination = interpretations
            .OrderByDescending(candidate => candidate.Strength)
            .FirstOrDefault();
        return combination is not null;
    }

    /// <summary>
    /// Returns every legal Phoenix assignment for a card set. This preserves
    /// strategically distinct interpretations (for example, a Phoenix full
    /// house can choose which rank forms the triple).
    /// </summary>
    public static IReadOnlyList<CardCombination> ClassifyAll(IEnumerable<Card> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        var hand = cards.ToArray();
        if (hand.Length is 0 or > 14 || hand.Any(card => !card.IsValid) || hand.Distinct().Count() != hand.Length)
        {
            return Array.Empty<CardCombination>();
        }

        if (hand.Any(card => card.Rank == CardRank.Phoenix) && hand.Length > 1)
        {
            var interpretations = new List<CardCombination>();
            for (var rank = (byte)CardRank.Two; rank <= (byte)CardRank.Ace; rank++)
            {
                if (TryClassifyCore(hand, (CardRank)rank, out var interpretation))
                {
                    interpretations.Add(interpretation!);
                }
            }

            return Array.AsReadOnly(interpretations.ToArray());
        }

        return TryClassifyCore(hand, phoenixAsRank: null, out var combination)
            ? Array.AsReadOnly(new[] { combination! })
            : Array.Empty<CardCombination>();
    }

    private static bool TryClassifyCore(Card[] hand, CardRank? phoenixAsRank, out CardCombination? combination)
    {
        combination = null;
        if (hand.Length == 1)
        {
            combination = new CardCombination(CombinationType.Single, hand, SingleStrength(hand[0].Rank));
            return true;
        }

        if (TryStraightFlushBomb(hand, out var straightFlushHigh))
        {
            combination = new CardCombination(CombinationType.StraightFlushBomb, hand, straightFlushHigh, phoenixAsRank);
            return true;
        }

        if (TryFourOfAKindBomb(hand, out var fourKindRank))
        {
            combination = new CardCombination(CombinationType.FourOfAKindBomb, hand, fourKindRank, phoenixAsRank);
            return true;
        }

        if (TrySameRankSet(hand, 2, phoenixAsRank, out var pairRank))
        {
            combination = new CardCombination(CombinationType.Pair, hand, pairRank, phoenixAsRank);
            return true;
        }

        if (TrySameRankSet(hand, 3, phoenixAsRank, out var tripleRank))
        {
            combination = new CardCombination(CombinationType.Triple, hand, tripleRank, phoenixAsRank);
            return true;
        }

        if (TryFullHouse(hand, phoenixAsRank, out var fullHouseTripleRank))
        {
            combination = new CardCombination(CombinationType.FullHouse, hand, fullHouseTripleRank, phoenixAsRank);
            return true;
        }

        if (TryConsecutivePairs(hand, phoenixAsRank, out var pairRunHigh))
        {
            combination = new CardCombination(CombinationType.ConsecutivePairs, hand, pairRunHigh, phoenixAsRank);
            return true;
        }

        if (TryStraight(hand, phoenixAsRank, out var straightHigh))
        {
            combination = new CardCombination(CombinationType.Straight, hand, straightHigh, phoenixAsRank);
            return true;
        }

        return false;
    }

    /// <summary>Returns a copy with the effective value of a Phoenix single response.</summary>
    public CardCombination ResolveForPlayOver(CardCombination currentTop)
    {
        ArgumentNullException.ThrowIfNull(currentTop);
        if (Type != CombinationType.Single || Cards[0].Rank != CardRank.Phoenix ||
            currentTop.Type != CombinationType.Single ||
            currentTop.Cards[0].Rank is CardRank.Dog or CardRank.Dragon)
        {
            return this;
        }

        return new CardCombination(Type, Cards.ToArray(), currentTop.Strength + 0.5m, PhoenixAsRank);
    }

    /// <summary>Whether this combination can legally beat the current top combination.</summary>
    public bool Beats(CardCombination currentTop)
    {
        ArgumentNullException.ThrowIfNull(currentTop);

        // The Dog transfers the lead immediately and cannot be answered or bombed.
        if (currentTop.Type == CombinationType.Single && currentTop.Cards[0].Rank == CardRank.Dog)
        {
            return false;
        }

        if (IsBomb)
        {
            if (!currentTop.IsBomb)
            {
                return true;
            }

            if (Type == CombinationType.FourOfAKindBomb)
            {
                return currentTop.Type == CombinationType.FourOfAKindBomb && Strength > currentTop.Strength;
            }

            if (currentTop.Type == CombinationType.FourOfAKindBomb)
            {
                return true;
            }

            // Longer straight-flush bombs beat shorter ones; ties use the high card.
            return CardCount > currentTop.CardCount ||
                   (CardCount == currentTop.CardCount && Strength > currentTop.Strength);
        }

        if (currentTop.IsBomb || Type != currentTop.Type || CardCount != currentTop.CardCount)
        {
            return false;
        }

        if (Type == CombinationType.Single)
        {
            if (Cards[0].Rank == CardRank.Dog)
            {
                return false;
            }

            if (Cards[0].Rank == CardRank.Phoenix)
            {
                return currentTop.Cards[0].Rank != CardRank.Dragon;
            }
        }

        return Strength > currentTop.Strength;
    }

    private static bool TrySameRankSet(Card[] cards, int expectedCount, CardRank? phoenixAsRank, out int rank)
    {
        rank = 0;
        if (cards.Length != expectedCount)
        {
            return false;
        }

        var phoenixCount = cards.Count(card => card.Rank == CardRank.Phoenix);
        if (phoenixCount > 1 || cards.Any(card => card.Rank != CardRank.Phoenix && !Card.IsOrdinaryRank(card.Rank)))
        {
            return false;
        }

        var normalRanks = cards.Where(card => card.Rank != CardRank.Phoenix).Select(card => (int)card.Rank).ToArray();
        if (normalRanks.Length == 0 || normalRanks.Distinct().Count() != 1)
        {
            return false;
        }

        rank = normalRanks[0];
        return phoenixCount == 0 || phoenixAsRank == (CardRank)rank;
    }

    private static bool TryFourOfAKindBomb(Card[] cards, out int rank)
    {
        rank = 0;
        if (cards.Length != 4 || cards.Any(card => !Card.IsOrdinaryRank(card.Rank)))
        {
            return false;
        }

        var ranks = cards.Select(card => (int)card.Rank).Distinct().ToArray();
        if (ranks.Length != 1)
        {
            return false;
        }

        rank = ranks[0];
        return true;
    }

    private static bool TryStraightFlushBomb(Card[] cards, out int high)
    {
        high = 0;
        if (cards.Length < 5 || cards.Any(card => !Card.IsOrdinaryRank(card.Rank)) ||
            cards.Select(card => card.Suit).Distinct().Count() != 1)
        {
            return false;
        }

        var ranks = cards.Select(card => (int)card.Rank).Order().ToArray();
        if (!AreConsecutive(ranks))
        {
            return false;
        }

        high = ranks[^1];
        return true;
    }

    private static bool TryFullHouse(Card[] cards, CardRank? phoenixAsRank, out int tripleRank)
    {
        tripleRank = 0;
        if (cards.Length != 5 || !TryNormalRankCounts(cards, allowMahJong: false, out var counts, out var phoenixCount))
        {
            return false;
        }

        var found = false;
        foreach (var assignment in WildcardAssignments(counts, phoenixCount, (int)CardRank.Two, (int)CardRank.Ace, phoenixAsRank))
        {
            if (assignment.Count != 2)
            {
                continue;
            }

            var triple = assignment.SingleOrDefault(pair => pair.Value == 3);
            var pair = assignment.SingleOrDefault(item => item.Value == 2);
            if (triple.Value == 3 && pair.Value == 2 && (!found || triple.Key > tripleRank))
            {
                tripleRank = triple.Key;
                found = true;
            }
        }

        return found;
    }

    private static bool TryConsecutivePairs(Card[] cards, CardRank? phoenixAsRank, out int high)
    {
        high = 0;
        if (cards.Length < 4 || cards.Length % 2 != 0 ||
            !TryNormalRankCounts(cards, allowMahJong: false, out var counts, out var phoenixCount))
        {
            return false;
        }

        var found = false;
        foreach (var assignment in WildcardAssignments(counts, phoenixCount, (int)CardRank.Two, (int)CardRank.Ace, phoenixAsRank))
        {
            if (assignment.Count < 2 || assignment.Values.Any(count => count != 2))
            {
                continue;
            }

            var ranks = assignment.Keys.Order().ToArray();
            if (AreConsecutive(ranks) && (!found || ranks[^1] > high))
            {
                high = ranks[^1];
                found = true;
            }
        }

        return found;
    }

    private static bool TryStraight(Card[] cards, CardRank? phoenixAsRank, out int high)
    {
        high = 0;
        if (cards.Length < 5 || !TryNormalRankCounts(cards, allowMahJong: true, out var counts, out var phoenixCount))
        {
            return false;
        }

        var found = false;
        foreach (var assignment in WildcardAssignments(counts, phoenixCount, (int)CardRank.Two, (int)CardRank.Ace, phoenixAsRank))
        {
            if (assignment.Values.Any(count => count != 1))
            {
                continue;
            }

            var ranks = assignment.Keys.Order().ToArray();
            if (ranks.Length == cards.Length && AreConsecutive(ranks) && (!found || ranks[^1] > high))
            {
                high = ranks[^1];
                found = true;
            }
        }

        return found;
    }

    private static bool TryNormalRankCounts(
        Card[] cards,
        bool allowMahJong,
        out Dictionary<int, int> counts,
        out int phoenixCount)
    {
        counts = new Dictionary<int, int>();
        phoenixCount = 0;
        foreach (var card in cards)
        {
            if (card.Rank == CardRank.Phoenix)
            {
                phoenixCount++;
                if (phoenixCount > 1)
                {
                    return false;
                }

                continue;
            }

            if (Card.IsOrdinaryRank(card.Rank))
            {
                counts[(int)card.Rank] = counts.GetValueOrDefault((int)card.Rank) + 1;
                continue;
            }

            if (allowMahJong && card.Rank == CardRank.MahJong)
            {
                counts[(int)CardRank.MahJong] = counts.GetValueOrDefault((int)CardRank.MahJong) + 1;
                continue;
            }

            return false;
        }

        return true;
    }

    private static IEnumerable<Dictionary<int, int>> WildcardAssignments(
        Dictionary<int, int> counts,
        int phoenixCount,
        int minimumRank,
        int maximumRank,
        CardRank? phoenixAsRank)
    {
        if (phoenixCount == 0)
        {
            yield return counts;
            yield break;
        }

        if (phoenixAsRank is not null)
        {
            var rank = (int)phoenixAsRank.Value;
            if (rank >= minimumRank && rank <= maximumRank)
            {
                var assignment = new Dictionary<int, int>(counts)
                {
                    [rank] = counts.GetValueOrDefault(rank) + 1
                };
                yield return assignment;
            }

            yield break;
        }

        for (var rank = minimumRank; rank <= maximumRank; rank++)
        {
            var assignment = new Dictionary<int, int>(counts)
            {
                [rank] = counts.GetValueOrDefault(rank) + 1
            };
            yield return assignment;
        }
    }

    private static bool AreConsecutive(int[] sortedRanks)
    {
        if (sortedRanks.Length == 0)
        {
            return false;
        }

        for (var i = 1; i < sortedRanks.Length; i++)
        {
            if (sortedRanks[i] != sortedRanks[i - 1] + 1)
            {
                return false;
            }
        }

        return true;
    }

    private static decimal SingleStrength(CardRank rank) => rank switch
    {
        CardRank.MahJong => 1m,
        CardRank.Phoenix => 1.5m,
        CardRank.Dragon => 15m,
        CardRank.Dog => 0m,
        _ => (int)rank
    };
}
