using Tichu.Core;

namespace Tichu.SelfPlay;

public enum SelfPlayWinner : byte
{
    Draw,
    BottomTop,
    RightLeft
}

public sealed record SelfPlayPassRecord(
    PlayerSeat Seat,
    Card ToRight,
    Card ToPartner,
    Card ToLeft);

public sealed record SelfPlayCallRecord(
    PlayerSeat Seat,
    TichuCall Call,
    int? BeforeActionIndex = null,
    IReadOnlyList<Card>? HandBeforeCall = null,
    CardRank? PendingWish = null);

public sealed record SelfPlayTrickPlayRecord(
    PlayerSeat Seat,
    CombinationType Type,
    decimal Strength,
    IReadOnlyList<Card> Cards);

public sealed record SelfPlayCollectionRecord(
    PlayerSeat WinnerSeat,
    PlayerSeat PointsRecipientSeat,
    IReadOnlyList<Card> Cards,
    IReadOnlyList<SelfPlayTrickPlayRecord> Plays,
    PlayerSeat? NextLeadSeat);

/// <summary>A private-hand/public-state observation paired with the chosen action.</summary>
public sealed record SelfPlayActionRecord(
    PlayerSeat Seat,
    IReadOnlyList<Card> HandBefore,
    IReadOnlyList<Card> CurrentTopCards,
    CombinationType? CurrentTopType,
    decimal? CurrentTopStrength,
    CardRank? PendingWish,
    IReadOnlyList<SelfPlayTrickPlayRecord> PlaysOnTable,
    IReadOnlyList<PlayerSeat> ActiveSeats,
    IReadOnlyList<PlayerSeat> PassedSeats,
    IReadOnlyList<int> HandSizes,
    IReadOnlyList<int> CollectedPointsBySeat,
    bool IsPass,
    bool IsOutOfTurnBomb,
    IReadOnlyList<Card> PlayedCards,
    CombinationType? ChosenType,
    CardRank? WishForMahJong);

public sealed record SelfPlayRoundRecord(
    int RoundIndex,
    int Seed,
    int BottomTopScoreBefore,
    int RightLeftScoreBefore,
    IReadOnlyList<Card> DeckOrder,
    IReadOnlyList<SelfPlayCallRecord> Calls,
    IReadOnlyList<SelfPlayPassRecord> Passes,
    IReadOnlyList<SelfPlayActionRecord> Actions,
    IReadOnlyList<SelfPlayCollectionRecord> Collections,
    IReadOnlyList<PlayerSeat> FinishOrder,
    RoundScoringResult Score);

/// <summary>Full match result and trajectory for replay, analysis, or later training.</summary>
public sealed record SelfPlayMatchRecord(
    int SchemaVersion,
    long GameId,
    int MatchSeed,
    int TargetScore,
    int BottomTopScore,
    int RightLeftScore,
    SelfPlayWinner Winner,
    IReadOnlyList<SelfPlayRoundRecord> Rounds);

/// <summary>Final score for a match when its replay trajectory is not needed.</summary>
public sealed record SelfPlayMatchSummary(
    long GameId,
    int MatchSeed,
    int TargetScore,
    int BottomTopScore,
    int RightLeftScore,
    SelfPlayWinner Winner);
