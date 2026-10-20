using System.Collections.ObjectModel;

namespace Tichu.Core;

/// <summary>One player's simultaneous pass: one card to each other seat.</summary>
public sealed record CardPassSelection(Card ToRight, Card ToPartner, Card ToLeft);

/// <summary>Validates and applies the four players' simultaneous card passes.</summary>
public static class CardExchange
{
    /// <summary>
    /// Applies one pass to the right, one to the partner, and one to the left
    /// for each player. Inputs are copied; the returned hands each contain 14 cards.
    /// </summary>
    public static IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> Apply(
        IReadOnlyDictionary<PlayerSeat, IEnumerable<Card>> hands,
        IReadOnlyDictionary<PlayerSeat, CardPassSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(hands);
        ArgumentNullException.ThrowIfNull(selections);
        ValidateSeats(hands, nameof(hands));
        ValidateSeats(selections, nameof(selections));

        var handCopies = new Card[4][];
        var allCards = new HashSet<Card>();
        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            var hand = hands[seat]?.ToArray()
                ?? throw new ArgumentException($"The hand for {seat} cannot be null.", nameof(hands));
            if (hand.Length != 14 || hand.Any(card => !card.IsValid) ||
                hand.Distinct().Count() != hand.Length || hand.Any(card => !allCards.Add(card)))
            {
                throw new ArgumentException("The four hands must contain the 56 distinct cards of a standard deck, 14 per player.", nameof(hands));
            }

            handCopies[seat.ToIndex()] = hand;
        }

        if (allCards.Count != TichuDeck.CardCount)
        {
            throw new ArgumentException("The four hands must contain all 56 cards of a standard deck.", nameof(hands));
        }

        var checkedSelections = new CardPassSelection[4];
        for (var i = 0; i < checkedSelections.Length; i++)
        {
            var seat = (PlayerSeat)i;
            var selection = selections[seat]
                ?? throw new ArgumentException($"The pass selection for {seat} cannot be null.", nameof(selections));
            var outgoing = new[] { selection.ToRight, selection.ToPartner, selection.ToLeft };
            if (outgoing.Any(card => !card.IsValid) || outgoing.Distinct().Count() != 3 ||
                outgoing.Any(card => !handCopies[i].Contains(card)))
            {
                throw new ArgumentException($"{seat}'s pass must contain three distinct cards from that player's hand.", nameof(selections));
            }

            checkedSelections[i] = selection;
        }

        var resultHands = handCopies.Select(hand => hand.ToList()).ToArray();
        for (var i = 0; i < checkedSelections.Length; i++)
        {
            var selection = checkedSelections[i];
            resultHands[i].Remove(selection.ToRight);
            resultHands[i].Remove(selection.ToPartner);
            resultHands[i].Remove(selection.ToLeft);
        }

        foreach (var sender in Enum.GetValues<PlayerSeat>())
        {
            var selection = checkedSelections[sender.ToIndex()];
            resultHands[sender.ToTheRight().ToIndex()].Add(selection.ToRight);
            resultHands[sender.Partner().ToIndex()].Add(selection.ToPartner);
            resultHands[sender.ToTheLeft().ToIndex()].Add(selection.ToLeft);
        }

        var readOnlyHands = Enum.GetValues<PlayerSeat>().ToDictionary(
            seat => seat,
            seat => (IReadOnlyList<Card>)Array.AsReadOnly(resultHands[seat.ToIndex()].ToArray()));
        return new ReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>>(readOnlyHands);
    }

    private static void ValidateSeats<T>(IReadOnlyDictionary<PlayerSeat, T> values, string parameterName)
    {
        var seats = Enum.GetValues<PlayerSeat>();
        if (values.Count != seats.Length || seats.Any(seat => !values.ContainsKey(seat)))
        {
            throw new ArgumentException("A value must be provided for each of the four seats.", parameterName);
        }
    }
}
