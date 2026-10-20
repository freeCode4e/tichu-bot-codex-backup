using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class MoveGeneratorTests
{
    [Fact]
    public void LeadGeneratesPlaysAndAllMahJongWishChoicesButNoPass()
    {
        var mahJong = new Card(CardRank.MahJong);
        var hand = new[]
        {
            mahJong, N(2, CardSuit.Jade), N(3, CardSuit.Jade),
            N(4, CardSuit.Pagoda), N(5, CardSuit.Star), N(8, CardSuit.Sword)
        };

        var moves = MoveGenerator.Generate(hand);
        var mahJongSingles = moves.Where(move =>
            move.Combination?.Type == CombinationType.Single &&
            move.Combination.Cards[0].Rank == CardRank.MahJong).ToArray();

        Assert.DoesNotContain(moves, move => move.IsPass);
        Assert.Equal(14, mahJongSingles.Length);
        Assert.Contains(mahJongSingles, move => move.WishForMahJong is null);
        Assert.Equal(13, mahJongSingles.Where(move => move.WishForMahJong is not null)
            .Select(move => move.WishForMahJong).Distinct().Count());
        Assert.Contains(moves, move => move.Combination?.Type == CombinationType.Straight);
    }

    [Fact]
    public void ResponseContainsOnlyBeatingPlaysAndPassWhenPassIsAllowed()
    {
        var hand = new[]
        {
            N(7, CardSuit.Jade), N(9, CardSuit.Jade),
            new Card(CardRank.Phoenix), new Card(CardRank.Dragon)
        };
        var top = Combo(N(8, CardSuit.Sword));

        var moves = MoveGenerator.Generate(hand, top);

        Assert.Contains(moves, move => move.Combination?.Cards.Single().Rank == CardRank.Nine);
        Assert.Contains(moves, move => move.Combination?.Cards.Single().Rank == CardRank.Phoenix);
        Assert.Contains(moves, move => move.Combination?.Cards.Single().Rank == CardRank.Dragon);
        Assert.DoesNotContain(moves, move => move.Combination?.Cards.Single().Rank == CardRank.Seven);
        Assert.Contains(moves, move => move.IsPass);
    }

    [Fact]
    public void OnTurnBombsRemainTurnPlaysAndAreCheckedAgainstWish()
    {
        var fives = Enum.GetValues<CardSuit>().Select(suit => N(5, suit)).ToArray();
        var hand = fives.Append(N(9, CardSuit.Jade)).ToArray();
        var top = Combo(new Card(CardRank.Dragon));

        var moves = MoveGenerator.Generate(hand, top, CardRank.Nine);

        var bomb = Assert.Single(moves, move => move.Combination?.IsBomb == true);
        Assert.False(bomb.IsOutOfTurnBomb);
        Assert.True(MoveRules.ValidatePlay(hand, bomb.Combination!, top, CardRank.Nine).IsLegal);
    }

    [Fact]
    public void OutOfTurnGeneratesOnlyBombsAndMayBypassWish()
    {
        var fives = Enum.GetValues<CardSuit>().Select(suit => N(5, suit)).ToArray();
        var hand = fives.Append(N(9, CardSuit.Jade)).ToArray();
        var top = Combo(new Card(CardRank.Dragon));

        var moves = MoveGenerator.Generate(hand, top, CardRank.Nine, isOutOfTurn: true);

        var bomb = Assert.Single(moves);
        Assert.True(bomb.IsOutOfTurnBomb);
        Assert.True(bomb.Combination!.IsBomb);
        Assert.DoesNotContain(moves, move => move.IsPass);
    }

    [Fact]
    public void DogHasNoResponseAndInvalidHandsAreRejected()
    {
        var dog = Combo(new Card(CardRank.Dog));
        Assert.Empty(MoveGenerator.Generate(new[] { N(14, CardSuit.Jade) }, dog));

        var eight = N(8, CardSuit.Jade);
        Assert.Throws<ArgumentException>(() => MoveGenerator.Generate(new[] { eight, eight }));
        Assert.Throws<ArgumentOutOfRangeException>(() => MoveGenerator.Generate(
            new[] { eight }, pendingWish: CardRank.MahJong));
    }

    [Fact]
    public void GeneratorMatchesExhaustiveOracleAcrossRandomHandsAndTurnModes()
    {
        var random = new Random(20261020);
        var deck = TichuDeck.CreateStandard().ToArray();

        for (var iteration = 0; iteration < 5; iteration++)
        {
            Shuffle(deck, random);
            var hand = deck.Take(14).ToArray();
            var outsideHand = deck.Skip(14).ToArray();
            var top = RandomCombination(outsideHand, random);
            var wish = (CardRank)random.Next((int)CardRank.Two, (int)CardRank.Ace + 1);

            AssertGeneratorMatchesOracle(hand, currentTop: null, pendingWish: null, isOutOfTurn: false);
            AssertGeneratorMatchesOracle(hand, top, wish, isOutOfTurn: false);
            AssertGeneratorMatchesOracle(hand, top, wish, isOutOfTurn: true);
        }
    }

    private static void AssertGeneratorMatchesOracle(
        Card[] hand,
        CardCombination? currentTop,
        CardRank? pendingWish,
        bool isOutOfTurn)
    {
        var expected = new HashSet<MoveIdentity>();
        var maskLimit = 1 << hand.Length;
        for (var subsetMask = 1; subsetMask < maskLimit; subsetMask++)
        {
            var cards = hand.Where((_, index) => (subsetMask & (1 << index)) != 0).ToArray();
            foreach (var combination in CardCombination.ClassifyAll(cards))
            {
                if (isOutOfTurn && !combination.IsBomb)
                {
                    continue;
                }

                if (!MoveRules.ValidatePlay(hand, combination, currentTop, pendingWish, isOutOfTurn).IsLegal)
                {
                    continue;
                }

                var wishes = combination.Cards.Any(card => card.Rank == CardRank.MahJong)
                    ? new CardRank?[] { null }.Concat(
                        Enumerable.Range((int)CardRank.Two, (int)CardRank.Ace - (int)CardRank.Two + 1)
                            .Select(rank => (CardRank?)rank)).ToArray()
                    : new CardRank?[] { null };
                foreach (var mahJongWish in wishes)
                {
                    expected.Add(Identify(subsetMask, combination.Type, combination.PhoenixAsRank,
                        mahJongWish, isOutOfTurn));
                }
            }
        }

        if (!isOutOfTurn && currentTop is not null &&
            MoveRules.ValidatePass(hand, currentTop, pendingWish).IsLegal)
        {
            expected.Add(Identify(0, null, null, null, false));
        }

        var actual = MoveGenerator.Generate(hand, currentTop, pendingWish, isOutOfTurn)
            .Select(move => move.IsPass
                ? Identify(0, null, null, null, false)
                : Identify(MaskOf(move.Combination!.Cards, hand), move.Combination.Type,
                    move.Combination.PhoenixAsRank, move.WishForMahJong, move.IsOutOfTurnBomb))
            .ToHashSet();

        Assert.True(expected.SetEquals(actual),
            $"Move generation differed for top {currentTop?.Type}/{currentTop?.CardCount}, wish {pendingWish}, out-of-turn {isOutOfTurn}.");
    }

    private static MoveIdentity Identify(
        int mask,
        CombinationType? type,
        CardRank? phoenixAsRank,
        CardRank? wish,
        bool outOfTurn) => new((ushort)mask, type, phoenixAsRank, wish, outOfTurn);

    private static int MaskOf(IReadOnlyList<Card> cards, Card[] hand)
    {
        var mask = 0;
        foreach (var card in cards)
        {
            mask |= 1 << Array.IndexOf(hand, card);
        }

        return mask;
    }

    private static CardCombination RandomCombination(Card[] cards, Random random)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var selection = cards.OrderBy(_ => random.Next()).Take(random.Next(1, 8)).ToArray();
            if (CardCombination.TryClassify(selection, out var combination) &&
                !(combination!.Type == CombinationType.Single && combination.Cards[0].Rank == CardRank.Dog))
            {
                return combination;
            }
        }

        return Combo(cards.First(card => card.Rank != CardRank.Dog));
    }

    private static void Shuffle<T>(T[] items, Random random)
    {
        for (var index = items.Length - 1; index > 0; index--)
        {
            var other = random.Next(index + 1);
            (items[index], items[other]) = (items[other], items[index]);
        }
    }

    private readonly record struct MoveIdentity(
        ushort CardMask,
        CombinationType? Type,
        CardRank? PhoenixAsRank,
        CardRank? Wish,
        bool IsOutOfTurn);

    private static CardCombination Combo(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination));
        return combination!;
    }

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
