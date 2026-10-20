namespace Tichu.Core;

public enum MoveValidationStatus : byte
{
    Legal,
    PassNotAllowedOnLead,
    NoResponseToDog,
    CardsNotInHand,
    CannotBeatCurrentPlay,
    MustFulfillWish,
    OutOfTurnMoveMustBeBomb,
    NoTrickToBomb
}

public sealed record MoveValidationResult(
    MoveValidationStatus Status,
    CardCombination? ResolvedPlay = null)
{
    public bool IsLegal => Status == MoveValidationStatus.Legal;
}

/// <summary>
/// Validates an already-classified play against hand ownership, the current
/// combination, and an active Mah Jong wish. Turn order and trick collection
/// remain the responsibility of the game state.
/// </summary>
public static class MoveRules
{
    public static MoveValidationResult ValidatePlay(
        IEnumerable<Card> hand,
        CardCombination play,
        CardCombination? currentTop = null,
        CardRank? pendingWish = null,
        bool isOutOfTurn = false)
    {
        ArgumentNullException.ThrowIfNull(play);
        ValidateWishRank(pendingWish);
        var handCards = ValidateHand(hand);

        var handSet = handCards.ToHashSet();
        if (play.Cards.Any(card => !handSet.Contains(card)))
        {
            return new MoveValidationResult(MoveValidationStatus.CardsNotInHand);
        }

        if (currentTop?.Type == CombinationType.Single && currentTop.Cards[0].Rank == CardRank.Dog)
        {
            return new MoveValidationResult(MoveValidationStatus.NoResponseToDog);
        }

        if (isOutOfTurn)
        {
            if (currentTop is null)
            {
                return new MoveValidationResult(MoveValidationStatus.NoTrickToBomb);
            }

            if (!play.IsBomb)
            {
                return new MoveValidationResult(MoveValidationStatus.OutOfTurnMoveMustBeBomb);
            }
        }

        if (currentTop is not null && !play.Beats(currentTop))
        {
            return new MoveValidationResult(MoveValidationStatus.CannotBeatCurrentPlay);
        }

        var containsWishedRank = pendingWish is not null && play.Cards.Any(card => card.Rank == pendingWish.Value);
        if (!isOutOfTurn && pendingWish is not null && !containsWishedRank &&
            HasLegalWishPlay(handCards, pendingWish.Value, currentTop))
        {
            return new MoveValidationResult(MoveValidationStatus.MustFulfillWish);
        }

        var resolvedPlay = currentTop is null ? play : play.ResolveForPlayOver(currentTop);
        return new MoveValidationResult(MoveValidationStatus.Legal, resolvedPlay);
    }

    public static MoveValidationResult ValidatePass(
        IEnumerable<Card> hand,
        CardCombination? currentTop,
        CardRank? pendingWish = null)
    {
        ValidateWishRank(pendingWish);
        var handCards = ValidateHand(hand);

        if (currentTop is null)
        {
            return new MoveValidationResult(MoveValidationStatus.PassNotAllowedOnLead);
        }

        if (currentTop.Type == CombinationType.Single && currentTop.Cards[0].Rank == CardRank.Dog)
        {
            return new MoveValidationResult(MoveValidationStatus.NoResponseToDog);
        }

        if (pendingWish is not null && HasLegalWishPlay(handCards, pendingWish.Value, currentTop))
        {
            return new MoveValidationResult(MoveValidationStatus.MustFulfillWish);
        }

        return new MoveValidationResult(MoveValidationStatus.Legal);
    }

    private static bool HasLegalWishPlay(Card[] hand, CardRank wishedRank, CardCombination? currentTop)
    {
        var wishedCards = hand.Select((card, index) => (Card: card, Index: index))
            .Where(item => item.Card.Rank == wishedRank)
            .ToArray();
        if (wishedCards.Length == 0)
        {
            return false;
        }

        // On an open lead, the wished card is itself a legal single.
        if (currentTop is null)
        {
            return true;
        }

        if (HasLegalResponseContainingWish(hand, wishedCards, currentTop))
        {
            return true;
        }

        return HasLegalWishBomb(hand, wishedCards, wishedRank, currentTop);
    }

    private static bool HasLegalResponseContainingWish(
        Card[] hand,
        (Card Card, int Index)[] wishedCards,
        CardCombination currentTop)
    {
        var targetCount = currentTop.CardCount;
        if (targetCount > hand.Length)
        {
            return false;
        }

        // A candidate containing two wished-rank cards can be reached from
        // either card. The compact hand-index mask avoids classifying it twice.
        var classifiedCandidates = new HashSet<ushort>();
        foreach (var wishedCard in wishedCards)
        {
            var selection = new Card[targetCount];
            selection[0] = wishedCard.Card;
            var selectedMask = (ushort)(1 << wishedCard.Index);
            if (FindLegalWishedCombination(
                    hand,
                    wishedCard.Index,
                    0,
                    selection,
                    selectedCount: 1,
                    selectedMask,
                    classifiedCandidates,
                    currentTop))
            {
                return true;
            }
        }

        return false;
    }

    private static bool FindLegalWishedCombination(
        Card[] hand,
        int excludedIndex,
        int index,
        Card[] selection,
        int selectedCount,
        ushort selectedMask,
        HashSet<ushort> classifiedCandidates,
        CardCombination currentTop)
    {
        if (selectedCount == selection.Length)
        {
            return classifiedCandidates.Add(selectedMask) &&
                   CardCombination.ClassifyAll(selection).Any(combination => combination.Beats(currentTop));
        }

        var remainingSlots = selection.Length - selectedCount;
        for (var candidateIndex = index; candidateIndex < hand.Length; candidateIndex++)
        {
            if (candidateIndex == excludedIndex)
            {
                continue;
            }

            var cardsAfterThis = hand.Length - candidateIndex - 1;
            if (cardsAfterThis < remainingSlots - 1)
            {
                break;
            }

            selection[selectedCount] = hand[candidateIndex];
            if (FindLegalWishedCombination(
                    hand,
                    excludedIndex,
                    candidateIndex + 1,
                    selection,
                    selectedCount + 1,
                    (ushort)(selectedMask | (1 << candidateIndex)),
                    classifiedCandidates,
                    currentTop))
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasLegalWishBomb(
        Card[] hand,
        (Card Card, int Index)[] wishedCards,
        CardRank wishedRank,
        CardCombination currentTop)
    {
        var cardsOfWishedRank = hand.Where(card => card.Rank == wishedRank).ToArray();
        if (cardsOfWishedRank.Length == 4 &&
            CardCombination.ClassifyAll(cardsOfWishedRank).Any(combination => combination.IsBomb && combination.Beats(currentTop)))
        {
            return true;
        }

        var handSet = hand.ToHashSet();
        foreach (var (wishedCard, _) in wishedCards)
        {
            if (wishedCard.Suit is null)
            {
                continue;
            }

            var suit = wishedCard.Suit.Value;
            for (var start = (int)CardRank.Two; start <= (int)wishedRank; start++)
            {
                for (var end = (int)wishedRank; end <= (int)CardRank.Ace; end++)
                {
                    if (end - start + 1 < 5)
                    {
                        continue;
                    }

                    var cards = Enumerable.Range(start, end - start + 1)
                        .Select(rank => new Card((CardRank)rank, suit))
                        .ToArray();
                    if (cards.Any(card => !handSet.Contains(card)))
                    {
                        continue;
                    }

                    if (CardCombination.ClassifyAll(cards)
                        .Any(combination => combination.Type == CombinationType.StraightFlushBomb &&
                                            combination.Beats(currentTop)))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static Card[] ValidateHand(IEnumerable<Card> hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        var cards = hand.ToArray();
        if (cards.Any(card => !card.IsValid))
        {
            throw new ArgumentException("A hand can contain only valid physical cards.", nameof(hand));
        }

        if (cards.Distinct().Count() != cards.Length)
        {
            throw new ArgumentException("A hand cannot contain the same physical card more than once.", nameof(hand));
        }

        return cards;
    }

    internal static void ValidateWishRank(CardRank? pendingWish)
    {
        if (pendingWish is not null && !Card.IsOrdinaryRank(pendingWish.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(pendingWish), "A Mah Jong wish must be a rank from 2 through Ace.");
        }
    }
}
