namespace Tichu.Core;

/// <summary>A dealt and exchanged round, ready for card play.</summary>
public sealed class TichuPreparedRound
{
    internal TichuPreparedRound(
        IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> hands,
        PlayerSeat openingSeat)
    {
        Hands = hands;
        openingSeat.ToIndex();
        OpeningSeat = openingSeat;
    }

    /// <summary>The post-exchange hands, still private to each player in a game UI.</summary>
    public IReadOnlyDictionary<PlayerSeat, IReadOnlyList<Card>> Hands { get; }

    /// <summary>The post-exchange holder of Mah Jong, who leads the round.</summary>
    public PlayerSeat OpeningSeat { get; }

    /// <summary>Creates the round action state from these validated hands.</summary>
    public RoundState CreateRoundState()
    {
        var hands = Hands.ToDictionary(
            pair => pair.Key,
            pair => (IEnumerable<Card>)pair.Value);
        return new RoundState(OpeningSeat, hands);
    }
}

/// <summary>Connects a complete deal to the simultaneous pass and round start.</summary>
public static class TichuRoundSetup
{
    /// <summary>Applies the four card selections and chooses the post-pass Mah Jong holder to lead.</summary>
    public static TichuPreparedRound Prepare(
        TichuDeal deal,
        IReadOnlyDictionary<PlayerSeat, CardPassSelection> selections)
    {
        ArgumentNullException.ThrowIfNull(deal);
        ArgumentNullException.ThrowIfNull(selections);

        var prePassHands = deal.FourteenCardHands.ToDictionary(
            pair => pair.Key,
            pair => (IEnumerable<Card>)pair.Value);
        var postPassHands = CardExchange.Apply(prePassHands, selections);
        var mahJongHolders = Enum.GetValues<PlayerSeat>()
            .Where(seat => postPassHands[seat].Any(card => card.Rank == CardRank.MahJong))
            .ToArray();
        if (mahJongHolders.Length != 1)
        {
            throw new InvalidOperationException("A complete post-pass deal must have exactly one Mah Jong holder.");
        }

        return new TichuPreparedRound(postPassHands, mahJongHolders[0]);
    }
}
