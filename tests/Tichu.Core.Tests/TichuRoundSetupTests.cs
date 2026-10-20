using Tichu.Core;

namespace Tichu.Core.Tests;

public sealed class TichuRoundSetupTests
{
    [Fact]
    public void PreparedRoundUsesMahJongHolderAfterExchangeAsOpeningSeat()
    {
        var deal = TichuDealer.FromDeckOrder(TichuDeck.CreateStandard());
        var selections = deal.FourteenCardHands.ToDictionary(
            pair => pair.Key,
            pair => new CardPassSelection(pair.Value[0], pair.Value[1], pair.Value[2]));
        var bottomPass = selections[PlayerSeat.Bottom];
        selections[PlayerSeat.Bottom] = bottomPass with { ToLeft = new Card(CardRank.MahJong) };

        var prepared = TichuRoundSetup.Prepare(deal, selections);

        Assert.Equal(PlayerSeat.Left, prepared.OpeningSeat);
        Assert.Contains(new Card(CardRank.MahJong), prepared.Hands[PlayerSeat.Left]);
        Assert.DoesNotContain(new Card(CardRank.MahJong), prepared.Hands[PlayerSeat.Bottom]);

        var round = prepared.CreateRoundState();
        Assert.Equal(PlayerSeat.Left, round.Snapshot.Trick.CurrentTurnSeat);
        Assert.Equal(new[] { 14, 14, 14, 14 }, round.Snapshot.HandSizes);
    }
}
