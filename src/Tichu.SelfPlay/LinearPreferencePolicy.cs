using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>
/// A small softmax policy trained with episodic policy gradients. It learns
/// declarations, card passing, card play, and out-of-turn bomb decisions;
/// Dragon recipients use the random baseline policy.
/// </summary>
public sealed class LinearPreferencePolicy : ITichuPolicy
{
    public const int ModelSchemaVersion = 1;
    private const double MaximumGradientNorm = 5.0;
    private const double MaximumAbsoluteWeight = 8.0;

    private readonly double[] _weights;
    private readonly double[] _declarationWeights;
    private readonly double[] _passWeights;
    private readonly List<PolicyDecision> _episode = new();
    private readonly Dictionary<PlayerSeat, PolicyRoundContext> _roundContexts = new();
    private Random _random = new(0);
    private RandomBaselinePolicy _fallback = new(new Random(0));
    private double _rewardBaseline;
    private readonly bool _recordEpisode;
    private const double DeclarationBaselinePriorLogit = 2.0;
    private const double BaselinePassPriorLogit = 4.0;
    // Tichu is reconsidered over multiple public states, so keep the baseline
    // exploration rate low when its hand-only heuristic says not to call.
    private const double ExploratoryTichuCallPriorLogit = -6.0;

    public LinearPreferencePolicy(
        IEnumerable<double>? weights = null,
        double rewardBaseline = 0.0,
        bool recordEpisode = true,
        IEnumerable<double>? declarationWeights = null,
        IEnumerable<double>? passWeights = null)
    {
        _weights = weights?.ToArray() ?? new double[MoveFeatureEncoder.FeatureCount];
        _declarationWeights = declarationWeights?.ToArray() ?? new double[DeclarationFeatureEncoder.FeatureCount];
        _passWeights = passWeights?.ToArray() ?? new double[PassFeatureEncoder.FeatureCount];
        if (_weights.Length != MoveFeatureEncoder.FeatureCount || _weights.Any(weight => !double.IsFinite(weight)))
        {
            throw new ArgumentException(
                $"The policy requires {MoveFeatureEncoder.FeatureCount} finite weights.",
                nameof(weights));
        }

        if (_declarationWeights.Length != DeclarationFeatureEncoder.FeatureCount ||
            _declarationWeights.Any(weight => !double.IsFinite(weight)))
        {
            throw new ArgumentException(
                $"The policy requires {DeclarationFeatureEncoder.FeatureCount} finite declaration weights.",
                nameof(declarationWeights));
        }

        if (_passWeights.Length != PassFeatureEncoder.FeatureCount ||
            _passWeights.Any(weight => !double.IsFinite(weight)))
        {
            throw new ArgumentException(
                $"The policy requires {PassFeatureEncoder.FeatureCount} finite pass weights.",
                nameof(passWeights));
        }

        if (!double.IsFinite(rewardBaseline))
        {
            throw new ArgumentOutOfRangeException(nameof(rewardBaseline));
        }

        _rewardBaseline = rewardBaseline;
        _recordEpisode = recordEpisode;
    }

    public IReadOnlyList<double> Weights => Array.AsReadOnly(_weights.ToArray());

    public IReadOnlyList<double> DeclarationWeights => Array.AsReadOnly(_declarationWeights.ToArray());

    public IReadOnlyList<double> PassWeights => Array.AsReadOnly(_passWeights.ToArray());

    public double RewardBaseline => _rewardBaseline;

    public int LastEpisodeDecisionCount { get; private set; }

    public void BeginEpisode(int seed)
    {
        if (_episode.Count > 0)
        {
            throw new InvalidOperationException("Complete the current policy episode before starting another.");
        }

        LastEpisodeDecisionCount = 0;
        _random = new Random(seed);
        _fallback = new RandomBaselinePolicy(new Random(unchecked(seed ^ 0x6D2B79F5)));
        _roundContexts.Clear();
    }

    public void BeginRound(PolicyRoundContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Seat.ToIndex();
        ArgumentNullException.ThrowIfNull(context.PassSelection);
        _roundContexts[context.Seat] = context;
    }

    public void CompleteEpisode(double reward, double learningRate)
    {
        if (!double.IsFinite(reward) || !double.IsFinite(learningRate) || learningRate <= 0.0)
        {
            throw new ArgumentOutOfRangeException(nameof(reward), "Reward must be finite and learning rate positive.");
        }

        reward = Math.Clamp(reward, -1.0, 1.0);
        LastEpisodeDecisionCount = _episode.Count;
        if (_episode.Count > 0)
        {
            var advantage = reward - _rewardBaseline;
            ApplyEpisodeGradient(PolicyParameterSet.Move, advantage, learningRate);
            ApplyEpisodeGradient(PolicyParameterSet.Declaration, advantage, learningRate);
            ApplyEpisodeGradient(PolicyParameterSet.Pass, advantage, learningRate);
        }

        _rewardBaseline += 0.05 * (reward - _rewardBaseline);
        _episode.Clear();
    }

    public bool ShouldCallGrand(IReadOnlyList<Card> hand) =>
        ChooseDeclaration(hand, isGrandDecision: true, _fallback.ShouldCallGrand(hand), context: null);

    public bool ShouldCallGrand(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ChooseDeclaration(hand, isGrandDecision: true, _fallback.ShouldCallGrand(hand), context);
    }

    public bool ShouldCallTichu(IReadOnlyList<Card> hand) =>
        ChooseDeclaration(hand, isGrandDecision: false, _fallback.ShouldCallTichu(hand), context: null);

    public bool ShouldCallTichu(IReadOnlyList<Card> hand, PolicyDeclarationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return ChooseDeclaration(hand, isGrandDecision: false, _fallback.ShouldCallTichu(hand), context);
    }

    public CardPassSelection ChoosePass(IReadOnlyList<Card> hand) => _fallback.ChoosePass(hand);

    public CardPassSelection ChoosePass(PlayerSeat seat, IReadOnlyList<Card> hand)
    {
        seat.ToIndex();
        ArgumentNullException.ThrowIfNull(hand);
        var baselineSelection = _fallback.ChoosePass(hand);
        var remaining = hand.ToList();

        var toPartner = ChoosePassCard(seat, remaining, seat.Partner(), baselineSelection.ToPartner);
        remaining.Remove(toPartner);
        var toRight = ChoosePassCard(seat, remaining, seat.ToTheRight(), baselineSelection.ToRight);
        remaining.Remove(toRight);
        var toLeft = ChoosePassCard(seat, remaining, seat.ToTheLeft(), baselineSelection.ToLeft);
        return new CardPassSelection(toRight, toPartner, toLeft);
    }

    public LegalMove ChooseMove(PlayerSeat seat, IReadOnlyList<Card> hand, RoundSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var legalMoves = MoveGenerator.Generate(hand, snapshot.Trick.CurrentTop, snapshot.Trick.PendingWish);
        if (legalMoves.Count == 0)
        {
            throw new InvalidOperationException($"No legal move is available for {seat}.");
        }

        return ChooseAndRecord(seat, hand, snapshot, legalMoves);
    }

    public LegalMove? ChooseOutOfTurnBomb(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        IReadOnlyList<LegalMove> bombs)
    {
        ArgumentNullException.ThrowIfNull(hand);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(bombs);
        if (bombs.Count == 0)
        {
            return null;
        }

        var choices = new LegalMove[bombs.Count + 1];
        for (var index = 0; index < bombs.Count; index++)
        {
            choices[index] = bombs[index];
        }

        // The pass-shaped choice represents declining this bomb opportunity.
        // Mark it as out-of-turn so the existing feature vector distinguishes
        // it from an ordinary trick pass without changing the model schema.
        choices[^1] = new LegalMove(null, IsOutOfTurnBomb: true);
        var chosen = ChooseAndRecord(seat, hand, snapshot, choices);
        return chosen.IsPass ? null : chosen;
    }

    public PlayerSeat ChooseDragonRecipient(PlayerSeat dragonWinner) =>
        _fallback.ChooseDragonRecipient(dragonWinner);

    private LegalMove ChooseAndRecord(
        PlayerSeat seat,
        IReadOnlyList<Card> hand,
        RoundSnapshot snapshot,
        IReadOnlyList<LegalMove> moves)
    {
        if (moves.Count == 1)
        {
            return moves[0];
        }

        var features = new double[moves.Count][];
        for (var actionIndex = 0; actionIndex < moves.Count; actionIndex++)
        {
            features[actionIndex] = MoveFeatureEncoder.Encode(
                seat,
                hand,
                snapshot,
                moves[actionIndex],
                _roundContexts.GetValueOrDefault(seat));
        }

        return moves[ChooseAndRecord(features, PolicyParameterSet.Move)];
    }

    private bool ChooseDeclaration(
        IReadOnlyList<Card> hand,
        bool isGrandDecision,
        bool baselineCall,
        PolicyDeclarationContext? context)
    {
        var callFeatures = DeclarationFeatureEncoder.Encode(hand, isGrandDecision, context);
        var choices = new[] { callFeatures, new double[DeclarationFeatureEncoder.FeatureCount] };
        var priorLogit = baselineCall
            ? DeclarationBaselinePriorLogit
            : isGrandDecision
                ? -DeclarationBaselinePriorLogit
                : ExploratoryTichuCallPriorLogit;
        return ChooseAndRecord(choices, PolicyParameterSet.Declaration, priorLogit) == 0;
    }

    private Card ChoosePassCard(
        PlayerSeat seat,
        IReadOnlyList<Card> remainingHand,
        PlayerSeat recipient,
        Card baselineCard)
    {
        var baselineCardAvailable = remainingHand.Contains(baselineCard);
        var candidates = baselineCardAvailable
            ? remainingHand.Where(card => card != baselineCard).Prepend(baselineCard).ToArray()
            : remainingHand.ToArray();
        var features = candidates
            .Select(card => PassFeatureEncoder.Encode(seat, remainingHand, card, recipient))
            .ToArray();
        var priorLogit = baselineCardAvailable ? BaselinePassPriorLogit : 0.0;
        return candidates[ChooseAndRecord(features, PolicyParameterSet.Pass, priorLogit)];
    }

    private int ChooseAndRecord(
        IReadOnlyList<double[]> features,
        PolicyParameterSet parameterSet,
        double callPriorLogit = 0.0)
    {
        var weights = GetWeights(parameterSet);
        var scores = new double[features.Count];
        for (var actionIndex = 0; actionIndex < features.Count; actionIndex++)
        {
            scores[actionIndex] = Dot(weights, features[actionIndex]);
        }

        scores[0] += callPriorLogit;
        var probabilities = Softmax(scores);
        var chosenIndex = Sample(probabilities);
        if (_recordEpisode)
        {
            _episode.Add(new PolicyDecision(features.ToArray(), probabilities, chosenIndex, parameterSet));
        }

        return chosenIndex;
    }

    private void ApplyEpisodeGradient(PolicyParameterSet parameterSet, double advantage, double learningRate)
    {
        var weights = GetWeights(parameterSet);
        var gradient = new double[weights.Length];
        foreach (var decision in _episode)
        {
            if (decision.ParameterSet != parameterSet)
            {
                continue;
            }

            for (var featureIndex = 0; featureIndex < gradient.Length; featureIndex++)
            {
                var expectedFeature = 0.0;
                for (var actionIndex = 0; actionIndex < decision.Features.Length; actionIndex++)
                {
                    expectedFeature += decision.Probabilities[actionIndex] *
                                       decision.Features[actionIndex][featureIndex];
                }

                gradient[featureIndex] += advantage *
                    (decision.Features[decision.ChosenIndex][featureIndex] - expectedFeature);
            }
        }

        var norm = Math.Sqrt(gradient.Sum(value => value * value));
        var scale = norm > MaximumGradientNorm ? MaximumGradientNorm / norm : 1.0;
        for (var index = 0; index < weights.Length; index++)
        {
            weights[index] = Math.Clamp(
                weights[index] + learningRate * gradient[index] * scale,
                -MaximumAbsoluteWeight,
                MaximumAbsoluteWeight);
        }
    }

    private int Sample(IReadOnlyList<double> probabilities)
    {
        var sample = _random.NextDouble();
        var cumulative = 0.0;
        for (var index = 0; index < probabilities.Count; index++)
        {
            cumulative += probabilities[index];
            if (sample < cumulative)
            {
                return index;
            }
        }

        return probabilities.Count - 1;
    }

    private static double[] Softmax(IReadOnlyList<double> scores)
    {
        var maximum = scores.Max();
        var probabilities = new double[scores.Count];
        var total = 0.0;
        for (var index = 0; index < scores.Count; index++)
        {
            probabilities[index] = Math.Exp(Math.Clamp(scores[index] - maximum, -40.0, 0.0));
            total += probabilities[index];
        }

        for (var index = 0; index < probabilities.Length; index++)
        {
            probabilities[index] /= total;
        }

        return probabilities;
    }

    private static double Dot(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        var result = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            result += left[index] * right[index];
        }

        return result;
    }

    private double[] GetWeights(PolicyParameterSet parameterSet) => parameterSet switch
    {
        PolicyParameterSet.Move => _weights,
        PolicyParameterSet.Declaration => _declarationWeights,
        PolicyParameterSet.Pass => _passWeights,
        _ => throw new ArgumentOutOfRangeException(nameof(parameterSet))
    };

    private enum PolicyParameterSet
    {
        Move,
        Declaration,
        Pass
    }

    private sealed record PolicyDecision(
        double[][] Features,
        double[] Probabilities,
        int ChosenIndex,
        PolicyParameterSet ParameterSet);
}
