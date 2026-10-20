using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class TrickStateTests
{
    [Fact]
    public void PlayersTakeTurnsAndTrickIsCollectedAfterEveryOtherActivePlayerPasses()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var bottomEight = Combo(N(8, CardSuit.Jade));
        var rightNine = Combo(N(9, CardSuit.Jade));

        Assert.Equal(TrickActionStatus.NoTrickToCollect, state.TryCollect().Status);
        Assert.Equal(TrickActionStatus.NotPlayersTurn,
            state.TryPlay(PlayerSeat.Right, new[] { N(9, CardSuit.Jade) }, rightNine).Status);

        var lead = state.TryPlay(PlayerSeat.Bottom, new[] { N(8, CardSuit.Jade), N(2, CardSuit.Jade) }, bottomEight);
        Assert.True(lead.Accepted);
        Assert.Same(bottomEight, lead.ResolvedPlay);
        Assert.Equal(PlayerSeat.Right, lead.Snapshot.CurrentTurnSeat);

        var response = state.TryPlay(PlayerSeat.Right, new[] { N(9, CardSuit.Jade), N(3, CardSuit.Jade) }, rightNine);
        Assert.True(response.Accepted);
        Assert.Equal(PlayerSeat.Top, response.Snapshot.CurrentTurnSeat);
        Assert.Empty(response.Snapshot.PassedSeats);

        Assert.True(state.TryPass(PlayerSeat.Top, new[] { N(10, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Left, new[] { N(11, CardSuit.Jade) }).Accepted);
        var finalPass = state.TryPass(PlayerSeat.Bottom, new[] { N(8, CardSuit.Jade), N(2, CardSuit.Jade) });

        Assert.True(finalPass.Accepted);
        Assert.True(finalPass.Snapshot.CanCollect);
        Assert.Null(finalPass.Snapshot.CurrentTurnSeat);

        var collected = state.TryCollect();
        Assert.True(collected.Accepted);
        Assert.Equal(PlayerSeat.Right, collected.Resolution!.WinnerSeat);
        Assert.Equal(PlayerSeat.Right, collected.Resolution.PointsRecipientSeat);
        Assert.Equal(new[] { N(8, CardSuit.Jade), N(9, CardSuit.Jade) }, collected.Resolution.Cards);
        Assert.Equal(PlayerSeat.Right, collected.Resolution.NextLeadSeat);
        Assert.Empty(collected.Snapshot.Plays);
        Assert.Equal(PlayerSeat.Right, collected.Snapshot.CurrentTurnSeat);
    }

    [Fact]
    public void OutOfTurnBombReopensTrickAfterThreePasses()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var eight = N(8, CardSuit.Jade);
        var bombCards = new[]
        {
            N(9, CardSuit.Jade), N(9, CardSuit.Sword), N(9, CardSuit.Pagoda), N(9, CardSuit.Star)
        };
        var bomb = Combo(bombCards);

        Assert.True(state.TryPlay(PlayerSeat.Bottom, new[] { eight }, Combo(eight)).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Right, bombCards).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Top, new[] { N(10, CardSuit.Jade) }).Accepted);
        var allPassed = state.TryPass(PlayerSeat.Left, new[] { N(11, CardSuit.Jade) });
        Assert.True(allPassed.Snapshot.CanCollect);

        var bombResult = state.TryBomb(PlayerSeat.Right, bombCards, bomb);

        Assert.True(bombResult.Accepted);
        Assert.Same(bomb, bombResult.ResolvedPlay);
        Assert.False(bombResult.Snapshot.CanCollect);
        Assert.Equal(PlayerSeat.Top, bombResult.Snapshot.CurrentTurnSeat);
        Assert.Empty(bombResult.Snapshot.PassedSeats);

        Assert.True(state.TryPass(PlayerSeat.Top, new[] { N(10, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Left, new[] { N(11, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Bottom, new[] { N(2, CardSuit.Jade) }).Snapshot.CanCollect);
        var resolution = state.TryCollect().Resolution!;
        Assert.Equal(PlayerSeat.Right, resolution.WinnerSeat);
        Assert.Equal(5, resolution.Cards.Count);
    }

    [Fact]
    public void DogTransfersLeadToPartnerAndSkipsToPartnerRightIfPartnerIsOut()
    {
        var dog = new Card(CardRank.Dog);
        var dogCombination = Combo(dog);
        var fullTable = new TrickState(PlayerSeat.Bottom);

        var transfer = fullTable.TryPlay(PlayerSeat.Bottom, new[] { dog }, dogCombination);

        Assert.True(transfer.Accepted);
        Assert.Same(dogCombination, transfer.ResolvedPlay);
        Assert.Equal(PlayerSeat.Top, transfer.Snapshot.CurrentTurnSeat);
        Assert.Null(transfer.Snapshot.CurrentTop);
        Assert.Empty(transfer.Snapshot.Plays);

        var partnerOut = new TrickState(PlayerSeat.Bottom, new[] { PlayerSeat.Bottom, PlayerSeat.Right, PlayerSeat.Left });
        var skippedPartner = partnerOut.TryPlay(PlayerSeat.Bottom, new[] { dog }, dogCombination);
        Assert.Equal(PlayerSeat.Left, skippedPartner.Snapshot.CurrentTurnSeat);
    }

    [Fact]
    public void MahjongWishConstrainsOrdinaryTurnAndIsClearedWhenFulfilled()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var mahJong = new Card(CardRank.MahJong);
        var lead = state.TryPlay(
            PlayerSeat.Bottom,
            new[] { mahJong, N(4, CardSuit.Jade) },
            Combo(mahJong),
            wishForMahJong: CardRank.Seven);
        Assert.True(lead.Accepted);
        Assert.Equal(CardRank.Seven, lead.Snapshot.PendingWish);

        var seven = N(7, CardSuit.Jade);
        var eight = N(8, CardSuit.Jade);
        var illegal = state.TryPlay(PlayerSeat.Right, new[] { seven, eight }, Combo(eight));
        Assert.Equal(TrickActionStatus.MoveRejected, illegal.Status);
        Assert.Equal(MoveValidationStatus.MustFulfillWish, illegal.MoveStatus);
        Assert.Equal(PlayerSeat.Right, illegal.Snapshot.CurrentTurnSeat);

        var fulfilled = state.TryPlay(PlayerSeat.Right, new[] { seven, eight }, Combo(seven));
        Assert.True(fulfilled.Accepted);
        Assert.Null(fulfilled.Snapshot.PendingWish);
    }

    [Fact]
    public void OutOfTurnBombDoesNotRequireWishButPlayingWishedRankFulfillsIt()
    {
        var mahJong = new Card(CardRank.MahJong);
        var sevens = new[]
        {
            N(7, CardSuit.Jade), N(7, CardSuit.Sword), N(7, CardSuit.Pagoda), N(7, CardSuit.Star)
        };
        var eights = new[]
        {
            N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star)
        };

        var wishedBombState = new TrickState(PlayerSeat.Bottom);
        Assert.True(wishedBombState.TryPlay(
            PlayerSeat.Bottom, new[] { mahJong }, Combo(mahJong), CardRank.Seven).Accepted);
        var wishedBomb = wishedBombState.TryBomb(PlayerSeat.Left, sevens, Combo(sevens));
        Assert.True(wishedBomb.Accepted);
        Assert.Null(wishedBomb.Snapshot.PendingWish);

        var otherBombState = new TrickState(PlayerSeat.Bottom);
        Assert.True(otherBombState.TryPlay(
            PlayerSeat.Bottom, new[] { mahJong }, Combo(mahJong), CardRank.Seven).Accepted);
        var otherBomb = otherBombState.TryBomb(PlayerSeat.Left, eights, Combo(eights));
        Assert.True(otherBomb.Accepted);
        Assert.Equal(CardRank.Seven, otherBomb.Snapshot.PendingWish);
    }

    [Fact]
    public void InvalidWishAndWishWithoutMahjongAreRejectedWithoutChangingTurn()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var eight = N(8, CardSuit.Jade);
        var hand = new[] { eight };

        Assert.Equal(TrickActionStatus.InvalidWishRank,
            state.TryPlay(PlayerSeat.Bottom, hand, Combo(eight), CardRank.Phoenix).Status);
        Assert.Equal(TrickActionStatus.NewWishRequiresMahJong,
            state.TryPlay(PlayerSeat.Bottom, hand, Combo(eight), CardRank.Seven).Status);
        Assert.Equal(PlayerSeat.Bottom, state.Snapshot.CurrentTurnSeat);
    }

    [Fact]
    public void AcceptedPhoenixResponseReturnsResolvedStrength()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var eight = N(8, CardSuit.Jade);
        var phoenix = new Card(CardRank.Phoenix);

        Assert.True(state.TryPlay(PlayerSeat.Bottom, new[] { eight }, Combo(eight)).Accepted);
        var response = state.TryPlay(PlayerSeat.Right, new[] { phoenix }, Combo(phoenix));

        Assert.True(response.Accepted);
        Assert.Equal(8.5m, response.ResolvedPlay!.Strength);
        Assert.Equal(8.5m, response.Snapshot.CurrentTop!.Strength);
    }

    [Fact]
    public void DragonTrickRequiresOpponentRecipientAndTransfersPoints()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var dragon = new Card(CardRank.Dragon);

        Assert.True(state.TryPlay(PlayerSeat.Bottom, new[] { dragon }, Combo(dragon)).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Right, new[] { N(2, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Top, new[] { N(3, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Left, new[] { N(4, CardSuit.Jade) }).Accepted);

        Assert.Equal(TrickActionStatus.DragonRecipientRequired, state.TryCollect().Status);
        Assert.Equal(TrickActionStatus.InvalidDragonRecipient, state.TryCollect(PlayerSeat.Top).Status);
        var collected = state.TryCollect(PlayerSeat.Left);

        Assert.True(collected.Accepted);
        Assert.Equal(PlayerSeat.Bottom, collected.Resolution!.WinnerSeat);
        Assert.Equal(PlayerSeat.Left, collected.Resolution.PointsRecipientSeat);
        Assert.Equal(new[] { dragon }, collected.Resolution.Cards);
    }

    [Fact]
    public void BombedDragonTrickIsCollectedNormallyWithoutDragonGift()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var dragon = new Card(CardRank.Dragon);
        var bombCards = new[]
        {
            N(8, CardSuit.Jade), N(8, CardSuit.Sword), N(8, CardSuit.Pagoda), N(8, CardSuit.Star)
        };

        Assert.True(state.TryPlay(PlayerSeat.Bottom, new[] { dragon }, Combo(dragon)).Accepted);
        Assert.True(state.TryPlay(PlayerSeat.Right, bombCards, Combo(bombCards)).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Top, new[] { N(9, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Left, new[] { N(10, CardSuit.Jade) }).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Bottom, new[] { dragon }).Snapshot.CanCollect);

        Assert.Equal(TrickActionStatus.UnexpectedDragonRecipient, state.TryCollect(PlayerSeat.Left).Status);
        var collected = state.TryCollect();

        Assert.Equal(PlayerSeat.Right, collected.Resolution!.WinnerSeat);
        Assert.Equal(PlayerSeat.Right, collected.Resolution.PointsRecipientSeat);
        Assert.Equal(5, collected.Resolution.Cards.Count);
    }

    [Fact]
    public void PartnerFinishingFirstAndSecondEndsRoundAsDoubleVictory()
    {
        var state = new TrickState(PlayerSeat.Bottom);
        var mahJong = new Card(CardRank.MahJong);
        var dragon = new Card(CardRank.Dragon);

        var firstOut = state.TryPlay(PlayerSeat.Bottom, new[] { mahJong }, Combo(mahJong));
        Assert.True(firstOut.Accepted);
        Assert.Equal(new[] { PlayerSeat.Bottom }, firstOut.Snapshot.FinishOrder);
        Assert.True(state.TryPass(PlayerSeat.Right, new[] { N(2, CardSuit.Jade) }).Accepted);

        var secondOut = state.TryPlay(PlayerSeat.Top, new[] { dragon }, Combo(dragon));

        Assert.True(secondOut.Accepted);
        Assert.True(secondOut.Snapshot.IsDoubleVictory);
        Assert.True(secondOut.Snapshot.IsRoundEnded);
        Assert.Null(secondOut.Snapshot.CurrentTurnSeat);
        Assert.Equal(new[] { PlayerSeat.Bottom, PlayerSeat.Top }, secondOut.Snapshot.FinishOrder);
        Assert.Equal(TrickActionStatus.RoundEnded, state.TryCollect().Status);
    }

    [Fact]
    public void RoundEndingOnLastActivePlayerAllowsFinalTrickToBeCollectedImmediately()
    {
        var state = new TrickState(PlayerSeat.Bottom, new[] { PlayerSeat.Bottom, PlayerSeat.Right });
        var eight = N(8, CardSuit.Jade);

        var lastPlay = state.TryPlay(PlayerSeat.Bottom, new[] { eight }, Combo(eight));
        Assert.True(lastPlay.Snapshot.IsRoundEnded);
        Assert.True(lastPlay.Snapshot.CanCollect);

        var collected = state.TryCollect();

        Assert.True(collected.Accepted);
        Assert.Equal(PlayerSeat.Bottom, collected.Resolution!.WinnerSeat);
        Assert.Null(collected.Resolution.NextLeadSeat);
    }

    private static CardCombination Combo(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination));
        return combination!;
    }

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
