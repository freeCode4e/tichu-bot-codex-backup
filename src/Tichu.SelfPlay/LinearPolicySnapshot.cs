using System.Security.Cryptography;
using System.Text.Json;

namespace Tichu.SelfPlay;

/// <summary>A fixed, validated set of linear-policy weights for evaluation or self-play.</summary>
public sealed class LinearPolicySnapshot
{
    public const string RandomBaselineFingerprint = "random-baseline-v1";

    private readonly double[] _weights;
    private readonly double[] _declarationWeights;
    private readonly double[] _passWeights;

    public LinearPolicySnapshot(
        IEnumerable<double> weights,
        IEnumerable<double>? declarationWeights = null,
        IEnumerable<double>? passWeights = null,
        double rewardBaseline = 0.0)
        : this(weights, declarationWeights, passWeights, rewardBaseline, fingerprintOverride: null)
    {
    }

    private LinearPolicySnapshot(
        IEnumerable<double> weights,
        IEnumerable<double>? declarationWeights,
        IEnumerable<double>? passWeights,
        double rewardBaseline,
        string? fingerprintOverride)
    {
        ArgumentNullException.ThrowIfNull(weights);
        _weights = weights.ToArray();
        _declarationWeights = declarationWeights?.ToArray() ?? new double[DeclarationFeatureEncoder.FeatureCount];
        _passWeights = passWeights?.ToArray() ?? new double[PassFeatureEncoder.FeatureCount];
        if (_weights.Length != MoveFeatureEncoder.FeatureCount || _weights.Any(weight => !double.IsFinite(weight)) ||
            _declarationWeights.Length != DeclarationFeatureEncoder.FeatureCount ||
            _declarationWeights.Any(weight => !double.IsFinite(weight)) ||
            _passWeights.Length != PassFeatureEncoder.FeatureCount ||
            _passWeights.Any(weight => !double.IsFinite(weight)) ||
            !double.IsFinite(rewardBaseline))
        {
            throw new ArgumentException("The policy snapshot contains incompatible or non-finite weights.", nameof(weights));
        }

        RewardBaseline = rewardBaseline;
        Fingerprint = fingerprintOverride ?? ComputeFingerprint(
            LinearPreferencePolicy.ModelSchemaVersion,
            _weights,
            _declarationWeights,
            _passWeights);
    }

    public IReadOnlyList<double> Weights => Array.AsReadOnly(_weights.ToArray());

    public IReadOnlyList<double> DeclarationWeights => Array.AsReadOnly(_declarationWeights.ToArray());

    public IReadOnlyList<double> PassWeights => Array.AsReadOnly(_passWeights.ToArray());

    public double RewardBaseline { get; }

    public string Fingerprint { get; }

    public LinearPreferencePolicy CreatePolicy() => new(
        _weights,
        RewardBaseline,
        recordEpisode: false,
        declarationWeights: _declarationWeights,
        passWeights: _passWeights);

    public static LinearPolicySnapshot LoadFromDirectory(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var checkpointPath = Path.Combine(Path.GetFullPath(directory), "policy-checkpoint.json");
        if (!File.Exists(checkpointPath))
        {
            throw new FileNotFoundException("No policy checkpoint exists in the selected directory.", checkpointPath);
        }

        var checkpoint = JsonSerializer.Deserialize<LinearPolicyTrainingCheckpoint>(File.ReadAllText(checkpointPath))
                         ?? throw new InvalidDataException("The policy checkpoint is empty or invalid.");
        return FromCheckpoint(checkpoint);
    }

    public static LinearPolicySnapshot FromCheckpoint(LinearPolicyTrainingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var legacyFingerprint = (
                                   checkpoint.Configuration.ModelSchemaVersion ==
                                   PolicyCheckpointCompatibility.LegacyModelSchemaVersion &&
                                   checkpoint.Configuration.FeatureCount == MoveFeatureEncoder.LegacyFeatureCount &&
                                   checkpoint.Weights is { Length: MoveFeatureEncoder.LegacyFeatureCount }) ||
                               (checkpoint.Configuration.ModelSchemaVersion ==
                                PolicyCheckpointCompatibility.PreviousModelSchemaVersion &&
                                checkpoint.Configuration.FeatureCount == MoveFeatureEncoder.PreviousFeatureCount &&
                                checkpoint.Weights is { Length: MoveFeatureEncoder.PreviousFeatureCount }) ||
                               (checkpoint.Configuration.ModelSchemaVersion ==
                                PolicyCheckpointCompatibility.PreviousCurrentModelSchemaVersion &&
                                checkpoint.Configuration.FeatureCount == MoveFeatureEncoder.PreviousCurrentFeatureCount &&
                                checkpoint.Weights is { Length: MoveFeatureEncoder.PreviousCurrentFeatureCount })
            ? ComputeFingerprint(
                checkpoint.Configuration.ModelSchemaVersion,
                checkpoint.Weights,
                checkpoint.DeclarationWeights ?? Array.Empty<double>(),
                checkpoint.PassWeights ?? Array.Empty<double>())
            : null;
        checkpoint = PolicyCheckpointCompatibility.Upgrade(checkpoint);
        return new LinearPolicySnapshot(
            checkpoint.Weights,
            checkpoint.DeclarationWeights,
            checkpoint.PassWeights,
            checkpoint.RewardBaseline,
            legacyFingerprint);
    }

    private static string ComputeFingerprint(
        int modelSchemaVersion,
        double[] weights,
        double[] declarationWeights,
        double[] passWeights)
    {
        var fingerprintBytes = JsonSerializer.SerializeToUtf8Bytes(new FingerprintPayload(
            modelSchemaVersion,
            weights,
            declarationWeights,
            passWeights));
        return Convert.ToHexString(SHA256.HashData(fingerprintBytes));
    }

    private sealed record FingerprintPayload(
        int ModelSchemaVersion,
        double[] Weights,
        double[] DeclarationWeights,
        double[] PassWeights);
}
