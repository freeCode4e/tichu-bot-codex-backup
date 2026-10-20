namespace Tichu.Core;

public static class CardScoring
{
    public static int Value(Card card)
    {
        if (!card.IsValid)
        {
            throw new ArgumentOutOfRangeException(nameof(card), "A valid physical card is required.");
        }

        return card.Rank switch
        {
            CardRank.Five => 5,
            CardRank.Ten or CardRank.King => 10,
            CardRank.Dragon => 25,
            CardRank.Phoenix => -25,
            _ => 0
        };
    }

    public static int Total(IEnumerable<Card> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return cards.Sum(Value);
    }
}
