using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class GrandDecisionPhaseTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan DecisionLimit = TimeSpan.FromSeconds(60);

    private static DateTimeOffset At(int seconds) => Start.AddSeconds(seconds);

    private static GrandDecisionPhase CreatePhase() => new(Start, DecisionLimit);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_RejectsNonPositiveDecisionLimit(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GrandDecisionPhase(Start, TimeSpan.FromTicks(ticks)));
    }

    [Fact]
    public void Seats_RotateClockwiseAndPartnerIsOpposite()
    {
        Assert.Equal(PlayerSeat.Right, PlayerSeat.Bottom.ToTheRight());
        Assert.Equal(PlayerSeat.Left, PlayerSeat.Bottom.ToTheLeft());
        Assert.Equal(PlayerSeat.Top, PlayerSeat.Bottom.Partner());

        foreach (var seat in Enum.GetValues<PlayerSeat>())
        {
            Assert.Equal(seat, seat.ToTheRight().ToTheLeft());
            Assert.Equal(seat, seat.Partner().Partner());
        }
    }

    [Fact]
    public void InitialSnapshot_ContainsFourUndecidedSeats()
    {
        var snapshot = CreatePhase().AdvanceTo(Start);

        Assert.Equal(Start, snapshot.StartedAt);
        Assert.Equal(Start + DecisionLimit, snapshot.DeadlineAt);
        Assert.False(snapshot.IsComplete);
        Assert.Equal(4, snapshot.Seats.Count);
        Assert.All(snapshot.Seats, decision =>
        {
            Assert.Equal(GrandDecisionStatus.Undecided, decision.Status);
            Assert.Null(decision.DecidedAt);
            Assert.Null(decision.Origin);
        });
    }

    [Fact]
    public void RemainingAt_ClampsBeforeStartAndAtDeadline()
    {
        var phase = CreatePhase();

        Assert.Equal(DecisionLimit, phase.AdvanceTo(Start).RemainingAt(Start.AddMinutes(-1)));
        Assert.Equal(DecisionLimit, phase.AdvanceTo(Start).RemainingAt(Start));
        Assert.Equal(TimeSpan.FromSeconds(30), phase.AdvanceTo(Start).RemainingAt(At(30)));
        Assert.Equal(TimeSpan.Zero, phase.AdvanceTo(Start).RemainingAt(Start + DecisionLimit));
    }

    [Fact]
    public void CallBeforePhaseStart_IsRejectedWithoutResolvingSeat()
    {
        var phase = CreatePhase();

        var result = phase.TryCallGrand(PlayerSeat.Bottom, Start.AddTicks(-1));

        Assert.Equal(GrandDecisionActionStatus.BeforePhaseStart, result.Status);
        Assert.Equal(GrandDecisionStatus.Undecided, result.Snapshot.For(PlayerSeat.Bottom).Status);
    }

    [Fact]
    public void CallBeforeDeadline_IsAcceptedAndRecorded()
    {
        var phase = CreatePhase();
        var calledAt = At(12);

        var result = phase.TryCallGrand(PlayerSeat.Right, calledAt);

        Assert.Equal(GrandDecisionActionStatus.Accepted, result.Status);
        Assert.True(result.Accepted);
        Assert.Equal(GrandDecisionStatus.Called, result.Snapshot.For(PlayerSeat.Right).Status);
        Assert.Equal(calledAt, result.Snapshot.For(PlayerSeat.Right).DecidedAt);
        Assert.Equal(GrandDecisionOrigin.Player, result.Snapshot.For(PlayerSeat.Right).Origin);
        Assert.False(result.Snapshot.For(PlayerSeat.Right).MayCallTichuAfterDeal);
    }

    [Fact]
    public void PartnerCallRequiresConfirmation_ButCanBeConfirmed()
    {
        var phase = CreatePhase();
        Assert.Equal(GrandDecisionActionStatus.Accepted, phase.TryCallGrand(PlayerSeat.Bottom, At(1)).Status);

        var warning = phase.TryCallGrand(PlayerSeat.Top, At(2));

        Assert.Equal(GrandDecisionActionStatus.PartnerConfirmationRequired, warning.Status);
        Assert.Equal(GrandDecisionStatus.Undecided, warning.Snapshot.For(PlayerSeat.Top).Status);

        var confirmed = phase.TryCallGrand(PlayerSeat.Top, At(3), confirmPartnerCall: true);

        Assert.Equal(GrandDecisionActionStatus.Accepted, confirmed.Status);
        Assert.Equal(GrandDecisionStatus.Called, confirmed.Snapshot.For(PlayerSeat.Bottom).Status);
        Assert.Equal(GrandDecisionStatus.Called, confirmed.Snapshot.For(PlayerSeat.Top).Status);
    }

    [Fact]
    public void DeclineGrand_PreservesLaterTichuOption()
    {
        var phase = CreatePhase();
        var declinedAt = At(10);

        var result = phase.TryDeclineGrand(PlayerSeat.Left, declinedAt);

        Assert.Equal(GrandDecisionActionStatus.Accepted, result.Status);
        Assert.Equal(GrandDecisionStatus.Declined, result.Snapshot.For(PlayerSeat.Left).Status);
        Assert.True(result.Snapshot.For(PlayerSeat.Left).MayCallTichuAfterDeal);
        Assert.Equal(declinedAt, result.Snapshot.For(PlayerSeat.Left).DecidedAt);
        Assert.Equal(GrandDecisionOrigin.Player, result.Snapshot.For(PlayerSeat.Left).Origin);
        Assert.False(result.Snapshot.For(PlayerSeat.Left).CanCallTichu);
    }

    [Fact]
    public void TichuCanBeCalledBeforeOrAfterOtherPlayersGrandCalls()
    {
        var phase = CreatePhase();

        Assert.True(phase.TryDeclineGrand(PlayerSeat.Bottom, At(1)).Accepted);
        var bottomDealt = phase.MarkFourteenthCardDealt(PlayerSeat.Bottom, At(2));
        Assert.True(bottomDealt.Snapshot.For(PlayerSeat.Bottom).CanCallTichu);
        Assert.True(phase.TryCallTichu(PlayerSeat.Bottom, At(3)).Accepted);
        Assert.Equal(TichuDecisionStatus.Called, phase.AdvanceTo(At(3)).For(PlayerSeat.Bottom).TichuStatus);

        Assert.True(phase.TryCallGrand(PlayerSeat.Right, At(4)).Accepted);
        Assert.True(phase.TryDeclineGrand(PlayerSeat.Top, At(5)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Top, At(6)).Accepted);
        Assert.True(phase.TryCallGrand(PlayerSeat.Left, At(7), confirmPartnerCall: true).Accepted);

        var topTichu = phase.TryCallTichu(PlayerSeat.Top, At(8));

        Assert.True(topTichu.Accepted);
        Assert.Equal(TichuDecisionStatus.Called, topTichu.Snapshot.For(PlayerSeat.Top).TichuStatus);
        Assert.Equal(GrandDecisionStatus.Called, topTichu.Snapshot.For(PlayerSeat.Right).Status);
        Assert.Equal(GrandDecisionStatus.Called, topTichu.Snapshot.For(PlayerSeat.Left).Status);
        Assert.True(topTichu.Snapshot.IsComplete);
    }

    [Fact]
    public void TichuIsUnavailableBeforeFourteenthCardAndClosesOnFirstPlayedCard()
    {
        var phase = CreatePhase();

        Assert.Equal(GrandDecisionActionStatus.TichuUnavailable,
            phase.TryCallTichu(PlayerSeat.Bottom, At(1)).Status);
        Assert.True(phase.TryDeclineGrand(PlayerSeat.Bottom, At(2)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Bottom, At(3)).Accepted);
        Assert.True(phase.AdvanceTo(At(3)).For(PlayerSeat.Bottom).CanCallTichu);

        var firstPlay = phase.MarkFirstCardPlayed(PlayerSeat.Bottom, At(4));

        Assert.True(firstPlay.Accepted);
        Assert.Equal(TichuDecisionStatus.Closed, firstPlay.Snapshot.For(PlayerSeat.Bottom).TichuStatus);
        Assert.False(firstPlay.Snapshot.For(PlayerSeat.Bottom).CanCallTichu);
        Assert.Equal(GrandDecisionActionStatus.AlreadyResolved,
            phase.TryCallTichu(PlayerSeat.Bottom, At(5)).Status);
    }

    [Fact]
    public void TichuEventsCannotBeBackdatedBeforeFourteenthCard()
    {
        var phase = CreatePhase();

        Assert.True(phase.TryDeclineGrand(PlayerSeat.Bottom, At(2)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Bottom, At(4)).Accepted);

        Assert.Equal(GrandDecisionActionStatus.EventOutOfOrder,
            phase.TryCallTichu(PlayerSeat.Bottom, At(3)).Status);
        Assert.Equal(GrandDecisionActionStatus.EventOutOfOrder,
            phase.MarkFirstCardPlayed(PlayerSeat.Bottom, At(3)).Status);
        Assert.True(phase.AdvanceTo(At(4)).For(PlayerSeat.Bottom).CanCallTichu);

        Assert.True(phase.TryCallTichu(PlayerSeat.Bottom, At(5)).Accepted);
        Assert.Equal(GrandDecisionActionStatus.EventOutOfOrder,
            phase.MarkFirstCardPlayed(PlayerSeat.Bottom, At(4)).Status);
    }

    [Fact]
    public void FourteenthCardCannotBeRecordedBeforeTheGrandDecision()
    {
        var phase = CreatePhase();

        Assert.True(phase.TryDeclineGrand(PlayerSeat.Bottom, At(4)).Accepted);

        Assert.Equal(GrandDecisionActionStatus.EventOutOfOrder,
            phase.MarkFourteenthCardDealt(PlayerSeat.Bottom, At(3)).Status);
        Assert.False(phase.AdvanceTo(At(4)).For(PlayerSeat.Bottom).CanCallTichu);
    }

    [Fact]
    public void SharedDeadlineClosesTichuOptionsStillOpenAfterFullDeal()
    {
        var phase = CreatePhase();
        Assert.True(phase.TryDeclineGrand(PlayerSeat.Left, At(1)).Accepted);
        Assert.True(phase.MarkFourteenthCardDealt(PlayerSeat.Left, At(2)).Accepted);

        var expired = phase.AdvanceTo(Start + DecisionLimit);

        Assert.True(expired.IsComplete);
        Assert.Equal(TichuDecisionStatus.Closed, expired.For(PlayerSeat.Left).TichuStatus);
        Assert.Equal(Start + DecisionLimit, expired.For(PlayerSeat.Left).TichuDecidedAt);
        Assert.Equal(GrandDecisionActionStatus.DeadlinePassed,
            phase.TryCallTichu(PlayerSeat.Left, Start + DecisionLimit).Status);
    }

    [Fact]
    public void NinthCard_ClosesOnlyThatSeatAndPreservesLaterTichuOption()
    {
        var phase = CreatePhase();

        var result = phase.CloseGrandOptionAfterNinthCard(PlayerSeat.Right, At(8));

        Assert.Equal(GrandDecisionActionStatus.Accepted, result.Status);
        Assert.Equal(GrandDecisionStatus.Declined, result.Snapshot.For(PlayerSeat.Right).Status);
        Assert.Equal(GrandDecisionOrigin.NinthCardReceived, result.Snapshot.For(PlayerSeat.Right).Origin);
        Assert.True(result.Snapshot.For(PlayerSeat.Right).MayCallTichuAfterDeal);
        Assert.False(result.Snapshot.IsComplete);
        Assert.Equal(GrandDecisionStatus.Undecided, result.Snapshot.For(PlayerSeat.Bottom).Status);

        Assert.Equal(GrandDecisionActionStatus.Accepted, phase.TryCallGrand(PlayerSeat.Bottom, At(9)).Status);
        Assert.Equal(
            GrandDecisionActionStatus.AlreadyResolved,
            phase.TryCallGrand(PlayerSeat.Right, At(10)).Status);
    }

    [Fact]
    public void ExactDeadlineCall_ExpiresAsDecline()
    {
        var phase = CreatePhase();
        var deadline = Start + DecisionLimit;

        var result = phase.TryCallGrand(PlayerSeat.Bottom, deadline);

        Assert.Equal(GrandDecisionActionStatus.DeadlinePassed, result.Status);
        Assert.Equal(GrandDecisionStatus.Declined, result.Snapshot.For(PlayerSeat.Bottom).Status);
        Assert.Equal(deadline, result.Snapshot.For(PlayerSeat.Bottom).DecidedAt);
        Assert.Equal(GrandDecisionOrigin.Timeout, result.Snapshot.For(PlayerSeat.Bottom).Origin);
    }

    [Fact]
    public void Deadline_DeclinesOnlySeatsStillUndecided()
    {
        var phase = CreatePhase();
        phase.TryCallGrand(PlayerSeat.Bottom, At(1));
        phase.TryDeclineGrand(PlayerSeat.Right, At(2));

        var snapshot = phase.AdvanceTo(Start + DecisionLimit);

        Assert.True(snapshot.IsComplete);
        Assert.Equal(GrandDecisionStatus.Called, snapshot.For(PlayerSeat.Bottom).Status);
        Assert.Equal(GrandDecisionOrigin.Player, snapshot.For(PlayerSeat.Bottom).Origin);
        Assert.Equal(GrandDecisionStatus.Declined, snapshot.For(PlayerSeat.Right).Status);
        Assert.Equal(GrandDecisionOrigin.Player, snapshot.For(PlayerSeat.Right).Origin);

        foreach (var seat in new[] { PlayerSeat.Top, PlayerSeat.Left })
        {
            Assert.Equal(GrandDecisionStatus.Declined, snapshot.For(seat).Status);
            Assert.Equal(Start + DecisionLimit, snapshot.For(seat).DecidedAt);
            Assert.Equal(GrandDecisionOrigin.Timeout, snapshot.For(seat).Origin);
            Assert.True(snapshot.For(seat).MayCallTichuAfterDeal);
        }
    }

    [Fact]
    public void AdvanceToBeforePhaseStart_Throws()
    {
        var phase = CreatePhase();

        Assert.Throws<ArgumentOutOfRangeException>(() => phase.AdvanceTo(Start.AddTicks(-1)));
    }

    [Fact]
    public void InvalidSeatValue_IsRejected()
    {
        var phase = CreatePhase();

        Assert.Throws<ArgumentOutOfRangeException>(() => phase.TryCallGrand((PlayerSeat)4, At(1)));
    }

    [Fact]
    public void Snapshot_IsIndependentAndReadOnly()
    {
        var phase = CreatePhase();
        var oldSnapshot = phase.AdvanceTo(Start);
        phase.TryDeclineGrand(PlayerSeat.Bottom, At(1));

        Assert.Equal(GrandDecisionStatus.Undecided, oldSnapshot.For(PlayerSeat.Bottom).Status);
        var seatList = Assert.IsAssignableFrom<IList<GrandSeatDecision>>(oldSnapshot.Seats);
        Assert.Throws<NotSupportedException>(() => seatList[0] = new(
            PlayerSeat.Bottom,
            GrandDecisionStatus.Called,
            At(2),
            GrandDecisionOrigin.Player));
    }
}
