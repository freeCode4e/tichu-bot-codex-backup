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
        LinearPolicySnapshot? frozenOpponent = null)
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
            var match = simulator.PlayMatchSummary(
                gameId,
                matchSeed,
                seat => IsCandidateSeat(seat, candidateUsesBottomTop)
                    ? agent
                    : (ITichuPolicy?)frozenAgent ??
                      new RandomBaselinePolicy(new Random(unchecked(matchSeed + ((int)seat + 1) * 1_000_003))));

            var candidateScore = candidateUsesBottomTop ? match.BottomTopScore : match.RightLeftScore;
            var opponentScore = candidateUsesBottomTop ? match.RightLeftScore : match.BottomTopScore;
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
            averageReward);
    }

    private static bool IsCandidateSeat(PlayerSeat seat, bool candidateUsesBottomTop) => candidateUsesBottomTop
        ? seat is PlayerSeat.Bottom or PlayerSeat.Top
        : seat is PlayerSeat.Right or PlayerSeat.Left;
}

public sealed record LinearPolicyEvaluationResult(
    int CompletedGames,
    int Wins,
    int Losses,
    int Draws,
    double AverageCandidateScore,
    double AverageOpponentScore,
    double AverageReward);
