namespace Tichu.Core;

/// <summary>A validated play choice, or a pass when <see cref="Combination"/> is null.</summary>
public sealed record LegalMove(
    CardCombination? Combination,
    CardRank? WishForMahJong = null,
    bool IsOutOfTurnBomb = false)
{
    public bool IsPass => Combination is null;
}

/// <summary>Enumerates legal moves from one hand for the current trick state.</summary>
public static class MoveGenerator
{
    /// <summary>
    /// Generates ordinary turn plays (and pass when allowed), or only bombs for
    /// an out-of-turn response. Responses enumerate matching-size plays plus
    /// bombs; a lead examines every subset of the hand.
    /// </summary>
    public static IReadOnlyList<LegalMove> Generate(
        IEnumerable<Card> hand,
        CardCombination? currentTop = null,
        CardRank? pendingWish = null,
        bool isOutOfTurn = false)
    {
        MoveRules.ValidateWishRank(pendingWish);
        var handCards = MoveRules.ValidateHand(hand);

        var moves = new List<LegalMove>();
        var seen = new HashSet<MoveKey>();
        if (handCards.Length == 0 ||
            (isOutOfTurn && (currentTop is null || IsDog(currentTop))) ||
            (currentTop is not null && IsDog(currentTop)))
        {
            return Array.AsReadOnly(moves.ToArray());
        }

        if (currentTop is null)
        {
            for (var size = 1; size <= handCards.Length; size++)
            {
                EnumerateSubsets(handCards, size, (cards, mask) =>
                    AddClassifiedMoves(handCards, cards, mask, currentTop, pendingWish, isOutOfTurn, moves, seen));
            }
        }
        else if (isOutOfTurn)
        {
            AddBombMoves(handCards, currentTop, pendingWish, isOutOfTurn: true, moves, seen);
        }
        else
        {
            if (currentTop.CardCount <= handCards.Length)
            {
                EnumerateSubsets(handCards, currentTop.CardCount, (cards, mask) =>
                    AddClassifiedMoves(handCards, cards, mask, currentTop, pendingWish, false, moves, seen));
            }

            AddBombMoves(handCards, currentTop, pendingWish, isOutOfTurn: false, moves, seen);
        }

        if (!isOutOfTurn && currentTop is not null &&
            MoveRules.ValidatePass(handCards, currentTop, pendingWish).IsLegal)
        {
            moves.Add(new LegalMove(null));
        }

        return Array.AsReadOnly(moves.ToArray());
    }

    private static void AddBombMoves(
        Card[] hand,
        CardCombination currentTop,
        CardRank? pendingWish,
        bool isOutOfTurn,
        List<LegalMove> moves,
        HashSet<MoveKey> seen)
    {
        foreach (var bombCards in EnumerateBombs(hand))
        {
            ushort mask = 0;
            foreach (var card in bombCards)
            {
                var index = Array.IndexOf(hand, card);
                mask |= (ushort)(1 << index);
            }

            AddClassifiedMoves(hand, bombCards, mask, currentTop, pendingWish, isOutOfTurn, moves, seen);
        }
    }

    private static IEnumerable<Card[]> EnumerateBombs(Card[] hand)
    {
        foreach (var rankGroup in hand
                     .Where(card => Card.IsOrdinaryRank(card.Rank))
                     .GroupBy(card => card.Rank)
                     .Where(group => group.Count() == 4))
        {
            yield return rankGroup.ToArray();
        }

        foreach (var suit in Enum.GetValues<CardSuit>())
        {
            var availableRanks = hand
                .Where(card => card.Suit == suit)
                .Select(card => (int)card.Rank)
                .ToHashSet();
            for (var start = (int)CardRank.Two; start <= (int)CardRank.Ace - 4; start++)
            {
                for (var end = start + 4; end <= (int)CardRank.Ace; end++)
                {
                    var count = end - start + 1;
                    var cards = new Card[count];
                    var complete = true;
                    for (var offset = 0; offset < count; offset++)
                    {
                        var rank = start + offset;
                        if (!availableRanks.Contains(rank))
                        {
                            complete = false;
                            break;
                        }

                        cards[offset] = new Card((CardRank)rank, suit);
                    }

                    if (complete)
                    {
                        yield return cards;
                    }
                }
            }
        }
    }

    private static void AddClassifiedMoves(
        Card[] hand,
        Card[] cards,
        ushort mask,
        CardCombination? currentTop,
        CardRank? pendingWish,
        bool isOutOfTurn,
        List<LegalMove> moves,
        HashSet<MoveKey> seen)
    {
        foreach (var combination in CardCombination.ClassifyAll(cards))
        {
            if (isOutOfTurn && !combination.IsBomb)
            {
                continue;
            }

            var validation = MoveRules.ValidatePlay(
                hand,
                combination,
                currentTop,
                pendingWish,
                isOutOfTurn);
            if (!validation.IsLegal)
            {
                continue;
            }

            var hasMahJong = combination.Cards.Any(card => card.Rank == CardRank.MahJong);
            if (!hasMahJong)
            {
                AddMove(validation.ResolvedPlay!, null, isOutOfTurn, mask, combination, moves, seen);
                continue;
            }

            AddMove(validation.ResolvedPlay!, null, isOutOfTurn, mask, combination, moves, seen);
            for (var rank = (int)CardRank.Two; rank <= (int)CardRank.Ace; rank++)
            {
                var wish = (CardRank)rank;
                AddMove(validation.ResolvedPlay!, wish, isOutOfTurn, mask, combination, moves, seen);
            }
        }
    }

    private static void AddMove(
        CardCombination resolved,
        CardRank? wish,
        bool isOutOfTurn,
        ushort mask,
        CardCombination classified,
        List<LegalMove> moves,
        HashSet<MoveKey> seen)
    {
        var key = new MoveKey(mask, classified.Type, classified.PhoenixAsRank, wish, isOutOfTurn);
        if (seen.Add(key))
        {
            moves.Add(new LegalMove(resolved, wish, isOutOfTurn));
        }
    }

    private static void EnumerateSubsets(Card[] hand, int size, Action<Card[], ushort> action)
    {
        var selected = new Card[size];
        Visit(startIndex: 0, selectedCount: 0, mask: 0);

        void Visit(int startIndex, int selectedCount, ushort mask)
        {
            if (selectedCount == size)
            {
                action(selected.ToArray(), mask);
                return;
            }

            var remaining = size - selectedCount;
            for (var index = startIndex; index <= hand.Length - remaining; index++)
            {
                selected[selectedCount] = hand[index];
                Visit(index + 1, selectedCount + 1, (ushort)(mask | (1 << index)));
            }
        }
    }

    private static bool IsDog(CardCombination combination) =>
        combination.Type == CombinationType.Single && combination.Cards[0].Rank == CardRank.Dog;

    private readonly record struct MoveKey(
        ushort CardMask,
        CombinationType Type,
        CardRank? PhoenixAsRank,
        CardRank? Wish,
        bool IsOutOfTurnBomb);
}
