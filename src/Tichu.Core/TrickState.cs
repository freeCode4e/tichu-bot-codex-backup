namespace Tichu.Core;

public enum TrickActionStatus : byte
{
    Accepted,
    NotPlayersTurn,
    PlayerInactive,
    MoveRejected,
    NewWishRequiresMahJong,
    InvalidWishRank,
    BombMustUseTurnPlay,
    NoTrickToBomb,
    NoTrickToCollect,
    TrickNotReadyToCollect,
    DragonRecipientRequired,
    InvalidDragonRecipient,
    UnexpectedDragonRecipient,
    RoundEnded
}

public sealed record TrickPlay(PlayerSeat Seat, CardCombination Combination);

public sealed record TrickSnapshot(
    PlayerSeat? CurrentTurnSeat,
    CardCombination? CurrentTop,
    PlayerSeat? CurrentTopSeat,
    CardRank? PendingWish,
    IReadOnlyList<TrickPlay> Plays,
    IReadOnlyList<PlayerSeat> ActiveSeats,
    IReadOnlyList<PlayerSeat> PassedSeats,
    IReadOnlyList<PlayerSeat> FinishOrder,
    bool CanCollect,
    bool IsDoubleVictory,
    bool IsRoundEnded);

public sealed record TrickResolution(
    PlayerSeat WinnerSeat,
    PlayerSeat PointsRecipientSeat,
    IReadOnlyList<Card> Cards,
    IReadOnlyList<TrickPlay> Plays,
    PlayerSeat? NextLeadSeat);

public sealed record TrickActionResult(
    TrickActionStatus Status,
    TrickSnapshot Snapshot,
    MoveValidationStatus? MoveStatus = null,
    TrickResolution? Resolution = null,
    CardCombination? ResolvedPlay = null)
{
    public bool Accepted => Status == TrickActionStatus.Accepted;
}

/// <summary>
/// Tracks one round's turn and trick progression. The round controller owns
/// persistent hands and scores; each action supplies the acting player's hand
/// so this state can validate it before advancing the trick.
/// </summary>
public sealed class TrickState
{
    private readonly HashSet<PlayerSeat> _activeSeats;
    private readonly HashSet<PlayerSeat> _passedSeats = new();
    private readonly List<TrickPlay> _plays = new();
    private readonly List<PlayerSeat> _finishOrder = new();
    private PlayerSeat? _currentTurnSeat;
    private CardCombination? _currentTop;
    private PlayerSeat? _currentTopSeat;
    private CardRank? _pendingWish;
    private bool _isDoubleVictory;
    private bool _isRoundEnded;

    public TrickState(PlayerSeat openingSeat, IEnumerable<PlayerSeat>? activeSeats = null)
    {
        openingSeat.ToIndex();
        var seats = (activeSeats ?? Enum.GetValues<PlayerSeat>()).ToArray();
        if (seats.Distinct().Count() != seats.Length || seats.Length < 2 || seats.Any(seat => (uint)(int)seat >= 4))
        {
            throw new ArgumentException("At least two distinct valid seats are required.", nameof(activeSeats));
        }

        _activeSeats = seats.ToHashSet();
        if (!_activeSeats.Contains(openingSeat))
        {
            throw new ArgumentException("The opening seat must be active.", nameof(openingSeat));
        }

        _currentTurnSeat = openingSeat;
    }

    public TrickSnapshot Snapshot => CreateSnapshot();

    public TrickActionResult TryPlay(
        PlayerSeat seat,
        IEnumerable<Card> hand,
        CardCombination play,
        CardRank? wishForMahJong = null)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(play);

        if (_isRoundEnded)
        {
            return Result(TrickActionStatus.RoundEnded);
        }

        if (!_activeSeats.Contains(seat))
        {
            return Result(TrickActionStatus.PlayerInactive);
        }

        if (_currentTurnSeat != seat)
        {
            return Result(TrickActionStatus.NotPlayersTurn);
        }

        if (wishForMahJong is not null && !Card.IsOrdinaryRank(wishForMahJong.Value))
        {
            return Result(TrickActionStatus.InvalidWishRank);
        }

        var hasMahJong = play.Cards.Any(card => card.Rank == CardRank.MahJong);
        if (wishForMahJong is not null && !hasMahJong)
        {
            return Result(TrickActionStatus.NewWishRequiresMahJong);
        }

        var handCards = hand.ToArray();
        var validation = MoveRules.ValidatePlay(handCards, play, _currentTop, _pendingWish);
        if (!validation.IsLegal)
        {
            return Result(TrickActionStatus.MoveRejected, validation.Status);
        }

        if (_pendingWish is not null && play.Cards.Any(card => card.Rank == _pendingWish.Value))
        {
            _pendingWish = null;
        }

        if (hasMahJong)
        {
            _pendingWish = wishForMahJong;
        }

        if (play.Type == CombinationType.Single && play.Cards[0].Rank == CardRank.Dog)
        {
            MarkFinishedIfEmpty(seat, handCards.Length, play.CardCount);
            if (!_isRoundEnded)
            {
                var partner = seat.Partner();
                _currentTurnSeat = _activeSeats.Contains(partner) ? partner : NextActiveAfter(partner);
            }

            return Result(TrickActionStatus.Accepted, resolvedPlay: validation.ResolvedPlay);
        }

        AddPlay(seat, validation.ResolvedPlay!, handCards.Length);
        SetNextTurnAfterPlay(seat);
        return Result(TrickActionStatus.Accepted, resolvedPlay: validation.ResolvedPlay);
    }

    /// <summary>Plays a bomb outside the acting player's ordinary turn.</summary>
    public TrickActionResult TryBomb(PlayerSeat seat, IEnumerable<Card> hand, CardCombination bomb)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(bomb);

        if (_isRoundEnded)
        {
            return Result(TrickActionStatus.RoundEnded);
        }

        if (!_activeSeats.Contains(seat))
        {
            return Result(TrickActionStatus.PlayerInactive);
        }

        if (_currentTurnSeat == seat)
        {
            return Result(TrickActionStatus.BombMustUseTurnPlay);
        }

        if (_currentTop is null)
        {
            return Result(TrickActionStatus.NoTrickToBomb);
        }

        var handCards = hand.ToArray();
        var validation = MoveRules.ValidatePlay(
            handCards,
            bomb,
            _currentTop,
            _pendingWish,
            isOutOfTurn: true);
        if (!validation.IsLegal)
        {
            return Result(TrickActionStatus.MoveRejected, validation.Status);
        }

        if (_pendingWish is not null && bomb.Cards.Any(card => card.Rank == _pendingWish.Value))
        {
            _pendingWish = null;
        }

        AddPlay(seat, validation.ResolvedPlay!, handCards.Length);
        SetNextTurnAfterPlay(seat);
        return Result(TrickActionStatus.Accepted, resolvedPlay: validation.ResolvedPlay);
    }

    public TrickActionResult TryPass(PlayerSeat seat, IEnumerable<Card> hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (_isRoundEnded)
        {
            return Result(TrickActionStatus.RoundEnded);
        }

        if (!_activeSeats.Contains(seat))
        {
            return Result(TrickActionStatus.PlayerInactive);
        }

        if (_currentTurnSeat != seat)
        {
            return Result(TrickActionStatus.NotPlayersTurn);
        }

        var validation = MoveRules.ValidatePass(hand, _currentTop, _pendingWish);
        if (!validation.IsLegal)
        {
            return Result(TrickActionStatus.MoveRejected, validation.Status);
        }

        _passedSeats.Add(seat);
        _currentTurnSeat = CanCollect ? null : NextActiveAfter(seat);
        return Result(TrickActionStatus.Accepted);
    }

    public TrickActionResult TryCollect(PlayerSeat? dragonRecipient = null)
    {
        if (_isDoubleVictory)
        {
            return Result(TrickActionStatus.RoundEnded);
        }

        if (_currentTop is null || _currentTopSeat is null)
        {
            return Result(TrickActionStatus.NoTrickToCollect);
        }

        if (!CanCollect)
        {
            return Result(TrickActionStatus.TrickNotReadyToCollect);
        }

        var dragonWon = _currentTop.Type == CombinationType.Single &&
                        _currentTop.Cards[0].Rank == CardRank.Dragon;
        if (dragonWon)
        {
            if (dragonRecipient is null)
            {
                return Result(TrickActionStatus.DragonRecipientRequired);
            }

            dragonRecipient.Value.ToIndex();
            if (dragonRecipient == _currentTopSeat || dragonRecipient == _currentTopSeat.Value.Partner())
            {
                return Result(TrickActionStatus.InvalidDragonRecipient);
            }
        }
        else if (dragonRecipient is not null)
        {
            return Result(TrickActionStatus.UnexpectedDragonRecipient);
        }

        var winner = _currentTopSeat.Value;
        var recipient = dragonWon ? dragonRecipient!.Value : winner;
        var trickPlays = Array.AsReadOnly(_plays.ToArray());
        var trickCards = Array.AsReadOnly(_plays.SelectMany(item => item.Combination.Cards).ToArray());
        var nextLead = _isRoundEnded ? null :
            (_activeSeats.Contains(winner) ? winner : NextActiveAfter(winner));
        var resolution = new TrickResolution(winner, recipient, trickCards, trickPlays, nextLead);

        _plays.Clear();
        _passedSeats.Clear();
        _currentTop = null;
        _currentTopSeat = null;
        _currentTurnSeat = nextLead;
        return Result(TrickActionStatus.Accepted, resolution: resolution);
    }

    private bool CanCollect
    {
        get
        {
            if (_currentTop is null || _currentTopSeat is null || _isDoubleVictory)
            {
                return false;
            }

            if (_activeSeats.Count <= 1)
            {
                return true;
            }

            return _activeSeats
                .Where(seat => seat != _currentTopSeat)
                .All(_passedSeats.Contains);
        }
    }

    private void AddPlay(PlayerSeat seat, CardCombination resolvedPlay, int startingHandCount)
    {
        _plays.Add(new TrickPlay(seat, resolvedPlay));
        _currentTop = resolvedPlay;
        _currentTopSeat = seat;
        _passedSeats.Clear();
        MarkFinishedIfEmpty(seat, startingHandCount, resolvedPlay.CardCount);
    }

    private void MarkFinishedIfEmpty(PlayerSeat seat, int handCount, int playedCount)
    {
        if (handCount != playedCount || !_activeSeats.Remove(seat))
        {
            return;
        }

        _finishOrder.Add(seat);
        if (_finishOrder.Count == 2 && _finishOrder[0].Partner() == _finishOrder[1])
        {
            _isDoubleVictory = true;
            _isRoundEnded = true;
        }

        if (_activeSeats.Count <= 1)
        {
            _isRoundEnded = true;
        }

        if (_isRoundEnded)
        {
            _currentTurnSeat = null;
        }
    }

    private void SetNextTurnAfterPlay(PlayerSeat seat)
    {
        if (_isRoundEnded)
        {
            _currentTurnSeat = null;
            return;
        }

        _currentTurnSeat = CanCollect ? null : NextActiveAfter(seat);
    }

    private PlayerSeat? NextActiveAfter(PlayerSeat seat)
    {
        for (var offset = 1; offset <= 4; offset++)
        {
            var candidate = (PlayerSeat)(((int)seat + offset) % 4);
            if (_activeSeats.Contains(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private TrickActionResult Result(
        TrickActionStatus status,
        MoveValidationStatus? moveStatus = null,
        CardCombination? resolvedPlay = null,
        TrickResolution? resolution = null)
    {
        return new TrickActionResult(status, CreateSnapshot(), moveStatus, resolution, resolvedPlay);
    }

    private TrickSnapshot CreateSnapshot()
    {
        return new TrickSnapshot(
            _currentTurnSeat,
            _currentTop,
            _currentTopSeat,
            _pendingWish,
            Array.AsReadOnly(_plays.ToArray()),
            Array.AsReadOnly(_activeSeats.OrderBy(seat => seat.ToIndex()).ToArray()),
            Array.AsReadOnly(_passedSeats.OrderBy(seat => seat.ToIndex()).ToArray()),
            Array.AsReadOnly(_finishOrder.ToArray()),
            CanCollect,
            _isDoubleVictory,
            _isRoundEnded);
    }
}
