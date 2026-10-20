using System.Collections.ObjectModel;

namespace Tichu.Core;

/// <summary>A reproducible deal, before the three-card exchange.</summary>
public sealed class TichuDeal
{
    internal TichuDeal(Card[] deckOrder, Card[][] initialEight, Card[][] fourteenCardHands)
    {
        DeckOrder = Array.AsReadOnly(deckOrder.ToArray());
        InitialEightHands = MakeReadOnlyHands(initialEight);
        FourteenCardHands = MakeReadOnlyHands(fourteenCardHands);
    }

    /// <summary>The exact shuffled order, retained so the deal can be replayed.</summary>
    public IReadOnlyList<Card> DeckOrder { get; }

    /// <summary>The first eight cards received by each seat, before Grand decisions.</summary>
    public IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> InitialEightHands { get; }

    /// <summary>All fourteen dealt cards for each seat, before passing.</summary>
    public IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> FourteenCardHands { get; }

    /// <summary>The six cards dealt after the initial eight, in deal order.</summary>
    public IReadOnlyList<Card> AdditionalSixFor(PlayerSeat seat)
    {
        seat.ToIndex();
        var fullHand = FourteenCardHands[seat];
        return Array.AsReadOnly(fullHand.Skip(8).ToArray());
    }

    private static IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> MakeReadOnlyHands(Card[][] hands)
    {
        var copy = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => (IReadOnlyList<Card>)Array.AsReadOnly(hands[seat.ToIndex()].ToArray()));
        return new ReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>>(copy);
    }
}

/// <summary>Creates or replays complete 56-card Tichu deals.</summary>
public static class TichuDealer
{
    /// <summary>Shuffles with the caller's random source, then deals eight plus six cards per seat.</summary>
    public static TichuDeal CreateShuffled(Random random)
    {
        ArgumentNullException.ThrowIfNull(random);
        var deck = TichuDeck.CreateStandard().ToArray();
        for (var index = deck.Length - 1; index > 0; index--)
        {
            var swapIndex = random.Next(index + 1);
            (deck[index], deck[swapIndex]) = (deck[swapIndex], deck[index]);
        }

        return FromDeckOrder(deck);
    }

    /// <summary>Creates a reproducible deal from a fixed seed.</summary>
    public static TichuDeal CreateShuffled(int seed) => CreateShuffled(new Random(seed));

    /// <summary>Replays a known order, rejecting anything other than the full standard deck.</summary>
    public static TichuDeal FromDeckOrder(IEnumerable<Card> deckOrder)
    {
        ArgumentNullException.ThrowIfNull(deckOrder);
        var cards = deckOrder.ToArray();
        var standardDeck = TichuDeck.CreateStandard().ToHashSet();
        if (cards.Length != TichuDeck.CardCount || cards.Any(card => !card.IsValid) ||
            cards.Distinct().Count() != TichuDeck.CardCount || !standardDeck.SetEquals(cards))
        {
            throw new ArgumentException("Deck order must contain every card in the standard 56-card deck exactly once.", nameof(deckOrder));
        }

        var initialEight = Enumerable.Range(0, 4).Select(_ => new Card[8]).ToArray();
        var fourteenCardHands = Enumerable.Range(0, 4).Select(_ => new Card[14]).ToArray();
        for (var dealRound = 0; dealRound < 14; dealRound++)
        {
            for (var seatIndex = 0; seatIndex < 4; seatIndex++)
            {
                var card = cards[dealRound * 4 + seatIndex];
                fourteenCardHands[seatIndex][dealRound] = card;
                if (dealRound < 8)
                {
                    initialEight[seatIndex][dealRound] = card;
                }
            }
        }

        return new TichuDeal(cards, initialEight, fourteenCardHands);
    }
}
