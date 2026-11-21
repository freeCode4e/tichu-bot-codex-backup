namespace Tichu.SelfPlay;

/// <summary>Expands older checkpoints with zeroed weights for newly added features.</summary>
internal static class PolicyCheckpointCompatibility
{
    private static readonly int[] PreviousDeclarationFeatureCounts = [23, 31, DeclarationFeatureEncoder.PreviousFeatureCount];

    public const int PreviousModelSchemaVersion = 2;
    public const int PreviousCurrentModelSchemaVersion = 3;
    public const int LegacyModelSchemaVersion = 1;

    public static LinearPolicyTrainingCheckpoint Upgrade(LinearPolicyTrainingCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        var configuration = checkpoint.Configuration;
        var weights = checkpoint.Weights;
        var declarationWeights = checkpoint.DeclarationWeights;

        var legacyMoveCheckpoint = configuration.ModelSchemaVersion == LegacyModelSchemaVersion &&
                                   configuration.FeatureCount == MoveFeatureEncoder.LegacyFeatureCount &&
                                   weights is { Length: MoveFeatureEncoder.LegacyFeatureCount };
        var previousMoveCheckpoint = configuration.ModelSchemaVersion == PreviousModelSchemaVersion &&
                                     configuration.FeatureCount == MoveFeatureEncoder.PreviousFeatureCount &&
                                     weights is { Length: MoveFeatureEncoder.PreviousFeatureCount };
        var previousCurrentMoveCheckpoint = configuration.ModelSchemaVersion == PreviousCurrentModelSchemaVersion &&
                                            configuration.FeatureCount == MoveFeatureEncoder.PreviousCurrentFeatureCount &&
                                            weights is { Length: MoveFeatureEncoder.PreviousCurrentFeatureCount };
        if (legacyMoveCheckpoint || previousMoveCheckpoint || previousCurrentMoveCheckpoint)
        {
            var expandedWeights = new double[MoveFeatureEncoder.FeatureCount];
            Array.Copy(weights!, expandedWeights, weights!.Length);
            weights = expandedWeights;
            configuration = configuration with
            {
                ModelSchemaVersion = LinearPreferencePolicy.ModelSchemaVersion,
                FeatureCount = MoveFeatureEncoder.FeatureCount
            };
        }

        var previousDeclarationFeatureCount = configuration.DeclarationFeatureCount;
        if (PreviousDeclarationFeatureCounts.Contains(previousDeclarationFeatureCount) &&
            declarationWeights is not null &&
            declarationWeights.Length == previousDeclarationFeatureCount)
        {
            var expandedDeclarationWeights = new double[DeclarationFeatureEncoder.FeatureCount];
            Array.Copy(declarationWeights, expandedDeclarationWeights, previousDeclarationFeatureCount);
            declarationWeights = expandedDeclarationWeights;
            configuration = configuration with
            {
                DeclarationFeatureCount = DeclarationFeatureEncoder.FeatureCount
            };
        }

        if (ReferenceEquals(weights, checkpoint.Weights) &&
            ReferenceEquals(declarationWeights, checkpoint.DeclarationWeights) &&
            configuration == checkpoint.Configuration)
        {
            return checkpoint;
        }

        return checkpoint with
        {
            Configuration = configuration,
            Weights = weights ?? Array.Empty<double>(),
            DeclarationWeights = declarationWeights ?? Array.Empty<double>()
        };
    }
}
