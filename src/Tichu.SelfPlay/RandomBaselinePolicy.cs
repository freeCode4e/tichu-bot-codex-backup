using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>A deliberately simple policy used to exercise and seed the simulator.</summary>
public sealed class RandomBaselinePolicy : ITichuPolicy
{
    private readonly Random _random;

    public RandomBaselinePolicy(Random random) =>
        _random = random ?? throw new ArgumentNullException(nameof(random));

    public void BeginRound(PolicyRoundContext context) => ArgumentNullException.ThrowIfNull(context);

    public bool ShouldCallGrand(IReadOnlyList<Card> hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        var ranks = hand.Select(card => card.Rank).ToArray();
        return HasFourOfAKind(hand) ||
               ranks.Contains(CardRank.Dragon) && ranks.Contains(CardRank.Phoenix) ||
               ranks.Count(rank => rank == CardRank.Ace) >= 3;
    }

    public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Seat.ToIndex();
        ArgumentNullException.ThrowIfNull(context.Snapshot);
        return ShouldCallGrand(hand);
    }

    public bool ShouldCallTichu(IReadOnlyList<Card> hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        var ranks = hand.Select(card => card.Rank).ToArray();
        var topCards = ranks.Count(rank => rank is CardRank.Ace or CardRank.Dragon or CardRank.Phoenix);
        return HasFourOfAKind(hand) ||
               ranks.Contains(CardRank.Dragon) && ranks.Contains(CardRank.Phoenix) ||
               topCards >= 4;
    }

    public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Seat.ToIndex();
        ArgumentNullException.ThrowIfNull(context.Snapshot);
        return ShouldCallTichu(hand);
    }

    public CardPassSelection ChoosePass(IReadOnlyList<Card> hand)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (hand.Count != 14 || hand.Distinct().Count() != hand.Count)
        {
            throw new ArgumentException("Passing requires fourteen distinct cards.", nameof(hand));
        }

        var partnerCard = hand
            .OrderByDescending(PartnerSupportValue)
            .ThenBy(card => card.Rank)
            .First();
        var opponentCards = hand
            .Where(card => card != partnerCard)
            .OrderBy(card => OpponentDiscardValue(card))
            .ThenBy(card => card.Rank)
            .Take(2)
            .ToArray();
        return new CardPassSelection(opponentCards[0], partnerCard, opponentCards[1]);
    }

    public LegalMove ChooseMove(IReadOnlyList<LegalMove> legalMoves)
    {
        ArgumentNullException.ThrowIfNull(legalMoves);
        if (legalMoves.Count == 0)
        {
            throw new ArgumentException("At least one legal move is required.", nameof(legalMoves));
        }

        return legalMoves[_random.Next(legalMoves.Count)];
    }

    public LegalMove ChooseMoveFromHand(
        IReadOnlyList<Card> hand,
        CardCombination? currentTop = null,
        CardRank? pendingWish = null)
    {
        ArgumentNullException.ThrowIfNull(hand);
        if (currentTop is not null)
        {
            return ChooseMove(MoveGenerator.Generate(hand, currentTop, pendingWish));
        }

        if (hand.Count is 0 or > 14 || hand.Any(card => !card.IsValid) || hand.Distinct().Count() != hand.Count)
        {
            throw new ArgumentException("A lead requires a hand of one to fourteen distinct valid cards.", nameof(hand));
        }

        if (pendingWish is { } wish && wish is >= CardRank.Two and <= CardRank.Ace)
        {
            var wishedCards = hand.Where(card => card.Rank == wish).ToArray();
            if (wishedCards.Length > 0)
            {
                var validation = MoveRules.ValidatePlay(hand, CardCombination.ClassifyAll(new[]
                {
                    wishedCards[_random.Next(wishedCards.Length)]
                })[0], pendingWish: pendingWish);
                if (validation.IsLegal)
                {
                    return new LegalMove(validation.ResolvedPlay);
                }
            }
        }

        var handCards = hand.ToArray();
        var subsetCount = 1 << handCards.Length;
        for (var attempt = 0; attempt < 512; attempt++)
        {
            var mask = _random.Next(1, subsetCount);
            var candidateCards = new List<Card>();
            for (var index = 0; index < handCards.Length; index++)
            {
                if ((mask & (1 << index)) != 0)
                {
                    candidateCards.Add(handCards[index]);
                }
            }

            var candidateMoves = new List<LegalMove>();
            foreach (var combination in CardCombination.ClassifyAll(candidateCards))
            {
                var validation = MoveRules.ValidatePlay(handCards, combination, pendingWish: pendingWish);
                if (!validation.IsLegal)
                {
                    continue;
                }

                if (combination.Cards.Any(card => card.Rank == CardRank.MahJong))
                {
                    for (var rank = (int)CardRank.Two; rank <= (int)CardRank.Ace; rank++)
                    {
                        candidateMoves.Add(new LegalMove(validation.ResolvedPlay, (CardRank)rank));
                    }
                }
                else
                {
                    candidateMoves.Add(new LegalMove(validation.ResolvedPlay));
                }
            }

            if (candidateMoves.Count > 0)
            {
                return ChooseMove(candidateMoves);
            }
        }

        // Sampling normally finds a lead quickly. The exhaustive path guarantees
        // termination for unusual hands and keeps this policy correct as rules grow.
        return ChooseMove(MoveGenerator.Generate(handCards, pendingWish: pendingWish));
    }

    public LegalMove? ChooseOutOfTurnBomb(IReadOnlyList<LegalMove> bombs)
    {
        ArgumentNullException.ThrowIfNull(bombs);
        if (bombs.Count == 0 || _random.NextDouble() >= 0.12)
        {
            return null;
        }

        return bombs[_random.Next(bombs.Count)];
    }

    public LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return ChooseMoveFromHand(hand, snapshot.Trick.CurrentTop, snapshot.Trick.PendingWish);
    }

    public LegalMove? ChooseOutOfTurnBomb(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        IReadOnlyList<LegalMove> bombs)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(snapshot);
        return ChooseOutOfTurnBomb(bombs);
    }

    public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner)
    {
        var opponents = Enum.GetValues<PlayerSeat>()
            .Where(seat => seat != dragonWinner && seat != dragonWinner.Partner())
            .ToArray();
        return opponents[_random.Next(opponents.Length)];
    }

    private static bool HasFourOfAKind(IEnumerable<Card> cards) => cards
        .Where(card => card.IsOrdinary)
        .GroupBy(card => card.Rank)
        .Any(group => group.Count() == 4);

    private static int PartnerSupportValue(Card card) => card.Rank switch
    {
        CardRank.Dragon => 100,
        CardRank.Phoenix => 90,
        CardRank.Ace => 80,
        CardRank.King => 60,
        CardRank.Queen => 50,
        _ => (int)card.Rank
    };

    private static int OpponentDiscardValue(Card card) => card.Rank switch
    {
        CardRank.Dog => 0,
        CardRank.MahJong => 30,
        CardRank.Phoenix => 40,
        CardRank.Dragon => 41,
        CardRank.Ace => 35,
        CardRank.King => 32,
        _ => (int)card.Rank
    };
}
