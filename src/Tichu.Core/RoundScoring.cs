namespace Tichu.Core;

public enum TichuCall : byte
{
    None,
    Small,
    Grand
}

public sealed record RoundScoringResult(
    int BottomTopCardPoints,
    int RightLeftCardPoints,
    int BottomTopTichuPoints,
    int RightLeftTichuPoints,
    bool IsDoubleVictory)
{
    public int BottomTopTotal => BottomTopCardPoints + BottomTopTichuPoints;

    public int RightLeftTotal => RightLeftCardPoints + RightLeftTichuPoints;
}

/// <summary>Settles a completed round, including transfers and called bonuses.</summary>
public static class RoundScoring
{
    public static RoundScoringResult Calculate(
        IReadOnlyList<int> collectedPointsBySeat,
        IEnumerable<Card> tailenderHand,
        IReadOnlyList<PlayerSeat> finishOrder,
        PlayerSeat? tailenderSeat,
        bool isDoubleVictory,
        IReadOnlyDictionary<PlayerSeat, TichuCall>? calls = null)
    {
        ArgumentNullException.ThrowIfNull(collectedPointsBySeat);
        ArgumentNullException.ThrowIfNull(tailenderHand);
        ArgumentNullException.ThrowIfNull(finishOrder);

        if (collectedPointsBySeat.Count != 4)
        {
            throw new ArgumentException("Collected points must be supplied in seat order for all four players.", nameof(collectedPointsBySeat));
        }

        var finishers = finishOrder.ToArray();
        if (finishers.Distinct().Count() != finishers.Length || finishers.Any(seat => (uint)(int)seat >= 4))
        {
            throw new ArgumentException("Finish order must contain distinct valid seats.", nameof(finishOrder));
        }

        if (finishers.Length == 0)
        {
            throw new ArgumentException("A completed round must have at least one player who finished.", nameof(finishOrder));
        }

        var remainingCards = tailenderHand.ToArray();
        if (remainingCards.Any(card => !card.IsValid) || remainingCards.Distinct().Count() != remainingCards.Length)
        {
            throw new ArgumentException("The tailender hand must contain valid, unique physical cards.", nameof(tailenderHand));
        }

        int bottomTopCardPoints;
        int rightLeftCardPoints;
        if (isDoubleVictory)
        {
            if (finishers.Length != 2 || finishers[0].Partner() != finishers[1] || tailenderSeat is not null)
            {
                throw new ArgumentException("Double victory requires partners to finish first and second, with no tailender.", nameof(finishOrder));
            }

            bottomTopCardPoints = IsBottomTop(finishers[0]) ? 200 : 0;
            rightLeftCardPoints = IsBottomTop(finishers[0]) ? 0 : 200;
        }
        else
        {
            if (finishers.Length != 3 || tailenderSeat is null || finishers.Contains(tailenderSeat.Value))
            {
                throw new ArgumentException("A normal completed round requires three finishers and one distinct tailender.", nameof(finishOrder));
            }

            var tailenderIndex = tailenderSeat.Value.ToIndex();
            var firstOutIndex = finishers[0].ToIndex();
            var adjustedPoints = collectedPointsBySeat.ToArray();
            adjustedPoints[firstOutIndex] += adjustedPoints[tailenderIndex];
            adjustedPoints[tailenderIndex] = 0;

            bottomTopCardPoints = adjustedPoints[(int)PlayerSeat.Bottom] + adjustedPoints[(int)PlayerSeat.Top];
            rightLeftCardPoints = adjustedPoints[(int)PlayerSeat.Right] + adjustedPoints[(int)PlayerSeat.Left];

            var tailenderHandPoints = CardScoring.Total(remainingCards);
            if (IsBottomTop(tailenderSeat.Value))
            {
                rightLeftCardPoints += tailenderHandPoints;
            }
            else
            {
                bottomTopCardPoints += tailenderHandPoints;
            }
        }

        var (bottomTopTichuPoints, rightLeftTichuPoints) = ScoreCalls(calls, finishers[0]);
        return new RoundScoringResult(
            bottomTopCardPoints,
            rightLeftCardPoints,
            bottomTopTichuPoints,
            rightLeftTichuPoints,
            isDoubleVictory);
    }

    private static (int BottomTop, int RightLeft) ScoreCalls(
        IReadOnlyDictionary<PlayerSeat, TichuCall>? calls,
        PlayerSeat firstOut)
    {
        var bottomTop = 0;
        var rightLeft = 0;
        if (calls is null)
        {
            return (bottomTop, rightLeft);
        }

        foreach (var (seat, call) in calls)
        {
            seat.ToIndex();
            if (!Enum.IsDefined(call))
            {
                throw new ArgumentOutOfRangeException(nameof(calls), $"Invalid Tichu call for seat {seat}.");
            }

            var value = call switch
            {
                TichuCall.Small => 100,
                TichuCall.Grand => 200,
                _ => 0
            };
            if (seat != firstOut)
            {
                value = -value;
            }

            if (IsBottomTop(seat))
            {
                bottomTop += value;
            }
            else
            {
                rightLeft += value;
            }
        }

        return (bottomTop, rightLeft);
    }

    private static bool IsBottomTop(PlayerSeat seat) => seat is PlayerSeat.Bottom or PlayerSeat.Top;
}
