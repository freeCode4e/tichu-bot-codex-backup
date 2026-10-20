using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class RoundStateTests
{
    [Fact]
    public void AcceptedPlayRemovesCardsAndRejectedPlayLeavesHandUnchanged()
    {
        var eight = N(8, CardSuit.Jade);
        var two = N(2, CardSuit.Jade);
        var nine = N(9, CardSuit.Jade);
        var state = CreateRound(
            (PlayerSeat.Bottom, new[] { eight, two }),
            (PlayerSeat.Right, new[] { nine }),
            (PlayerSeat.Top, new[] { N(10, CardSuit.Jade) }),
            (PlayerSeat.Left, new[] { N(11, CardSuit.Jade) }));

        Assert.Equal(TrickActionStatus.NotPlayersTurn, state.TryPlay(PlayerSeat.Right, Combo(nine)).Status);
        Assert.Equal(2, state.HandFor(PlayerSeat.Bottom).Count);

        var missingCard = N(3, CardSuit.Jade);
        var rejected = state.TryPlay(PlayerSeat.Bottom, Combo(missingCard));
        Assert.Equal(MoveValidationStatus.CardsNotInHand, rejected.MoveStatus);
        Assert.Equal(new[] { eight, two }, state.HandFor(PlayerSeat.Bottom));

        var accepted = state.TryPlay(PlayerSeat.Bottom, Combo(eight));
        Assert.True(accepted.Accepted);
        Assert.Equal(new[] { two }, state.HandFor(PlayerSeat.Bottom));
        Assert.Equal(new[] { 1, 1, 1, 1 }, state.Snapshot.HandSizes);
    }

    [Fact]
    public void CollectedDragonTrickAwardsPointsToChosenOpponent()
    {
        var dragon = new Card(CardRank.Dragon);
        var five = N(5, CardSuit.Jade);
        var state = CreateRound(
            (PlayerSeat.Bottom, new[] { dragon, five }),
            (PlayerSeat.Right, new[] { N(10, CardSuit.Sword), N(2, CardSuit.Sword) }),
            (PlayerSeat.Top, new[] { N(13, CardSuit.Pagoda), N(3, CardSuit.Pagoda) }),
            (PlayerSeat.Left, new[] { N(5, CardSuit.Star), N(4, CardSuit.Star) }));

        Assert.True(state.TryPlay(PlayerSeat.Bottom, Combo(dragon)).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Right).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Top).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Left).Accepted);
        Assert.Equal(new[] { 0, 0, 0, 0 }, state.Snapshot.CollectedPointsBySeat);

        var collected = state.TryCollect(PlayerSeat.Left);

        Assert.True(collected.Accepted);
        Assert.Equal(25, state.Snapshot.CollectedPointsBySeat[(int)PlayerSeat.Left]);
        Assert.Equal(25, state.Snapshot.TotalCollectedPoints);
        Assert.DoesNotContain(dragon, state.HandFor(PlayerSeat.Bottom));
        Assert.Contains(five, state.HandFor(PlayerSeat.Bottom));
    }

    [Fact]
    public void FinalDragonTrickCanBeCollectedAndTailendersHandTransfersToOpponents()
    {
        var five = N(5, CardSuit.Jade);
        var ten = N(10, CardSuit.Sword);
        var dragon = new Card(CardRank.Dragon);
        var phoenix = new Card(CardRank.Phoenix);
        var tailenderFive = N(5, CardSuit.Star);
        var state = CreateRound(
            (PlayerSeat.Bottom, new[] { five }),
            (PlayerSeat.Right, new[] { ten }),
            (PlayerSeat.Top, new[] { dragon }),
            (PlayerSeat.Left, new[] { phoenix, tailenderFive }));

        Assert.True(state.TryPlay(PlayerSeat.Bottom, Combo(five)).Accepted);
        Assert.True(state.TryPlay(PlayerSeat.Right, Combo(ten)).Accepted);
        var finalPlay = state.TryPlay(PlayerSeat.Top, Combo(dragon));
        Assert.True(finalPlay.Snapshot.IsRoundEnded);
        Assert.Equal(new[] { PlayerSeat.Bottom, PlayerSeat.Right, PlayerSeat.Top }, finalPlay.Snapshot.FinishOrder);
        Assert.Throws<InvalidOperationException>(() => state.ScoreRound());

        var finalTrick = state.TryCollect(PlayerSeat.Right);
        Assert.True(finalTrick.Accepted);
        Assert.Equal(40, state.Snapshot.CollectedPointsBySeat[(int)PlayerSeat.Right]);

        var score = state.ScoreRound();
        Assert.Equal(-20, score.BottomTopCardPoints);
        Assert.Equal(40, score.RightLeftCardPoints);
    }

    [Fact]
    public void ConstructorRequiresFourHandsAndRejectsDuplicatedPhysicalCards()
    {
        var eight = N(8, CardSuit.Jade);
        var missingSeat = new Dictionary<PlayerSeat, IEnumerable<Card>>
        {
            [PlayerSeat.Bottom] = new[] { eight },
            [PlayerSeat.Right] = Array.Empty<Card>(),
            [PlayerSeat.Top] = Array.Empty<Card>()
        };
        Assert.Throws<ArgumentException>(() => new RoundState(PlayerSeat.Bottom, missingSeat));

        var duplicateCard = new Dictionary<PlayerSeat, IEnumerable<Card>>
        {
            [PlayerSeat.Bottom] = new[] { eight },
            [PlayerSeat.Right] = new[] { eight },
            [PlayerSeat.Top] = Array.Empty<Card>(),
            [PlayerSeat.Left] = Array.Empty<Card>()
        };
        Assert.Throws<ArgumentException>(() => new RoundState(PlayerSeat.Bottom, duplicateCard));

        var invalidCard = new Dictionary<PlayerSeat, IEnumerable<Card>>
        {
            [PlayerSeat.Bottom] = new[] { default(Card) },
            [PlayerSeat.Right] = Array.Empty<Card>(),
            [PlayerSeat.Top] = Array.Empty<Card>(),
            [PlayerSeat.Left] = Array.Empty<Card>()
        };
        Assert.Throws<ArgumentException>(() => new RoundState(PlayerSeat.Bottom, invalidCard));
    }

    private static RoundState CreateRound(params (PlayerSeat Seat, Card[] Hand)[] hands) =>
        new(PlayerSeat.Bottom, hands.ToDictionary(item => item.Seat, item => (IEnumerable<Card>)item.Hand));

    private static CardCombination Combo(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination));
        return combination!;
    }

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
