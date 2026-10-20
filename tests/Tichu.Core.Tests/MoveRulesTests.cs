using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class MoveRulesTests
{
    [Fact]
    public void LeadPlayIsAcceptedAndPassingOnLeadIsRejected()
    {
        var pair = Combo(N(8, CardSuit.Jade), N(8, CardSuit.Sword));
        var hand = new[] { pair.Cards[0], pair.Cards[1], N(2, CardSuit.Jade) };

        var play = MoveRules.ValidatePlay(hand, pair);
        var pass = MoveRules.ValidatePass(hand, currentTop: null);

        Assert.True(play.IsLegal);
        Assert.Same(pair, play.ResolvedPlay);
        Assert.Equal(MoveValidationStatus.PassNotAllowedOnLead, pass.Status);
    }

    [Fact]
    public void ResponseMustBeatCurrentPlayAndUseCardsFromHand()
    {
        var top = Combo(N(7, CardSuit.Jade), N(7, CardSuit.Sword));
        var lowerPair = Combo(N(6, CardSuit.Jade), N(6, CardSuit.Sword));
        var higherPair = Combo(N(8, CardSuit.Jade), N(8, CardSuit.Sword));
        var hand = lowerPair.Cards.Concat(higherPair.Cards).ToArray();
        var absentCard = Combo(N(2, CardSuit.Jade));

        Assert.Equal(MoveValidationStatus.CannotBeatCurrentPlay, MoveRules.ValidatePlay(hand, lowerPair, top).Status);
        Assert.Equal(MoveValidationStatus.CardsNotInHand, MoveRules.ValidatePlay(higherPair.Cards, absentCard, top).Status);
        Assert.True(MoveRules.ValidatePlay(hand, higherPair, top).IsLegal);
    }

    [Fact]
    public void BombCanAnswerAnyCombinationAndOutOfTurnPlayMustBeBomb()
    {
        var top = Combo(new Card(CardRank.Dragon));
        var bomb = Combo(N(9, CardSuit.Jade), N(9, CardSuit.Sword), N(9, CardSuit.Pagoda), N(9, CardSuit.Star));
        var single = Combo(N(14, CardSuit.Jade));
        var hand = bomb.Cards.Concat(single.Cards).ToArray();

        Assert.True(MoveRules.ValidatePlay(hand, bomb, top).IsLegal);
        Assert.Equal(
            MoveValidationStatus.OutOfTurnMoveMustBeBomb,
            MoveRules.ValidatePlay(hand, single, top, isOutOfTurn: true).Status);
        Assert.True(MoveRules.ValidatePlay(hand, bomb, top, isOutOfTurn: true).IsLegal);
        Assert.Equal(
            MoveValidationStatus.NoTrickToBomb,
            MoveRules.ValidatePlay(hand, bomb, currentTop: null, isOutOfTurn: true).Status);
    }

    [Fact]
    public void PhoenixResponseIsResolvedAgainstTheCurrentSingle()
    {
        var phoenix = Combo(new Card(CardRank.Phoenix));
        var eight = Combo(N(8, CardSuit.Jade));
        var hand = new[] { new Card(CardRank.Phoenix) };

        var result = MoveRules.ValidatePlay(hand, phoenix, eight);

        Assert.True(result.IsLegal);
        Assert.Equal(8.5m, result.ResolvedPlay!.Strength);
    }

    [Fact]
    public void MahJongWishMustBeUsedWhenItsRankCanLegallyRespond()
    {
        var top = Combo(N(5, CardSuit.Jade));
        var wished = Combo(N(7, CardSuit.Jade));
        var other = Combo(N(8, CardSuit.Jade));
        var hand = new[] { N(7, CardSuit.Jade), N(8, CardSuit.Jade) };

        Assert.Equal(MoveValidationStatus.MustFulfillWish, MoveRules.ValidatePlay(hand, other, top, CardRank.Seven).Status);
        Assert.True(MoveRules.ValidatePlay(hand, wished, top, CardRank.Seven).IsLegal);
        Assert.Equal(MoveValidationStatus.MustFulfillWish, MoveRules.ValidatePass(hand, top, CardRank.Seven).Status);
    }

    [Fact]
    public void WishDoesNotForceAnUnplayableRankButPassRemainsAvailable()
    {
        var top = Combo(N(10, CardSuit.Jade), N(10, CardSuit.Sword));
        var queens = Combo(N(12, CardSuit.Jade), N(12, CardSuit.Sword));
        var hand = new[] { N(7, CardSuit.Jade), queens.Cards[0], queens.Cards[1] };

        Assert.True(MoveRules.ValidatePlay(hand, queens, top, CardRank.Seven).IsLegal);
        Assert.True(MoveRules.ValidatePass(hand, top, CardRank.Seven).IsLegal);
    }

    [Fact]
    public void WishMustBeFulfilledByAPlayableBombOnAnOrdinaryTurn()
    {
        var top = Combo(new Card(CardRank.Dragon));
        var wishedBomb = Combo(N(7, CardSuit.Jade), N(7, CardSuit.Sword), N(7, CardSuit.Pagoda), N(7, CardSuit.Star));
        var otherBomb = Combo(N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star));
        var hand = wishedBomb.Cards.Concat(otherBomb.Cards).ToArray();

        Assert.Equal(
            MoveValidationStatus.MustFulfillWish,
            MoveRules.ValidatePlay(hand, otherBomb, top, CardRank.Seven).Status);
        Assert.True(MoveRules.ValidatePlay(hand, wishedBomb, top, CardRank.Seven).IsLegal);
    }

    [Fact]
    public void WishCanBeFulfilledByAStraightFlushBombEvenWhenItsSingleCannotBeatTheDragon()
    {
        var top = Combo(new Card(CardRank.Dragon));
        var straightFlush = Combo(
            N(5, CardSuit.Jade), N(6, CardSuit.Jade), N(7, CardSuit.Jade),
            N(8, CardSuit.Jade), N(9, CardSuit.Jade));

        var pass = MoveRules.ValidatePass(straightFlush.Cards, top, CardRank.Seven);

        Assert.Equal(MoveValidationStatus.MustFulfillWish, pass.Status);
        Assert.True(straightFlush.Beats(top));
    }

    [Fact]
    public void OutOfTurnBombDoesNotHaveToFulfillWish()
    {
        var top = Combo(new Card(CardRank.Dragon));
        var wishedBomb = Combo(N(7, CardSuit.Jade), N(7, CardSuit.Sword), N(7, CardSuit.Pagoda), N(7, CardSuit.Star));
        var otherBomb = Combo(N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star));
        var hand = wishedBomb.Cards.Concat(otherBomb.Cards).ToArray();

        Assert.True(MoveRules.ValidatePlay(hand, otherBomb, top, CardRank.Seven, isOutOfTurn: true).IsLegal);
    }

    [Fact]
    public void OptimizedWishSearchMatchesExhaustiveCombinationOracle()
    {
        var random = new Random(20261019);
        var deck = TichuDeck.CreateStandard().ToArray();

        for (var iteration = 0; iteration < 32; iteration++)
        {
            Shuffle(deck, random);
            var hand = deck.Take(14).ToArray();
            var outsideHand = deck.Skip(14).ToArray();
            var wishedRank = (CardRank)random.Next((int)CardRank.Two, (int)CardRank.Ace + 1);
            var currentTop = RandomCombination(outsideHand, random);

            var expected = ExhaustiveHasLegalWishPlay(hand, wishedRank, currentTop);
            var actual = MoveRules.ValidatePass(hand, currentTop, wishedRank).Status ==
                         MoveValidationStatus.MustFulfillWish;

            Assert.True(expected == actual,
                $"Wish search disagreed on iteration {iteration}, wish {wishedRank}, top {currentTop.Type}/{currentTop.CardCount}/{currentTop.Strength}.");
        }
    }

    [Fact]
    public void DogTransferHasNoResponseOrPassAction()
    {
        var dog = Combo(new Card(CardRank.Dog));
        var ace = Combo(N(14, CardSuit.Jade));
        var hand = new[] { ace.Cards[0] };

        Assert.Equal(MoveValidationStatus.NoResponseToDog, MoveRules.ValidatePlay(hand, ace, dog).Status);
        Assert.Equal(MoveValidationStatus.NoResponseToDog, MoveRules.ValidatePass(hand, dog).Status);
    }

    [Fact]
    public void MahJongWishMustBeAnOrdinaryRank()
    {
        var hand = new[] { N(8, CardSuit.Jade) };
        var play = Combo(hand[0]);

        Assert.Throws<ArgumentOutOfRangeException>(() => MoveRules.ValidatePlay(hand, play, pendingWish: CardRank.Phoenix));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoveRules.ValidatePass(hand, play, CardRank.MahJong));
    }

    [Fact]
    public void DuplicateCardsInHandAreRejectedAsInvalidState()
    {
        var eight = N(8, CardSuit.Jade);
        var play = Combo(eight);

        Assert.Throws<ArgumentException>(() => MoveRules.ValidatePlay(new[] { eight, eight }, play));
        Assert.Throws<ArgumentException>(() => MoveRules.ValidatePass(new[] { eight, eight }, play));
        Assert.Throws<ArgumentException>(() => MoveRules.ValidatePlay(new[] { default(Card), eight }, play));
        Assert.Throws<ArgumentException>(() => MoveRules.ValidatePass(new[] { default(Card), eight }, play));
    }

    private static CardCombination Combo(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination));
        return combination!;
    }

    private static CardCombination RandomCombination(Card[] cards, Random random)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var count = random.Next(1, 8);
            var candidateCards = cards.OrderBy(_ => random.Next()).Take(count).ToArray();
            if (!CardCombination.TryClassify(candidateCards, out var combination) ||
                combination!.Type == CombinationType.Single && combination.Cards[0].Rank == CardRank.Dog)
            {
                continue;
            }

            return combination;
        }

        return Combo(cards.First(card => card.Rank != CardRank.Dog));
    }

    private static bool ExhaustiveHasLegalWishPlay(Card[] hand, CardRank wishedRank, CardCombination currentTop)
    {
        var wishedIndices = hand.Select((card, index) => (card, index))
            .Where(item => item.card.Rank == wishedRank)
            .Select(item => item.index);
        foreach (var wishedIndex in wishedIndices)
        {
            var remaining = hand.Where((_, index) => index != wishedIndex).ToArray();
            var selection = new List<Card> { hand[wishedIndex] };
            if (ExhaustiveHasBeatingCombination(remaining, 0, selection, currentTop))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ExhaustiveHasBeatingCombination(
        Card[] remaining,
        int index,
        List<Card> selection,
        CardCombination currentTop)
    {
        if (CardCombination.ClassifyAll(selection).Any(combination => combination.Beats(currentTop)))
        {
            return true;
        }

        for (var candidateIndex = index; candidateIndex < remaining.Length; candidateIndex++)
        {
            selection.Add(remaining[candidateIndex]);
            if (ExhaustiveHasBeatingCombination(remaining, candidateIndex + 1, selection, currentTop))
            {
                return true;
            }

            selection.RemoveAt(selection.Count - 1);
        }

        return false;
    }

    private static void Shuffle<T>(T[] items, Random random)
    {
        for (var index = items.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
