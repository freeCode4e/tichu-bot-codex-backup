namespace Tichu.Core;

/// <summary>
/// Seats are numbered clockwise from the bottom seat: bottom, right, top, left.
/// The opposite seat is always a player's partner.
/// </summary>
public enum PlayerSeat : byte
{
    Bottom = 0,
    Right = 1,
    Top = 2,
    Left = 3
}

public static class PlayerSeatExtensions
{
    public static PlayerSeat Partner(this PlayerSeat seat) => Rotate(seat, 2);

    public static PlayerSeat ToTheRight(this PlayerSeat seat) => Rotate(seat, 1);

    public static PlayerSeat ToTheLeft(this PlayerSeat seat) => Rotate(seat, 3);

    public static int ToIndex(this PlayerSeat seat)
    {
        var index = (int)seat;
        if ((uint)index >= 4)
        {
            throw new ArgumentOutOfRangeException(nameof(seat));
        }

        return index;
    }

    private static PlayerSeat Rotate(PlayerSeat seat, int offset) =>
        (PlayerSeat)((seat.ToIndex() + offset) % 4);
}

public enum GrandDecisionStatus : byte
{
    Undecided,
    Called,
    Declined
}

public enum GrandDecisionOrigin : byte
{
    Player,
    NinthCardReceived,
    FourteenthCardReceived,
    FirstCardPlayed,
    Timeout
}

public enum TichuDecisionStatus : byte
{
    Unavailable,
    Available,
    Called,
    Closed
}

public enum GrandDecisionActionStatus : byte
{
    Accepted,
    PartnerConfirmationRequired,
    AlreadyResolved,
    EventOutOfOrder,
    DeadlinePassed,
    BeforePhaseStart,
    TichuUnavailable
}

/// <summary>A public decision record. It contains no private hand information.</summary>
public sealed record GrandSeatDecision(
    PlayerSeat Seat,
    GrandDecisionStatus Status,
    DateTimeOffset? DecidedAt,
    GrandDecisionOrigin? Origin,
    TichuDecisionStatus TichuStatus = TichuDecisionStatus.Unavailable,
    DateTimeOffset? TichuDecidedAt = null,
    DateTimeOffset? TichuAvailableAt = null)
{
    /// <summary>
    /// True after declining Grand. This records potential eligibility only;
    /// <see cref="CanCallTichu"/> says whether the Tichu window is open now.
    /// </summary>
    public bool MayCallTichuAfterDeal => Status == GrandDecisionStatus.Declined;

    /// <summary>Whether this seat has received 14 cards and may call Tichu now.</summary>
    public bool CanCallTichu => TichuStatus == TichuDecisionStatus.Available;
}

/// <summary>Public state for the shared Grand and Tichu declaration window.</summary>
/// <param name="IsComplete">Whether every seat's declaration options are resolved.</param>
public sealed record GrandPhaseSnapshot(
    DateTimeOffset StartedAt,
    DateTimeOffset DeadlineAt,
    IReadOnlyList<GrandSeatDecision> Seats,
    bool IsComplete)
{
    public GrandSeatDecision For(PlayerSeat seat) => Seats[seat.ToIndex()];

    public TimeSpan RemainingAt(DateTimeOffset now)
    {
        if (now >= DeadlineAt)
        {
            return TimeSpan.Zero;
        }

        var effectiveNow = now < StartedAt ? StartedAt : now;
        return DeadlineAt - effectiveNow;
    }
}

public sealed record GrandDecisionActionResult(
    GrandDecisionActionStatus Status,
    GrandPhaseSnapshot Snapshot)
{
    public bool Accepted => Status == GrandDecisionActionStatus.Accepted;
}

/// <summary>
/// Tracks Grand and Tichu declarations in one shared, configurable decision
/// window. Grand is available with eight cards; Tichu opens after the fourteenth
/// card for players who declined Grand and closes on their first played card.
/// Calls and deal/play events must be supplied in chronological order. Game/UI
/// code owns dealing, card passing, and the choice of whether a bot calls.
/// </summary>
public sealed class GrandDecisionPhase
{
    private readonly object _gate = new();
    private readonly GrandSeatDecision[] _seats;

    public GrandDecisionPhase(DateTimeOffset startedAt, TimeSpan decisionLimit)
    {
        if (decisionLimit <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionLimit), "Decision time must be positive.");
        }

        StartedAt = startedAt;
        DeadlineAt = startedAt + decisionLimit;
        DecisionLimit = decisionLimit;
        _seats = Enum.GetValues<PlayerSeat>()
            .Select(seat => new GrandSeatDecision(
                seat,
                GrandDecisionStatus.Undecided,
                DecidedAt: null,
                Origin: null))
            .ToArray();
    }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset DeadlineAt { get; }

    public TimeSpan DecisionLimit { get; }

    /// <summary>
    /// Commits a Grand call. If the caller's partner already called, the UI can
    /// first ask for confirmation; bots may explicitly confirm through this API.
    /// The confirmation is a warning only and does not make a second call illegal.
    /// </summary>
    public GrandDecisionActionResult TryCallGrand(
        PlayerSeat seat,
        DateTimeOffset at,
        bool confirmPartnerCall = false)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            var current = _seats[index];
            if (current.Status != GrandDecisionStatus.Undecided)
            {
                return Result(current.Origin == GrandDecisionOrigin.Timeout
                    ? GrandDecisionActionStatus.DeadlinePassed
                    : GrandDecisionActionStatus.AlreadyResolved);
            }

            if (at >= DeadlineAt)
            {
                return Result(GrandDecisionActionStatus.DeadlinePassed);
            }

            var partner = seat.Partner();
            if (_seats[partner.ToIndex()].Status == GrandDecisionStatus.Called && !confirmPartnerCall)
            {
                return Result(GrandDecisionActionStatus.PartnerConfirmationRequired);
            }

            _seats[index] = new GrandSeatDecision(
                seat,
                GrandDecisionStatus.Called,
                at,
                GrandDecisionOrigin.Player,
                TichuDecisionStatus.Closed,
                at);

            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>Closes this seat's Grand option while preserving its later Tichu option.</summary>
    public GrandDecisionActionResult TryDeclineGrand(PlayerSeat seat, DateTimeOffset at)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            var current = _seats[index];
            if (current.Status != GrandDecisionStatus.Undecided)
            {
                return Result(current.Origin == GrandDecisionOrigin.Timeout
                    ? GrandDecisionActionStatus.DeadlinePassed
                    : GrandDecisionActionStatus.AlreadyResolved);
            }

            if (at >= DeadlineAt)
            {
                return Result(GrandDecisionActionStatus.DeadlinePassed);
            }

            _seats[index] = new GrandSeatDecision(
                seat,
                GrandDecisionStatus.Declined,
                at,
                GrandDecisionOrigin.Player);

            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>
    /// Closes this seat's Grand option when its ninth card is dealt. This lets
    /// that player continue while other seats are still deciding.
    /// </summary>
    public GrandDecisionActionResult CloseGrandOptionAfterNinthCard(PlayerSeat seat, DateTimeOffset at)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            var current = _seats[index];
            if (current.Status != GrandDecisionStatus.Undecided)
            {
                return Result(current.Origin == GrandDecisionOrigin.Timeout
                    ? GrandDecisionActionStatus.DeadlinePassed
                    : GrandDecisionActionStatus.AlreadyResolved);
            }

            if (at >= DeadlineAt)
            {
                return Result(GrandDecisionActionStatus.DeadlinePassed);
            }

            _seats[index] = new GrandSeatDecision(
                seat,
                GrandDecisionStatus.Declined,
                at,
                GrandDecisionOrigin.NinthCardReceived);

            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>
    /// Records receipt of the fourteenth card and opens this player's Tichu
    /// option if they did not call Grand. Reaching fourteen cards also closes
    /// an undecided Grand option defensively.
    /// </summary>
    public GrandDecisionActionResult MarkFourteenthCardDealt(PlayerSeat seat, DateTimeOffset at)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            if (at >= DeadlineAt)
            {
                return Result(GrandDecisionActionStatus.DeadlinePassed);
            }

            var current = _seats[index];
            if (current.Status == GrandDecisionStatus.Called)
            {
                return Result(GrandDecisionActionStatus.AlreadyResolved);
            }

            if ((current.DecidedAt is { } grandDecidedAt && at < grandDecidedAt) ||
                (current.TichuDecidedAt is { } tichuDecidedAt && at < tichuDecidedAt))
            {
                return Result(GrandDecisionActionStatus.EventOutOfOrder);
            }

            if (current.TichuStatus != TichuDecisionStatus.Unavailable)
            {
                return Result(GrandDecisionActionStatus.AlreadyResolved);
            }

            if (current.Status == GrandDecisionStatus.Undecided)
            {
                current = current with
                {
                    Status = GrandDecisionStatus.Declined,
                    DecidedAt = at,
                    Origin = GrandDecisionOrigin.FourteenthCardReceived
                };
            }

            _seats[index] = current with
            {
                TichuStatus = TichuDecisionStatus.Available,
                TichuAvailableAt = at
            };
            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>
    /// Calls Small Tichu after all fourteen cards are held. The option is
    /// independent of other players' Grand calls and remains open until the
    /// caller plays their first card or the shared declaration deadline passes.
    /// Passing does not close this option.
    /// </summary>
    public GrandDecisionActionResult TryCallTichu(PlayerSeat seat, DateTimeOffset at)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            var current = _seats[index];
            if (current.TichuStatus == TichuDecisionStatus.Available &&
                current.TichuAvailableAt is { } availableAt && at < availableAt)
            {
                return Result(GrandDecisionActionStatus.EventOutOfOrder);
            }

            if ((current.TichuStatus is TichuDecisionStatus.Called or TichuDecisionStatus.Closed) &&
                current.TichuDecidedAt is { } resolvedAt && at < resolvedAt)
            {
                return Result(GrandDecisionActionStatus.EventOutOfOrder);
            }

            if (current.TichuStatus != TichuDecisionStatus.Available)
            {
                return Result(at >= DeadlineAt
                    ? GrandDecisionActionStatus.DeadlinePassed
                    : current.TichuStatus == TichuDecisionStatus.Unavailable
                        ? GrandDecisionActionStatus.TichuUnavailable
                        : GrandDecisionActionStatus.AlreadyResolved);
            }

            _seats[index] = current with
            {
                TichuStatus = TichuDecisionStatus.Called,
                TichuDecidedAt = at
            };
            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>Closes the Tichu option when this seat plays its first card.</summary>
    public GrandDecisionActionResult MarkFirstCardPlayed(PlayerSeat seat, DateTimeOffset at)
    {
        var index = seat.ToIndex();

        lock (_gate)
        {
            if (at < StartedAt)
            {
                return Result(GrandDecisionActionStatus.BeforePhaseStart);
            }

            ApplyTimeouts(at);
            var current = _seats[index];
            if ((current.DecidedAt is { } grandDecidedAt && at < grandDecidedAt) ||
                (current.TichuAvailableAt is { } availableAt && at < availableAt) ||
                (current.TichuDecidedAt is { } tichuDecidedAt && at < tichuDecidedAt))
            {
                return Result(GrandDecisionActionStatus.EventOutOfOrder);
            }

            if (current.TichuStatus is TichuDecisionStatus.Called or TichuDecisionStatus.Closed)
            {
                return Result(GrandDecisionActionStatus.AlreadyResolved);
            }

            if (current.Status == GrandDecisionStatus.Undecided)
            {
                current = current with
                {
                    Status = GrandDecisionStatus.Declined,
                    DecidedAt = at,
                    Origin = GrandDecisionOrigin.FirstCardPlayed
                };
            }

            _seats[index] = current with
            {
                TichuStatus = TichuDecisionStatus.Closed,
                TichuDecidedAt = at
            };
            return Result(GrandDecisionActionStatus.Accepted);
        }
    }

    /// <summary>
    /// Advances the phase clock. The proposed timeout behavior is to decline
    /// Grand for every seat still undecided at the shared deadline.
    /// </summary>
    public GrandPhaseSnapshot AdvanceTo(DateTimeOffset now)
    {
        if (now < StartedAt)
        {
            throw new ArgumentOutOfRangeException(nameof(now), "Time cannot precede the phase start.");
        }

        lock (_gate)
        {
            ApplyTimeouts(now);
            return CreateSnapshot();
        }
    }

    private void ApplyTimeouts(DateTimeOffset now)
    {
        if (now < DeadlineAt)
        {
            return;
        }

        for (var i = 0; i < _seats.Length; i++)
        {
            if (_seats[i].Status != GrandDecisionStatus.Undecided)
            {
                if ((_seats[i].TichuStatus is TichuDecisionStatus.Unavailable or TichuDecisionStatus.Available) &&
                    _seats[i].Status != GrandDecisionStatus.Called)
                {
                    _seats[i] = _seats[i] with
                    {
                        TichuStatus = TichuDecisionStatus.Closed,
                        TichuDecidedAt = DeadlineAt
                    };
                }

                continue;
            }

            _seats[i] = _seats[i] with
            {
                Status = GrandDecisionStatus.Declined,
                DecidedAt = DeadlineAt,
                Origin = GrandDecisionOrigin.Timeout,
                TichuStatus = TichuDecisionStatus.Closed,
                TichuDecidedAt = DeadlineAt
            };
        }
    }

    private GrandDecisionActionResult Result(GrandDecisionActionStatus status) =>
        new(status, CreateSnapshot());

    private GrandPhaseSnapshot CreateSnapshot()
    {
        var copy = (GrandSeatDecision[])_seats.Clone();
        return new GrandPhaseSnapshot(
            StartedAt,
            DeadlineAt,
            Array.AsReadOnly(copy),
            copy.All(decision => decision.Status != GrandDecisionStatus.Undecided &&
                                 (decision.Status == GrandDecisionStatus.Called ||
                                  decision.TichuStatus is TichuDecisionStatus.Called or TichuDecisionStatus.Closed)));
    }
}
