using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class CardCombinationTests
{
    [Fact]
    public void StandardDeck_HasAll56UniqueCardsAndFourSpecials()
    {
        var deck = TichuDeck.CreateStandard();

        Assert.Equal(TichuDeck.CardCount, deck.Count);
        Assert.Equal(56, deck.Distinct().Count());
        Assert.Equal(52, deck.Count(card => card.IsOrdinary));
        Assert.Equal(1, deck.Count(card => card.Rank == CardRank.MahJong));
        Assert.Equal(1, deck.Count(card => card.Rank == CardRank.Phoenix));
        Assert.Equal(1, deck.Count(card => card.Rank == CardRank.Dragon));
        Assert.Equal(1, deck.Count(card => card.Rank == CardRank.Dog));
    }

    [Fact]
    public void CardPointValues_MatchTichuScoringAndDeckTotalsOneHundred()
    {
        var deck = TichuDeck.CreateStandard();

        Assert.Equal(5, CardScoring.Value(N(5, CardSuit.Jade)));
        Assert.Equal(10, CardScoring.Value(N(10, CardSuit.Sword)));
        Assert.Equal(10, CardScoring.Value(N(13, CardSuit.Pagoda)));
        Assert.Equal(25, CardScoring.Value(new Card(CardRank.Dragon)));
        Assert.Equal(-25, CardScoring.Value(new Card(CardRank.Phoenix)));
        Assert.Equal(0, CardScoring.Value(new Card(CardRank.MahJong)));
        Assert.Equal(0, CardScoring.Value(new Card(CardRank.Dog)));
        Assert.Equal(0, CardScoring.Value(N(14, CardSuit.Star)));
        Assert.Equal(100, CardScoring.Total(deck));
    }

    [Fact]
    public void Card_RequiresSuitsOnlyForOrdinaryCards()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Card(CardRank.Eight));
        Assert.Throws<ArgumentException>(() => new Card(CardRank.Phoenix, CardSuit.Jade));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Card(CardRank.Eight, (CardSuit)9));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Card((CardRank)99));
    }

    [Fact]
    public void DefaultCardValueIsRejectedEvenThoughItBypassesTheConstructor()
    {
        var defaultCard = default(Card);

        Assert.False(defaultCard.IsValid);
        Assert.False(CardCombination.TryClassify(new[] { defaultCard }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardScoring.Value(defaultCard));
    }

    [Theory]
    [InlineData(CardRank.MahJong)]
    [InlineData(CardRank.Phoenix)]
    [InlineData(CardRank.Dragon)]
    [InlineData(CardRank.Dog)]
    public void EverySpecialCardIsAValidSingle(CardRank rank)
    {
        var combination = Classify(new Card(rank));

        Assert.Equal(CombinationType.Single, combination.Type);
        Assert.Equal(1, combination.CardCount);
    }

    [Fact]
    public void PairAndTriple_AcceptPhoenixAsTheMissingNormalRank()
    {
        var pair = Classify(N(7, CardSuit.Jade), new Card(CardRank.Phoenix));
        var triple = Classify(N(11, CardSuit.Jade), N(11, CardSuit.Sword), new Card(CardRank.Phoenix));

        Assert.Equal(CombinationType.Pair, pair.Type);
        Assert.Equal(7m, pair.Strength);
        Assert.Equal(CombinationType.Triple, triple.Type);
        Assert.Equal(11m, triple.Strength);
    }

    [Fact]
    public void PairAndTriple_RejectDifferentRanksAndSpecialRankSubstitutions()
    {
        Reject(N(7, CardSuit.Jade), N(8, CardSuit.Sword));
        Reject(new Card(CardRank.MahJong), new Card(CardRank.Phoenix));
        Reject(N(7, CardSuit.Jade), N(7, CardSuit.Sword), N(8, CardSuit.Jade));
        Reject(N(7, CardSuit.Jade), N(7, CardSuit.Sword), new Card(CardRank.Dragon));
    }

    [Fact]
    public void ConsecutivePairs_AcceptTwoOrMoreAdjacentPairsAndPhoenixCompletion()
    {
        var twoPairs = Classify(N(6, CardSuit.Jade), N(6, CardSuit.Sword), N(7, CardSuit.Jade), N(7, CardSuit.Sword));
        var phoenixPair = Classify(N(6, CardSuit.Jade), N(6, CardSuit.Sword), N(7, CardSuit.Jade), new Card(CardRank.Phoenix));
        var threePairs = Classify(
            N(8, CardSuit.Jade), N(8, CardSuit.Sword),
            N(9, CardSuit.Jade), N(9, CardSuit.Sword),
            N(10, CardSuit.Jade), N(10, CardSuit.Sword));

        Assert.Equal(CombinationType.ConsecutivePairs, twoPairs.Type);
        Assert.Equal(7m, twoPairs.Strength);
        Assert.Equal(CombinationType.ConsecutivePairs, phoenixPair.Type);
        Assert.Equal(7m, phoenixPair.Strength);
        Assert.Equal(CombinationType.ConsecutivePairs, threePairs.Type);
        Assert.Equal(10m, threePairs.Strength);
    }

    [Fact]
    public void ConsecutivePairs_RejectGapsOddCountsAndMahJong()
    {
        Reject(N(6, CardSuit.Jade), N(6, CardSuit.Sword), N(8, CardSuit.Jade), N(8, CardSuit.Sword));
        Reject(N(6, CardSuit.Jade), N(6, CardSuit.Sword), N(7, CardSuit.Jade));
        Reject(new Card(CardRank.MahJong), N(2, CardSuit.Jade), N(2, CardSuit.Sword), N(3, CardSuit.Jade));
    }

    [Fact]
    public void FullHouse_AcceptsPhoenixInEitherGroupAndRanksByTriple()
    {
        var phoenixInTriple = Classify(
            N(9, CardSuit.Jade), N(9, CardSuit.Sword), new Card(CardRank.Phoenix),
            N(4, CardSuit.Jade), N(4, CardSuit.Sword));
        var phoenixInPair = Classify(
            N(9, CardSuit.Jade), N(9, CardSuit.Sword), N(9, CardSuit.Pagoda),
            N(4, CardSuit.Jade), new Card(CardRank.Phoenix));

        Assert.Equal(CombinationType.FullHouse, phoenixInTriple.Type);
        Assert.Equal(9m, phoenixInTriple.Strength);
        Assert.Equal(CombinationType.FullHouse, phoenixInPair.Type);
        Assert.Equal(9m, phoenixInPair.Strength);
    }

    [Fact]
    public void ClassifyAll_PreservesAlternativePhoenixAssignments()
    {
        var fullHouseOptions = CardCombination.ClassifyAll(new[]
        {
            N(9, CardSuit.Jade), N(9, CardSuit.Sword), new Card(CardRank.Phoenix),
            N(4, CardSuit.Jade), N(4, CardSuit.Sword)
        });
        var straightOptions = CardCombination.ClassifyAll(new[]
        {
            N(4, CardSuit.Jade), N(5, CardSuit.Sword), N(6, CardSuit.Pagoda), N(7, CardSuit.Star),
            new Card(CardRank.Phoenix)
        });

        Assert.Equal(new[] { 4m, 9m }, fullHouseOptions.Select(option => option.Strength).Order());
        Assert.Contains(fullHouseOptions, option => option.PhoenixAsRank == CardRank.Nine);
        Assert.Contains(fullHouseOptions, option => option.PhoenixAsRank == CardRank.Four);
        Assert.Equal(new[] { 7m, 8m }, straightOptions.Select(option => option.Strength).Order());
        Assert.Equal(new[] { CardRank.Three, CardRank.Eight }, straightOptions.Select(option => option.PhoenixAsRank!.Value).Order());
    }

    [Fact]
    public void FullHouse_RejectsFourOfAKindPlusPhoenixAndOtherRankCounts()
    {
        Reject(N(3, CardSuit.Jade), N(3, CardSuit.Sword), N(3, CardSuit.Pagoda), N(3, CardSuit.Star), new Card(CardRank.Phoenix));
        Reject(N(3, CardSuit.Jade), N(3, CardSuit.Sword), N(4, CardSuit.Jade), N(4, CardSuit.Sword), N(5, CardSuit.Jade));
    }

    [Fact]
    public void Straight_AcceptsMahJongAndPhoenixAtAnOpenEndOrGap()
    {
        var mahJongLow = Classify(new Card(CardRank.MahJong), N(2, CardSuit.Jade), N(3, CardSuit.Sword), N(4, CardSuit.Pagoda), N(5, CardSuit.Star));
        var phoenixGap = Classify(N(4, CardSuit.Jade), N(5, CardSuit.Sword), new Card(CardRank.Phoenix), N(7, CardSuit.Pagoda), N(8, CardSuit.Star));
        var phoenixLow = Classify(new Card(CardRank.Phoenix), N(3, CardSuit.Jade), N(4, CardSuit.Sword), N(5, CardSuit.Pagoda), N(6, CardSuit.Star));
        var ambiguousPhoenix = Classify(N(4, CardSuit.Jade), N(5, CardSuit.Sword), N(6, CardSuit.Pagoda), N(7, CardSuit.Star), new Card(CardRank.Phoenix));

        Assert.Equal(CombinationType.Straight, mahJongLow.Type);
        Assert.Equal(5m, mahJongLow.Strength);
        Assert.Equal(CombinationType.Straight, phoenixGap.Type);
        Assert.Equal(8m, phoenixGap.Strength);
        Assert.Equal(CombinationType.Straight, phoenixLow.Type);
        Assert.Equal(7m, phoenixLow.Strength);
        Assert.Equal(8m, ambiguousPhoenix.Strength);
    }

    [Fact]
    public void Straight_RejectsShortRunsGapsDuplicatesAndOtherSpecialCards()
    {
        Reject(N(2, CardSuit.Jade), N(3, CardSuit.Sword), N(4, CardSuit.Pagoda), N(5, CardSuit.Star));
        Reject(N(2, CardSuit.Jade), N(3, CardSuit.Sword), N(5, CardSuit.Pagoda), N(6, CardSuit.Star), N(7, CardSuit.Jade));
        Reject(N(2, CardSuit.Jade), N(2, CardSuit.Sword), N(3, CardSuit.Pagoda), N(4, CardSuit.Star), N(5, CardSuit.Jade));
        Reject(N(2, CardSuit.Jade), N(3, CardSuit.Sword), N(4, CardSuit.Pagoda), N(5, CardSuit.Star), new Card(CardRank.Dragon));
    }

    [Fact]
    public void Bombs_AcceptFourOfAKindAndStraightFlushButPhoenixCannotCompleteThem()
    {
        var fourOfAKind = Classify(
            N(12, CardSuit.Jade), N(12, CardSuit.Sword), N(12, CardSuit.Pagoda), N(12, CardSuit.Star));
        var straightFlush = Classify(
            N(5, CardSuit.Jade), N(6, CardSuit.Jade), N(7, CardSuit.Jade), N(8, CardSuit.Jade), N(9, CardSuit.Jade));

        Assert.Equal(CombinationType.FourOfAKindBomb, fourOfAKind.Type);
        Assert.Equal(12m, fourOfAKind.Strength);
        Assert.Equal(CombinationType.StraightFlushBomb, straightFlush.Type);
        Assert.Equal(9m, straightFlush.Strength);
        var phoenixStraight = Classify(N(5, CardSuit.Jade), N(6, CardSuit.Jade), N(7, CardSuit.Jade), N(8, CardSuit.Jade), new Card(CardRank.Phoenix));
        Assert.Equal(CombinationType.Straight, phoenixStraight.Type);
        Assert.False(phoenixStraight.IsBomb);
        Reject(N(5, CardSuit.Jade), N(7, CardSuit.Jade), N(8, CardSuit.Jade), N(9, CardSuit.Jade), N(10, CardSuit.Jade));
    }

    [Fact]
    public void DuplicatePhysicalCardsAreRejected()
    {
        var eightOfJade = N(8, CardSuit.Jade);

        Reject(eightOfJade, eightOfJade);
    }

    [Fact]
    public void NonBombCombinationsMustMatchTypeAndSizeAndIncreaseStrength()
    {
        var pairOfSevens = Classify(N(7, CardSuit.Jade), N(7, CardSuit.Sword));
        var pairOfEights = Classify(N(8, CardSuit.Jade), N(8, CardSuit.Sword));
        var pairOfNines = Classify(N(9, CardSuit.Jade), N(9, CardSuit.Sword));
        var tripleOfEights = Classify(N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda));

        Assert.True(pairOfEights.Beats(pairOfSevens));
        Assert.False(pairOfSevens.Beats(pairOfEights));
        Assert.False(pairOfNines.Beats(tripleOfEights));
    }

    [Fact]
    public void FullHouseAndStraights_UseTheirRuleSpecificComparisonRanks()
    {
        var lowerTripleHighPair = Classify(
            N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(14, CardSuit.Jade), N(14, CardSuit.Sword));
        var higherTripleLowPair = Classify(
            N(9, CardSuit.Jade), N(9, CardSuit.Sword), N(9, CardSuit.Pagoda), N(2, CardSuit.Jade), N(2, CardSuit.Sword));
        var sixHighStraight = Classify(N(2, CardSuit.Jade), N(3, CardSuit.Sword), N(4, CardSuit.Pagoda), N(5, CardSuit.Star), N(6, CardSuit.Jade));
        var sevenHighStraight = Classify(N(3, CardSuit.Jade), N(4, CardSuit.Sword), N(5, CardSuit.Pagoda), N(6, CardSuit.Star), N(7, CardSuit.Jade));
        var sixCardStraight = Classify(N(3, CardSuit.Jade), N(4, CardSuit.Sword), N(5, CardSuit.Pagoda), N(6, CardSuit.Star), N(7, CardSuit.Jade), N(8, CardSuit.Sword));

        Assert.True(higherTripleLowPair.Beats(lowerTripleHighPair));
        Assert.True(sevenHighStraight.Beats(sixHighStraight));
        Assert.False(sixCardStraight.Beats(sevenHighStraight));
    }

    [Fact]
    public void PhoenixSingle_UsesLeadValueAndHalfRankAboveCurrentSingle()
    {
        var mahJong = Classify(new Card(CardRank.MahJong));
        var phoenixLead = Classify(new Card(CardRank.Phoenix));
        var two = Classify(N(2, CardSuit.Jade));
        var eight = Classify(N(8, CardSuit.Jade));
        var nine = Classify(N(9, CardSuit.Jade));
        var ace = Classify(N(14, CardSuit.Jade));
        var dragon = Classify(new Card(CardRank.Dragon));
        var phoenixOverEight = phoenixLead.ResolveForPlayOver(eight);
        var phoenixOverAce = phoenixLead.ResolveForPlayOver(ace);

        Assert.Equal(1.5m, phoenixLead.Strength);
        Assert.False(mahJong.Beats(phoenixLead));
        Assert.True(two.Beats(phoenixLead));
        Assert.Equal(8.5m, phoenixOverEight.Strength);
        Assert.True(nine.Beats(phoenixOverEight));
        Assert.Equal(14.5m, phoenixOverAce.Strength);
        Assert.False(ace.Beats(phoenixOverAce));
        Assert.True(dragon.Beats(phoenixOverAce));
        Assert.False(phoenixLead.Beats(dragon));
    }

    [Fact]
    public void Bombs_BeatOrdinaryPlaysAndFollowBombHierarchy()
    {
        var fullHouse = Classify(
            N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(4, CardSuit.Jade), N(4, CardSuit.Sword));
        var lowerFourKind = Classify(N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star));
        var higherFourKind = Classify(N(9, CardSuit.Jade), N(9, CardSuit.Sword), N(9, CardSuit.Pagoda), N(9, CardSuit.Star));
        var shortStraightFlush = Classify(N(10, CardSuit.Jade), N(11, CardSuit.Jade), N(12, CardSuit.Jade), N(13, CardSuit.Jade), N(14, CardSuit.Jade));
        var longerLowerStraightFlush = Classify(N(2, CardSuit.Sword), N(3, CardSuit.Sword), N(4, CardSuit.Sword), N(5, CardSuit.Sword), N(6, CardSuit.Sword), N(7, CardSuit.Sword));
        var longerHigherStraightFlush = Classify(N(3, CardSuit.Pagoda), N(4, CardSuit.Pagoda), N(5, CardSuit.Pagoda), N(6, CardSuit.Pagoda), N(7, CardSuit.Pagoda), N(8, CardSuit.Pagoda));

        Assert.True(lowerFourKind.Beats(fullHouse));
        Assert.True(higherFourKind.Beats(lowerFourKind));
        Assert.True(shortStraightFlush.Beats(higherFourKind));
        Assert.False(higherFourKind.Beats(shortStraightFlush));
        Assert.True(longerLowerStraightFlush.Beats(shortStraightFlush));
        Assert.True(longerHigherStraightFlush.Beats(longerLowerStraightFlush));
    }

    [Fact]
    public void DogSingle_CannotBeAnsweredOrBombed()
    {
        var dog = Classify(new Card(CardRank.Dog));
        var ace = Classify(N(14, CardSuit.Jade));
        var fourKind = Classify(N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star));

        Assert.False(ace.Beats(dog));
        Assert.False(fourKind.Beats(dog));
        Assert.False(dog.Beats(ace));
    }

    private static CardCombination Classify(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination),
            $"Expected a valid combination from: {string.Join(", ", cards.Select(card => $"{card.Rank}/{card.Suit}"))}");
        return combination!;
    }

    private static void Reject(params Card[] cards) =>
        Assert.False(CardCombination.TryClassify(cards, out _),
            $"Expected an invalid combination from: {string.Join(", ", cards.Select(card => $"{card.Rank}/{card.Suit}"))}");

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
