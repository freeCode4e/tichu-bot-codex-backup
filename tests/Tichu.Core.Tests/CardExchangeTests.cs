using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class CardExchangeTests
{
    [Fact]
    public void ApplyPassesOneCardToEachOtherSeatAndPreservesTheWholeDeck()
    {
        var (hands, selections) = CreateInput();
        var originalHands = hands.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());

        var exchanged = CardExchange.Apply(
            hands.ToDictionary(pair => pair.Key, pair => (IEnumerable<Card>)pair.Value),
            selections);

        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var outgoing = selections[seat];
            Assert.Equal(14, exchanged[seat].Count);
            Assert.DoesNotContain(outgoing.ToRight, exchanged[seat]);
            Assert.DoesNotContain(outgoing.ToPartner, exchanged[seat]);
            Assert.DoesNotContain(outgoing.ToLeft, exchanged[seat]);

            Assert.Contains(outgoing.ToRight, exchanged[seat.ToTheRight()]);
            Assert.Contains(outgoing.ToPartner, exchanged[seat.Partner()]);
            Assert.Contains(outgoing.ToLeft, exchanged[seat.ToTheLeft()]);
            Assert.Equal(originalHands[seat], hands[seat]);
        }

        var allReceivedCards = exchanged.Values.SelectMany(hand => hand).ToArray();
        Assert.Equal(TichuDeck.CardCount, allReceivedCards.Length);
        Assert.Equal(TichuDeck.CardCount, allReceivedCards.Distinct().Count());
        Assert.Equal(TichuDeck.CreateStandard().OrderBy(card => card.Rank).ThenBy(card => card.Suit),
            allReceivedCards.OrderBy(card => card.Rank).ThenBy(card => card.Suit));
    }

    [Fact]
    public void ApplyRejectsPassesWithDuplicateOrUnownedCards()
    {
        var (hands, selections) = CreateInput();
        var seat = PlayerSeat.Bottom;
        var original = selections[seat];
        selections[seat] = new CardPassSelection(original.ToRight, original.ToRight, original.ToLeft);

        Assert.Throws<ArgumentException>(() => CardExchange.Apply(
            hands.ToDictionary(pair => pair.Key, pair => (IEnumerable<Card>)pair.Value),
            selections));

        var (otherHands, otherSelections) = CreateInput();
        var otherSeat = PlayerSeat.Right;
        var rightPass = otherSelections[otherSeat];
        otherSelections[otherSeat] = rightPass with { ToPartner = otherHands[PlayerSeat.Top][0] };

        Assert.Throws<ArgumentException>(() => CardExchange.Apply(
            otherHands.ToDictionary(pair => pair.Key, pair => (IEnumerable<Card>)pair.Value),
            otherSelections));
    }

    [Fact]
    public void ApplyRequiresAll56UniqueDeckCardsAndFourteenCardsPerSeat()
    {
        var (hands, selections) = CreateInput();
        hands[PlayerSeat.Bottom] = hands[PlayerSeat.Bottom][..13];

        Assert.Throws<ArgumentException>(() => CardExchange.Apply(
            hands.ToDictionary(pair => pair.Key, pair => (IEnumerable<Card>)pair.Value),
            selections));
    }

    private static (Dictionary<PlayerSeat, Card[]> Hands, Dictionary<PlayerSeat, CardPassSelection> Selections) CreateInput()
    {
        var deck = TichuDeck.CreateStandard();
        var seats = Enum.GetValues<PlayerSeat>();
        var hands = seats.ToDictionary(
            seat => seat,
            seat => deck.Skip(seat.ToIndex() * 14).Take(14).ToArray());
        var selections = seats.ToDictionary(
            seat => seat,
            seat => new CardPassSelection(
                hands[seat][0],
                hands[seat][1],
                hands[seat][2]));
        return (hands, selections);
    }
}
