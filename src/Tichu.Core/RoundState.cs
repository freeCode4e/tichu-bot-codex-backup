namespace Tichu.Core;

public sealed record RoundSnapshot(
    TrickSnapshot Trick,
    IReadOnlyList<int> HandSizes,
    IReadOnlyList<int> CollectedPointsBySeat)
{
    public bool IsRoundEnded => Trick.IsRoundEnded;

    public int TotalCollectedPoints => CollectedPointsBySeat.Sum();
}

/// <summary>
/// Owns the players' hands for one round and applies accepted trick actions to
/// those hands. The supplied hands represent the post-deal, post-pass hands.
/// Final transfer of the tailender's hand and team scoring are handled later.
/// </summary>
public sealed class RoundState
{
    private readonly Card[][] _hands;
    private readonly int[] _collectedPoints = new int[4];
    private readonly TrickState _trick;

    public RoundState(PlayerSeat openingSeat, IReadOnlyDictionary<PlayerSeat, IEnumerable<Card>> hands)
    {
        ArgumentNullException.ThrowIfNull(hands);
        openingSeat.ToIndex();
        var seats = Enum.GetValues<PlayerSeat>();
        if (hands.Count != seats.Length || seats.Any(seat => !hands.ContainsKey(seat)))
        {
            throw new ArgumentException("A hand must be provided for each of the four seats.", nameof(hands));
        }

        _hands = new Card[seats.Length][];
        var allCards = new HashSet<Card>();
        foreach (var seat in seats)
        {
            var hand = hands[seat]?.ToArray()
                ?? throw new ArgumentException($"The hand for {seat} cannot be null.", nameof(hands));
            if (hand.Any(card => !card.IsValid) || hand.Distinct().Count() != hand.Length ||
                hand.Any(card => !allCards.Add(card)))
            {
                throw new ArgumentException("Hands must contain valid physical cards, with no duplicates.", nameof(hands));
            }

            _hands[seat.ToIndex()] = hand;
        }

        _trick = new TrickState(openingSeat);
    }

    public RoundSnapshot Snapshot => CreateSnapshot();

    /// <summary>Returns a copy of the specified seat's current private hand.</summary>
    public IReadOnlyList<Card> HandFor(PlayerSeat seat) => Array.AsReadOnly(_hands[seat.ToIndex()].ToArray());

    public TrickActionResult TryPlay(PlayerSeat seat, CardCombination play, CardRank? wishForMahJong = null)
    {
        var result = _trick.TryPlay(seat, GetHand(seat), play, wishForMahJong);
        if (result.Accepted)
        {
            RemovePlayedCards(seat, play);
        }

        return result;
    }

    public TrickActionResult TryBomb(PlayerSeat seat, CardCombination bomb)
    {
        var result = _trick.TryBomb(seat, GetHand(seat), bomb);
        if (result.Accepted)
        {
            RemovePlayedCards(seat, bomb);
        }

        return result;
    }

    public TrickActionResult TryPass(PlayerSeat seat) => _trick.TryPass(seat, GetHand(seat));

    public TrickActionResult TryCollect(PlayerSeat? dragonRecipient = null)
    {
        var result = _trick.TryCollect(dragonRecipient);
        if (result.Accepted && result.Resolution is not null)
        {
            _collectedPoints[result.Resolution.PointsRecipientSeat.ToIndex()] +=
                CardScoring.Total(result.Resolution.Cards);
        }

        return result;
    }

    public RoundScoringResult ScoreRound(IReadOnlyDictionary<PlayerSeat, TichuCall>? calls = null)
    {
        var trick = _trick.Snapshot;
        if (!trick.IsRoundEnded)
        {
            throw new InvalidOperationException("The round cannot be scored before it has ended.");
        }

        if (trick.IsDoubleVictory)
        {
            return RoundScoring.Calculate(
                _collectedPoints,
                Array.Empty<Card>(),
                trick.FinishOrder,
                tailenderSeat: null,
                isDoubleVictory: true,
                calls: calls);
        }

        if (trick.ActiveSeats.Count != 1 || trick.FinishOrder.Count != 3 || trick.Plays.Count != 0)
        {
            throw new InvalidOperationException("Collect the final trick before scoring a normally ended round.");
        }

        var tailender = trick.ActiveSeats[0];
        return RoundScoring.Calculate(
            _collectedPoints,
            _hands[tailender.ToIndex()],
            trick.FinishOrder,
            tailender,
            isDoubleVictory: false,
            calls: calls);
    }

    private Card[] GetHand(PlayerSeat seat) => _hands[seat.ToIndex()];

    private void RemovePlayedCards(PlayerSeat seat, CardCombination play)
    {
        var played = play.Cards.ToHashSet();
        _hands[seat.ToIndex()] = _hands[seat.ToIndex()].Where(card => !played.Contains(card)).ToArray();
    }

    private RoundSnapshot CreateSnapshot() => new(
        _trick.Snapshot,
        Array.AsReadOnly(_hands.Select(hand => hand.Length).ToArray()),
        Array.AsReadOnly(_collectedPoints.ToArray()));
}
