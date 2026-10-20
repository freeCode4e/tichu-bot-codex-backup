using System.Text.Json;

namespace Tichu.SelfPlay;

/// <summary>Atomically persists learned weights and completed-game progress.</summary>
public sealed class PolicyTrainingStore
{
    private const int CheckpointSchemaVersion = 1;
    private static readonly int[] PreviousDeclarationFeatureCounts = [23, 31];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _checkpointPath;
    private readonly PolicyTrainingConfiguration _configuration;
    private readonly bool _validateOpponentFingerprint;

    public PolicyTrainingStore(
        string directory,
        int baseSeed,
        int targetScore,
        int decisionSeconds,
        double learningRate,
        string? opponentFingerprint = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (targetScore <= 0 || decisionSeconds <= 0 || learningRate <= 0 || !double.IsFinite(learningRate))
        {
            throw new ArgumentOutOfRangeException(nameof(targetScore), "Training settings must be positive and finite.");
        }

        if (opponentFingerprint is not null && string.IsNullOrWhiteSpace(opponentFingerprint))
        {
            throw new ArgumentException("Opponent fingerprint cannot be empty.", nameof(opponentFingerprint));
        }

        _validateOpponentFingerprint = opponentFingerprint is not null;
        DirectoryPath = Path.GetFullPath(directory);
        Directory.CreateDirectory(DirectoryPath);
        _checkpointPath = Path.Combine(DirectoryPath, "policy-checkpoint.json");
        _configuration = new PolicyTrainingConfiguration(
            CheckpointSchemaVersion,
            LinearPreferencePolicy.ModelSchemaVersion,
            MoveFeatureEncoder.FeatureCount,
            baseSeed,
            targetScore,
            decisionSeconds,
            learningRate)
        {
            OpponentFingerprint = opponentFingerprint ?? LinearPolicySnapshot.RandomBaselineFingerprint
        };
    }

    public string DirectoryPath { get; }

    public LinearPolicyTrainingCheckpoint LoadOrCreate(LinearPolicySnapshot? initialPolicy = null)
    {
        if (!File.Exists(_checkpointPath))
        {
            return new LinearPolicyTrainingCheckpoint(
                _configuration,
                0,
                0,
                0,
                0,
                0.0,
                initialPolicy?.RewardBaseline ?? 0.0,
                initialPolicy?.Weights.ToArray() ?? new double[MoveFeatureEncoder.FeatureCount],
                DateTimeOffset.UtcNow)
            {
                DeclarationWeights = initialPolicy?.DeclarationWeights.ToArray() ??
                                     new double[DeclarationFeatureEncoder.FeatureCount],
                PassWeights = initialPolicy?.PassWeights.ToArray() ?? new double[PassFeatureEncoder.FeatureCount]
            };
        }

        var checkpoint = JsonSerializer.Deserialize<LinearPolicyTrainingCheckpoint>(
                             File.ReadAllText(_checkpointPath),
                             JsonOptions)
                         ?? throw new InvalidDataException("The policy checkpoint is empty or invalid.");
        checkpoint = UpgradeDeclarationFeatureSchema(checkpoint);
        var expectedConfiguration = _validateOpponentFingerprint
            ? _configuration
            : _configuration with { OpponentFingerprint = checkpoint.Configuration.OpponentFingerprint };
        if (checkpoint.Configuration != expectedConfiguration)
        {
            throw new InvalidOperationException(
                "This training directory belongs to different seed, score, timer, model, or learning-rate settings. Use a new directory for a different run.");
        }

        if (checkpoint.Weights.Length != MoveFeatureEncoder.FeatureCount ||
            checkpoint.Weights.Any(weight => !double.IsFinite(weight)) ||
            checkpoint.DeclarationWeights is null ||
            checkpoint.DeclarationWeights.Length != DeclarationFeatureEncoder.FeatureCount ||
            checkpoint.DeclarationWeights.Any(weight => !double.IsFinite(weight)) ||
            checkpoint.PassWeights is null ||
            checkpoint.PassWeights.Length != PassFeatureEncoder.FeatureCount ||
            checkpoint.PassWeights.Any(weight => !double.IsFinite(weight)) ||
            checkpoint.CompletedGames < 0 ||
            !double.IsFinite(checkpoint.RewardBaseline) ||
            !double.IsFinite(checkpoint.AverageReward))
        {
            throw new InvalidDataException("The policy checkpoint contains invalid progress or weights.");
        }

        return checkpoint;
    }

    public void Save(LinearPolicyTrainingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        if (checkpoint.Configuration != _configuration ||
            checkpoint.Weights.Length != MoveFeatureEncoder.FeatureCount ||
            checkpoint.DeclarationWeights is null ||
            checkpoint.DeclarationWeights.Length != DeclarationFeatureEncoder.FeatureCount ||
            checkpoint.DeclarationWeights.Any(weight => !double.IsFinite(weight)) ||
            checkpoint.PassWeights is null ||
            checkpoint.PassWeights.Length != PassFeatureEncoder.FeatureCount ||
            checkpoint.PassWeights.Any(weight => !double.IsFinite(weight)))
        {
            throw new ArgumentException("The checkpoint does not match this training run.", nameof(checkpoint));
        }

        var temporary = _checkpointPath + ".tmp";
        var bytes = JsonSerializer.SerializeToUtf8Bytes(checkpoint with { UpdatedAt = DateTimeOffset.UtcNow }, JsonOptions);
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, _checkpointPath, overwrite: true);
    }

    private LinearPolicyTrainingCheckpoint UpgradeDeclarationFeatureSchema(
        LinearPolicyTrainingCheckpoint checkpoint)
    {
        var previousFeatureCount = checkpoint.Configuration.DeclarationFeatureCount;
        var previousConfiguration = _configuration with { DeclarationFeatureCount = previousFeatureCount };
        if (!PreviousDeclarationFeatureCounts.Contains(previousFeatureCount) ||
            checkpoint.Configuration != previousConfiguration ||
            checkpoint.DeclarationWeights is null ||
            checkpoint.DeclarationWeights.Length != previousFeatureCount ||
            checkpoint.DeclarationWeights.Any(weight => !double.IsFinite(weight)))
        {
            return checkpoint;
        }

        var expandedWeights = new double[DeclarationFeatureEncoder.FeatureCount];
        Array.Copy(checkpoint.DeclarationWeights, expandedWeights, previousFeatureCount);
        return checkpoint with
        {
            Configuration = _configuration,
            DeclarationWeights = expandedWeights
        };
    }

}

public sealed record PolicyTrainingConfiguration(
    int CheckpointSchemaVersion,
    int ModelSchemaVersion,
    int FeatureCount,
    int BaseSeed,
    int TargetScore,
    int DecisionSeconds,
    double LearningRate)
{
    // Defaults let checkpoints written before declaration learning load with zeroed weights.
    public int DeclarationFeatureCount { get; init; } = DeclarationFeatureEncoder.FeatureCount;

    // Missing in older checkpoints; treat their pass model as a zero-weight policy.
    public int PassFeatureCount { get; init; } = PassFeatureEncoder.FeatureCount;

    // Older runs used the random baseline as their opponent.
    public string OpponentFingerprint { get; init; } = LinearPolicySnapshot.RandomBaselineFingerprint;
}

public sealed record LinearPolicyTrainingCheckpoint(
    PolicyTrainingConfiguration Configuration,
    long CompletedGames,
    int Wins,
    int Losses,
    int Draws,
    double AverageReward,
    double RewardBaseline,
    double[] Weights,
    DateTimeOffset UpdatedAt)
{
    // Missing in older checkpoints; the initializer supplies a safe all-zero policy.
    public double[] DeclarationWeights { get; init; } = new double[DeclarationFeatureEncoder.FeatureCount];

    // Missing in older checkpoints; the initializer supplies a safe all-zero policy.
    public double[] PassWeights { get; init; } = new double[PassFeatureEncoder.FeatureCount];
}
