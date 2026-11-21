using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Measures a fixed policy against the random baseline on fresh match seeds.</summary>
public sealed class LinearPolicyEvaluator
{
    public LinearPolicyEvaluationResult Evaluate(
        int totalGames,
        int baseSeed,
        int targetScore,
        int decisionSeconds,
        IReadOnlyList<double> weights,
        Action<string>? reportProgress = null,
        IReadOnlyList<double>? declarationWeights = null,
        IReadOnlyList<double>? passWeights = null,
        LinearPolicySnapshot? frozenOpponent = null,
        bool includeSignalMetrics = false)
    {
        if (totalGames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalGames));
        }

        ArgumentNullException.ThrowIfNull(weights);
        var simulator = new RandomSelfPlaySimulator(
            targetScore,
            decisionLimit: TimeSpan.FromSeconds(decisionSeconds));
        var wins = 0;
        var losses = 0;
        var draws = 0;
        long candidatePoints = 0;
        long opponentPoints = 0;
        var averageReward = 0.0;
        var partnerPassedAceSingleDecisions = 0;
        var partnerPassedAcePreservedSingles = 0;
        var kingSingleAceControlOpportunities = 0;
        var kingSinglesPlayedInAceControlPositions = 0;

        for (var gameId = 0; gameId < totalGames; gameId++)
        {
            var matchSeed = unchecked(baseSeed + (int)(gameId * 104_729L));
            var candidateUsesBottomTop = gameId % 2 == 0;
            var agent = new LinearPreferencePolicy(
                weights,
                recordEpisode: false,
                declarationWeights: declarationWeights,
                passWeights: passWeights);
            agent.BeginEpisode(unchecked(matchSeed ^ 0x3B7A59D1));
            var frozenAgent = frozenOpponent?.CreatePolicy();
            frozenAgent?.BeginEpisode(unchecked(matchSeed ^ 0x519A7B3D));
            var policyFactory = new Func<PlayerSeat, ITichuPolicy>(seat => IsCandidateSeat(seat, candidateUsesBottomTop)
                ? agent
                : (ITichuPolicy?)frozenAgent ??
                  new RandomBaselinePolicy(new Random(unchecked(matchSeed + ((int)seat + 1) * 1_000_003))));
            SelfPlayMatchSummary matchSummary;
            if (includeSignalMetrics)
            {
                var match = simulator.PlayMatch(gameId, matchSeed, policyFactory);
                var signalCounts = CountCandidateSignalActions(match, candidateUsesBottomTop);
                partnerPassedAceSingleDecisions += signalCounts.PartnerPassedAceSingleDecisions;
                partnerPassedAcePreservedSingles += signalCounts.PartnerPassedAcePreservedSingles;
                kingSingleAceControlOpportunities += signalCounts.KingSingleAceControlOpportunities;
                kingSinglesPlayedInAceControlPositions += signalCounts.KingSinglesPlayedInAceControlPositions;
                matchSummary = new SelfPlayMatchSummary(
                    match.GameId,
                    match.MatchSeed,
                    match.TargetScore,
                    match.BottomTopScore,
                    match.RightLeftScore,
                    match.Winner);
            }
            else
            {
                matchSummary = simulator.PlayMatchSummary(gameId, matchSeed, policyFactory);
            }

            var candidateScore = candidateUsesBottomTop ? matchSummary.BottomTopScore : matchSummary.RightLeftScore;
            var opponentScore = candidateUsesBottomTop ? matchSummary.RightLeftScore : matchSummary.BottomTopScore;
            candidatePoints += candidateScore;
            opponentPoints += opponentScore;
            wins += candidateScore > opponentScore ? 1 : 0;
            losses += candidateScore < opponentScore ? 1 : 0;
            draws += candidateScore == opponentScore ? 1 : 0;
            var reward = Math.Clamp((candidateScore - (double)opponentScore) / targetScore, -1.0, 1.0);
            averageReward += (reward - averageReward) / (gameId + 1);

            if ((gameId + 1) % 10 == 0 || gameId + 1 == totalGames)
            {
            reportProgress?.Invoke(
                    $"Evaluation {gameId + 1}/{totalGames}: win rate {wins / (double)(gameId + 1):P1}, " +
                    $"average reward {averageReward:F3}.");
            }
        }

        return new LinearPolicyEvaluationResult(
            totalGames,
            wins,
            losses,
            draws,
            candidatePoints / (double)totalGames,
            opponentPoints / (double)totalGames,
            averageReward)
        {
            PartnerPassedAceSingleDecisions = partnerPassedAceSingleDecisions,
            PartnerPassedAcePreservedSingles = partnerPassedAcePreservedSingles,
            KingSingleAceControlOpportunities = kingSingleAceControlOpportunities,
            KingSinglesPlayedInAceControlPositions = kingSinglesPlayedInAceControlPositions
        };
    }

    private static SignalActionCounts CountCandidateSignalActions(
        SelfPlayMatchRecord match,
        bool candidateUsesBottomTop)
    {
        var partnerAceSingleDecisions = 0;
        var partnerAcePreservedSingles = 0;
        var kingSingleAceControlOpportunities = 0;
        var kingSinglesPlayedInAceControlPositions = 0;

        foreach (var round in match.Rounds)
        {
            var passesBySeat = round.Passes.ToDictionary(pass => pass.Seat);
            var playedBySeat = Enum.GetValues<PlayerSeat>()
                .ToDictionary(seat => seat, _ => new HashSet<Card>());

            foreach (var action in round.Actions)
            {
                if (IsCandidateSeat(action.Seat, candidateUsesBottomTop))
                {
                    var receivedFromPartner = passesBySeat[action.Seat.Partner()].ToPartner;
                    if (!action.IsOutOfTurnBomb &&
                        receivedFromPartner.Rank == CardRank.Ace &&
                        action.HandBefore.Contains(receivedFromPartner) &&
                        action.HandBefore.Count(card => card.Rank == CardRank.Ace) >= 2 &&
                        action.PlayedCards.Count == 1 &&
                        action.PlayedCards[0].Rank == CardRank.Ace)
                    {
                        partnerAceSingleDecisions++;
                        if (action.PlayedCards[0] != receivedFromPartner)
                        {
                            partnerAcePreservedSingles++;
                        }
                    }

                    if (!action.IsOutOfTurnBomb &&
                        action.HandBefore.Any(card => card.Rank == CardRank.King) &&
                        CanPlayKingSingle(action) &&
                        AllUnplayedAcesKnownToCandidateTeam(action, passesBySeat, playedBySeat))
                    {
                        kingSingleAceControlOpportunities++;
                        if (!action.IsPass &&
                            action.ChosenType == CombinationType.Single &&
                            action.PlayedCards.Count == 1 &&
                            action.PlayedCards[0].Rank == CardRank.King)
                        {
                            kingSinglesPlayedInAceControlPositions++;
                        }
                    }
                }

                foreach (var card in action.PlayedCards)
                {
                    playedBySeat[action.Seat].Add(card);
                }
            }
        }

        return new SignalActionCounts(
            partnerAceSingleDecisions,
            partnerAcePreservedSingles,
            kingSingleAceControlOpportunities,
            kingSinglesPlayedInAceControlPositions);
    }

    private static bool AllUnplayedAcesKnownToCandidateTeam(
        SelfPlayActionRecord action,
        IReadOnlyDictionary<PlayerSeat, SelfPlayPassRecord> passesBySeat,
        IReadOnlyDictionary<PlayerSeat, HashSet<Card>> playedBySeat)
    {
        var partnerSeat = action.Seat.Partner();
        var cardPassedToPartner = passesBySeat[action.Seat].ToPartner;
        if (cardPassedToPartner.Rank != CardRank.Ace ||
            action.HandSizes[partnerSeat.ToIndex()] == 0 ||
            playedBySeat[partnerSeat].Contains(cardPassedToPartner))
        {
            return false;
        }

        var playedCards = playedBySeat.Values.SelectMany(cards => cards).ToHashSet();
        return Enum.GetValues<CardSuit>()
            .Select(suit => new Card(CardRank.Ace, suit))
            .All(ace => playedCards.Contains(ace) || action.HandBefore.Contains(ace) || ace == cardPassedToPartner);
    }

    private static bool CanPlayKingSingle(SelfPlayActionRecord action)
    {
        if (action.CurrentTopType is not null &&
            (action.CurrentTopType != CombinationType.Single ||
             action.CurrentTopStrength is not { } topStrength || topStrength >= (int)CardRank.King))
        {
            return false;
        }

        if (action.PendingWish is not { } wishedRank ||
            wishedRank == CardRank.King ||
            !action.HandBefore.Any(card => card.Rank == wishedRank))
        {
            return true;
        }

        var wishCanRespond = action.CurrentTopType is null ||
                             action.CurrentTopType == CombinationType.Single &&
                             SingleStrength(wishedRank) > action.CurrentTopStrength;
        return !wishCanRespond;
    }

    private static decimal SingleStrength(CardRank rank) => rank switch
    {
        CardRank.Phoenix => 1.5m,
        CardRank.Dragon => 15m,
        CardRank.Dog => 0m,
        _ => (int)rank
    };

    private static bool IsCandidateSeat(PlayerSeat seat, bool candidateUsesBottomTop) => candidateUsesBottomTop
        ? seat is PlayerSeat.Bottom or PlayerSeat.Top
        : seat is PlayerSeat.Right or PlayerSeat.Left;

    private sealed record SignalActionCounts(
        int PartnerPassedAceSingleDecisions,
        int PartnerPassedAcePreservedSingles,
        int KingSingleAceControlOpportunities,
        int KingSinglesPlayedInAceControlPositions);
}

public sealed record LinearPolicyEvaluationResult(
    int CompletedGames,
    int Wins,
    int Losses,
    int Draws,
    double AverageCandidateScore,
    double AverageOpponentScore,
    double AverageReward)
{
    public int PartnerPassedAceSingleDecisions { get; init; }

    public int PartnerPassedAcePreservedSingles { get; init; }

    public int KingSingleAceControlOpportunities { get; init; }

    public int KingSinglesPlayedInAceControlPositions { get; init; }
}
