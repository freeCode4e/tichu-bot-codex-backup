using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Public-to-the-player context from the completed card exchange.</summary>
public sealed record PolicyRoundContext(PlayerSeat Seat, CardPassSelection PassSelection);

/// <summary>Public state visible when a Grand or Tichu call is considered.</summary>
public sealed record PolicyDeclarationContext(
    PlayerSeat Seat,
    GrandPhaseSnapshot Snapshot,
    RoundSnapshot? PlaySnapshot = null);

/// <summary>Decisions required from one seat's policy during a round.</summary>
public interface ITichuPolicy
{
    void BeginRound(PolicyRoundContext context);

    bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context);

    bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context);

    CardPassSelection ChoosePass(IReadOnlyList<Card> hand);

    CardPassSelection ChoosePass(PlayerSeat seat, IReadOnlyList<Card> hand)
    {
        seat.ToIndex();
        return ChoosePass(hand);
    }

    LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot);

    LegalMove? ChooseOutOfTurnBomb(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        IReadOnlyList<LegalMove> bombs);

    PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner);
}
