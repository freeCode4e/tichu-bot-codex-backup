using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class TichuDealerTests
{
    [Fact]
    public void FromDeckOrderDealsEightThenSixToEachSeatAndCanBeReplayed()
    {
        var deck = TichuDeck.CreateStandard();
        var deal = TichuDealer.FromDeckOrder(deck);

        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var expectedFullHand = deck.Where((_, index) => index % 4 == seat.ToIndex()).ToArray();
            Assert.Equal(expectedFullHand.Take(8), deal.InitialEightHands[seat]);
            Assert.Equal(expectedFullHand, deal.FourteenCardHands[seat]);
            Assert.Equal(expectedFullHand.Skip(8), deal.AdditionalSixFor(seat));
        }

        Assert.Equal(deck, deal.DeckOrder);
        Assert.Equal(TichuDeck.CardCount,
            deal.FourteenCardHands.Values.SelectMany(hand => hand).Distinct().Count());

        var replayed = TichuDealer.FromDeckOrder(deal.DeckOrder);
        Assert.Equal(deal.InitialEightHands[PlayerSeat.Left], replayed.InitialEightHands[PlayerSeat.Left]);
        Assert.Equal(deal.FourteenCardHands[PlayerSeat.Bottom], replayed.FourteenCardHands[PlayerSeat.Bottom]);
    }

    [Fact]
    public void SeededShuffleIsRepeatableAndProducesACompleteDeal()
    {
        var first = TichuDealer.CreateShuffled(20261019);
        var second = TichuDealer.CreateShuffled(20261019);

        Assert.Equal(first.DeckOrder, second.DeckOrder);
        Assert.Equal(TichuDeck.CardCount, first.DeckOrder.Distinct().Count());
        Assert.All(first.InitialEightHands.Values, hand => Assert.Equal(8, hand.Count));
        Assert.All(first.FourteenCardHands.Values, hand => Assert.Equal(14, hand.Count));
    }

    [Fact]
    public void ManySeededDealsAndExchangesConserveEveryPhysicalCard()
    {
        var standardDeck = TichuDeck.CreateStandard().ToHashSet();

        for (var seed = 0; seed < 128; seed++)
        {
            var deal = TichuDealer.CreateShuffled(seed);
            var hands = deal.FourteenCardHands.ToDictionary(
                pair => pair.Key,
                pair => (IEnumerable<Card>)pair.Value);
            var selections = deal.FourteenCardHands.ToDictionary(
                pair => pair.Key,
                pair => new CardPassSelection(pair.Value[0], pair.Value[1], pair.Value[2]));

            var exchanged = CardExchange.Apply(hands, selections);
            var receivedCards = exchanged.Values.SelectMany(hand => hand).ToArray();

            Assert.Equal(56, receivedCards.Length);
            Assert.Equal(56, receivedCards.Distinct().Count());
            Assert.True(standardDeck.SetEquals(receivedCards), $"Exchange lost or invented a card for seed {seed}.");
            Assert.All(exchanged.Values, hand => Assert.Equal(14, hand.Count));
        }
    }

    [Fact]
    public void FromDeckOrderRejectsMissingDuplicateOrInvalidCards()
    {
        var deck = TichuDeck.CreateStandard().ToArray();

        Assert.Throws<ArgumentException>(() => TichuDealer.FromDeckOrder(deck.Take(55)));
        deck[0] = deck[1];
        Assert.Throws<ArgumentException>(() => TichuDealer.FromDeckOrder(deck));
        deck[0] = default;
        Assert.Throws<ArgumentException>(() => TichuDealer.FromDeckOrder(deck));
    }
}
