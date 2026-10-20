using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Trains a shared play policy against the random baseline and saves each match.</summary>
public sealed class LinearPolicyTrainer
{
    public const double DefaultLearningRate = 0.02;

    public LinearPolicyTrainingCheckpoint Train(
        int totalGames,
        int baseSeed,
        int targetScore,
        int decisionSeconds,
        string outputDirectory,
        double learningRate = DefaultLearningRate,
        Action<string>? reportProgress = null,
        LinearPolicySnapshot? frozenOpponent = null)
    {
        if (totalGames <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalGames));
        }

        var store = new PolicyTrainingStore(
            outputDirectory,
            baseSeed,
            targetScore,
            decisionSeconds,
            learningRate,
            frozenOpponent?.Fingerprint ?? LinearPolicySnapshot.RandomBaselineFingerprint);
        var checkpoint = store.LoadOrCreate(frozenOpponent);
        if (checkpoint.CompletedGames >= totalGames)
        {
            reportProgress?.Invoke(
                $"Already complete: {checkpoint.CompletedGames} training matches are saved in {store.DirectoryPath}.");
            return checkpoint;
        }

        var simulator = new RandomSelfPlaySimulator(
            targetScore,
            decisionLimit: TimeSpan.FromSeconds(decisionSeconds));
        reportProgress?.Invoke(
            $"Training output: {store.DirectoryPath}; completed {checkpoint.CompletedGames}; target {totalGames}; " +
            $"opponent {(frozenOpponent is null ? "random baseline" : $"frozen policy {frozenOpponent.Fingerprint[..12]}")}.");

        for (var gameId = checkpoint.CompletedGames; gameId < totalGames; gameId++)
        {
            var matchSeed = unchecked(baseSeed + (int)(gameId * 104_729L));
            var agent = new LinearPreferencePolicy(
                checkpoint.Weights,
                checkpoint.RewardBaseline,
                declarationWeights: checkpoint.DeclarationWeights,
                passWeights: checkpoint.PassWeights);
            agent.BeginEpisode(unchecked(matchSeed ^ 0x2C1B3C6D));
            var frozenAgent = frozenOpponent?.CreatePolicy();
            frozenAgent?.BeginEpisode(unchecked(matchSeed ^ 0x519A7B3D));
            var candidateUsesBottomTop = gameId % 2 == 0;
            var match = simulator.PlayMatchSummary(
                gameId,
                matchSeed,
                seat => IsCandidateSeat(seat, candidateUsesBottomTop)
                    ? agent
                    : (ITichuPolicy?)frozenAgent ??
                      new RandomBaselinePolicy(new Random(unchecked(matchSeed + (int)seat * 1_000_003))));

            var candidateScore = candidateUsesBottomTop ? match.BottomTopScore : match.RightLeftScore;
            var opponentScore = candidateUsesBottomTop ? match.RightLeftScore : match.BottomTopScore;
            var reward = Math.Clamp((candidateScore - (double)opponentScore) / targetScore, -1.0, 1.0);
            agent.CompleteEpisode(reward, learningRate);

            checkpoint = checkpoint with
            {
                CompletedGames = gameId + 1,
                Wins = checkpoint.Wins + (candidateScore > opponentScore ? 1 : 0),
                Losses = checkpoint.Losses + (candidateScore < opponentScore ? 1 : 0),
                Draws = checkpoint.Draws + (candidateScore == opponentScore ? 1 : 0),
                AverageReward = checkpoint.AverageReward +
                    (reward - checkpoint.AverageReward) / (gameId + 1),
                RewardBaseline = agent.RewardBaseline,
                Weights = agent.Weights.ToArray(),
                DeclarationWeights = agent.DeclarationWeights.ToArray(),
                PassWeights = agent.PassWeights.ToArray(),
                UpdatedAt = DateTimeOffset.UtcNow
            };
            store.Save(checkpoint);

            if (checkpoint.CompletedGames % 10 == 0 || checkpoint.CompletedGames == totalGames)
            {
                var winRate = checkpoint.Wins / (double)checkpoint.CompletedGames;
                reportProgress?.Invoke(
                    $"Training {checkpoint.CompletedGames}/{totalGames}: win rate {winRate:P1}, " +
                    $"average reward {checkpoint.AverageReward:F3}, last-match decisions {agent.LastEpisodeDecisionCount}.");
            }
        }

        return checkpoint;
    }

    private static bool IsCandidateSeat(PlayerSeat seat, bool candidateUsesBottomTop) => candidateUsesBottomTop
        ? seat is PlayerSeat.Bottom or PlayerSeat.Top
        : seat is PlayerSeat.Right or PlayerSeat.Left;
}
