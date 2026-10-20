using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class RoundScoringTests
{
    [Fact]
    public void NormalScoringTransfersTailendersTricksAndHandAndAppliesTichuCalls()
    {
        var collectedPoints = new[] { 10, 20, 15, 25 };
        var tailenderHand = new[] { new Card(CardRank.Dragon), N(5, CardSuit.Jade) };
        var finishOrder = new[] { PlayerSeat.Right, PlayerSeat.Top, PlayerSeat.Left };
        var calls = new Dictionary<PlayerSeat, TichuCall>
        {
            [PlayerSeat.Right] = TichuCall.Small,
            [PlayerSeat.Top] = TichuCall.Grand
        };

        var score = RoundScoring.Calculate(
            collectedPoints,
            tailenderHand,
            finishOrder,
            tailenderSeat: PlayerSeat.Bottom,
            isDoubleVictory: false,
            calls: calls);

        // Bottom's 10 trick points go to Right; Bottom's 30 remaining hand points go to Right/Left.
        Assert.Equal(15, score.BottomTopCardPoints);
        Assert.Equal(85, score.RightLeftCardPoints);
        Assert.Equal(-200, score.BottomTopTichuPoints);
        Assert.Equal(100, score.RightLeftTichuPoints);
        Assert.Equal(-185, score.BottomTopTotal);
        Assert.Equal(185, score.RightLeftTotal);
    }

    [Fact]
    public void DoubleVictoryScores200AndStillScoresIndependentTichuCalls()
    {
        var calls = new Dictionary<PlayerSeat, TichuCall>
        {
            [PlayerSeat.Bottom] = TichuCall.Grand,
            [PlayerSeat.Right] = TichuCall.Small
        };

        var score = RoundScoring.Calculate(
            new[] { 10, 20, 30, 40 },
            new[] { new Card(CardRank.Dragon) },
            new[] { PlayerSeat.Bottom, PlayerSeat.Top },
            tailenderSeat: null,
            isDoubleVictory: true,
            calls: calls);

        Assert.True(score.IsDoubleVictory);
        Assert.Equal(200, score.BottomTopCardPoints);
        Assert.Equal(0, score.RightLeftCardPoints);
        Assert.Equal(200, score.BottomTopTichuPoints);
        Assert.Equal(-100, score.RightLeftTichuPoints);
        Assert.Equal(400, score.BottomTopTotal);
        Assert.Equal(-100, score.RightLeftTotal);
    }

    [Fact]
    public void RoundStateCanSettleDoubleVictoryAndCannotScoreAnActiveRound()
    {
        var mahJong = new Card(CardRank.MahJong);
        var dragon = new Card(CardRank.Dragon);
        var state = new RoundState(PlayerSeat.Bottom, new Dictionary<PlayerSeat, IEnumerable<Card>>
        {
            [PlayerSeat.Bottom] = new[] { mahJong },
            [PlayerSeat.Right] = new[] { N(2, CardSuit.Jade) },
            [PlayerSeat.Top] = new[] { dragon },
            [PlayerSeat.Left] = new[] { N(3, CardSuit.Jade) }
        });

        Assert.Throws<InvalidOperationException>(() => state.ScoreRound());
        Assert.True(state.TryPlay(PlayerSeat.Bottom, Combo(mahJong)).Accepted);
        Assert.True(state.TryPass(PlayerSeat.Right).Accepted);
        Assert.True(state.TryPlay(PlayerSeat.Top, Combo(dragon)).Accepted);

        var score = state.ScoreRound();

        Assert.True(score.IsDoubleVictory);
        Assert.Equal(200, score.BottomTopCardPoints);
        Assert.Equal(0, score.RightLeftCardPoints);
    }

    [Fact]
    public void InvalidFinishPatternsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => RoundScoring.Calculate(
            new[] { 0, 0, 0, 0 },
            Array.Empty<Card>(),
            new[] { PlayerSeat.Bottom, PlayerSeat.Right },
            tailenderSeat: null,
            isDoubleVictory: true));

        Assert.Throws<ArgumentException>(() => RoundScoring.Calculate(
            new[] { 0, 0, 0, 0 },
            Array.Empty<Card>(),
            new[] { PlayerSeat.Bottom, PlayerSeat.Right },
            tailenderSeat: PlayerSeat.Left,
            isDoubleVictory: false));
    }

    private static CardCombination Combo(params Card[] cards)
    {
        Assert.True(CardCombination.TryClassify(cards, out var combination));
        return combination!;
    }

    private static Card N(int rank, CardSuit suit) => new((CardRank)rank, suit);
}
