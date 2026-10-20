namespace Tichu.Core;

public enum CardSuit : byte
{
    Jade,
    Sword,
    Pagoda,
    Star
}

/// <summary>
/// Numbered cards use their Tichu rank (Mah Jong is 1 and Ace is 14).
/// Special cards have values outside that range and never have a suit.
/// </summary>
public enum CardRank : byte
{
    MahJong = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5,
    Six = 6,
    Seven = 7,
    Eight = 8,
    Nine = 9,
    Ten = 10,
    Jack = 11,
    Queen = 12,
    King = 13,
    Ace = 14,
    Phoenix = 15,
    Dragon = 16,
    Dog = 17
}

/// <summary>A physical card. Ordinary cards require a suit; special cards do not have one.</summary>
public readonly record struct Card
{
    public Card(CardRank rank, CardSuit? suit = null)
    {
        if (!Enum.IsDefined(rank))
        {
            throw new ArgumentOutOfRangeException(nameof(rank));
        }

        if (IsOrdinaryRank(rank))
        {
            if (suit is null || !Enum.IsDefined(suit.Value))
            {
                throw new ArgumentOutOfRangeException(nameof(suit), "Ordinary cards require a valid suit.");
            }
        }
        else if (suit is not null)
        {
            throw new ArgumentException("Special cards do not have a suit.", nameof(suit));
        }

        Rank = rank;
        Suit = suit;
    }

    public CardRank Rank { get; }

    public CardSuit? Suit { get; }

    public bool IsOrdinary => IsOrdinaryRank(Rank);

    /// <summary>Whether this value represents one of the 56 physical deck cards.</summary>
    public bool IsValid => IsOrdinaryRank(Rank)
        ? Suit is not null && Enum.IsDefined(Suit.Value)
        : Suit is null && Rank is CardRank.MahJong or CardRank.Phoenix or CardRank.Dragon or CardRank.Dog;

    internal static bool IsOrdinaryRank(CardRank rank) => rank is >= CardRank.Two and <= CardRank.Ace;
}

public static class TichuDeck
{
    public const int CardCount = 56;

    /// <summary>Creates the standard 52 suited cards and four unique special cards.</summary>
    public static IReadOnlyList<Card> CreateStandard()
    {
        var cards = new List<Card>(CardCount);
        foreach (var suit in Enum.GetValues<CardSuit>())
        {
            for (var rank = (byte)CardRank.Two; rank <= (byte)CardRank.Ace; rank++)
            {
                cards.Add(new Card((CardRank)rank, suit));
            }
        }

        cards.Add(new Card(CardRank.MahJong));
        cards.Add(new Card(CardRank.Phoenix));
        cards.Add(new Card(CardRank.Dragon));
        cards.Add(new Card(CardRank.Dog));
        return Array.AsReadOnly(cards.ToArray());
    }
}
