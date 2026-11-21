using System.Globalization;
using Tichu.Core;

namespace Tichu.SelfPlay;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }

            if (options.ReplayPath is { } replayPath &&
                options.ReplayGameId is { } replayGameId &&
                options.ReplayRoundIndex is { } replayRoundIndex)
            {
                Console.WriteLine(SelfPlayReplayTrace.FormatFromJournal(
                    replayPath,
                    replayGameId,
                    replayRoundIndex,
                    options.ReplaySeat));
                return 0;
            }

            if (options.TichuAnalysisInput is { } analysisInput)
            {
                var snapshot = CreateInitialAnalysisSnapshot();
                var roundContext = new PolicyRoundContext(
                    analysisInput.Seat,
                    analysisInput.OutgoingPass,
                    analysisInput.ReceivedFromPartner,
                    analysisInput.ReceivedFromRightOpponent,
                    analysisInput.ReceivedFromLeftOpponent);
                var analysis = TichuCallPlanAnalyzer.Analyze(
                    analysisInput.Seat,
                    analysisInput.Hand,
                    snapshot,
                    roundContext);
                Console.WriteLine(TichuCallPlanAnalysis.Format(analysis));
                return 0;
            }

            if (options.Train)
            {
                var frozenOpponent = options.OpponentOutputDirectory is null
                    ? null
                    : LinearPolicySnapshot.LoadFromDirectory(options.OpponentOutputDirectory);
                var checkpoint = new LinearPolicyTrainer().Train(
                    options.Games,
                    options.Seed,
                    options.TargetScore,
                    options.DecisionSeconds,
                    options.OutputDirectory,
                    options.LearningRate,
                    reportProgress: Console.WriteLine,
                    frozenOpponent: frozenOpponent);
                Console.WriteLine(
                    $"Done. {checkpoint.CompletedGames} training matches; " +
                    $"win rate {checkpoint.Wins / (double)Math.Max(1, checkpoint.CompletedGames):P1}; " +
                    $"checkpoint saved in {Path.GetFullPath(options.OutputDirectory)}.");
                return 0;
            }

            if (options.Evaluate)
            {
                var checkpointPath = Path.Combine(Path.GetFullPath(options.OutputDirectory), "policy-checkpoint.json");
                if (!File.Exists(checkpointPath))
                {
                    throw new FileNotFoundException("No trained policy checkpoint exists in the selected output directory.", checkpointPath);
                }

                var checkpoint = new PolicyTrainingStore(
                    options.OutputDirectory,
                    options.Seed,
                    options.TargetScore,
                    options.DecisionSeconds,
                    options.LearningRate).LoadOrCreate();
                var frozenOpponent = options.OpponentOutputDirectory is null
                    ? null
                    : LinearPolicySnapshot.LoadFromDirectory(options.OpponentOutputDirectory);
                var evaluationSeed = unchecked(options.Seed ^ 0x5F3759DF);
                var evaluationWeights = checkpoint.Weights.ToArray();
                if (options.PartnerCardPreserveBonus is { } partnerPreserveBonus)
                {
                    evaluationWeights[MoveFeatureEncoder.PreservesPartnerPassedCardIndex] = partnerPreserveBonus;
                }

                if (options.KingAceControlBonus is { } kingAceControlBonus)
                {
                    evaluationWeights[MoveFeatureEncoder.KingSingleSafeFromOpponentAcesIndex] = kingAceControlBonus;
                }

                var result = new LinearPolicyEvaluator().Evaluate(
                    options.Games,
                    evaluationSeed,
                    options.TargetScore,
                    options.DecisionSeconds,
                    evaluationWeights,
                    Console.WriteLine,
                    checkpoint.DeclarationWeights,
                    checkpoint.PassWeights,
                    frozenOpponent,
                    includeSignalMetrics: options.PartnerCardPreserveBonus is not null ||
                                          options.KingAceControlBonus is not null);
                Console.WriteLine(
                    $"Evaluation complete: {result.Wins} wins, {result.Losses} losses, {result.Draws} draws; " +
                    $"average team scores: policy {result.AverageCandidateScore:F1}, " +
                    $"opponent {result.AverageOpponentScore:F1}; " +
                    $"average reward {result.AverageReward:F3}; " +
                    $"partner-card preserve bonus {evaluationWeights[MoveFeatureEncoder.PreservesPartnerPassedCardIndex]:F2}, " +
                    $"King/Ace-control bonus {evaluationWeights[MoveFeatureEncoder.KingSingleSafeFromOpponentAcesIndex]:F2}.");
                if (options.PartnerCardPreserveBonus is not null || options.KingAceControlBonus is not null)
                {
                    Console.WriteLine(
                        $"Partner-passed Ace preserved on {result.PartnerPassedAcePreservedSingles}/{result.PartnerPassedAceSingleDecisions} single-Ace plays; " +
                        $"King played in {result.KingSinglesPlayedInAceControlPositions}/{result.KingSingleAceControlOpportunities} Ace-control opportunities.");
                }
                return 0;
            }

            var simulator = new RandomSelfPlaySimulator(
                options.TargetScore,
                decisionLimit: TimeSpan.FromSeconds(options.DecisionSeconds));
            using var store = new JsonlSelfPlayStore(
                options.OutputDirectory,
                options.Seed,
                options.TargetScore,
                options.DecisionSeconds);
            Console.WriteLine($"Output: {store.DirectoryPath}");
            Console.WriteLine($"Completed: {store.CompletedCount}; target: {options.Games}");

            for (long gameId = 0; gameId < options.Games; gameId++)
            {
                if (store.IsCompleted(gameId))
                {
                    continue;
                }

                var matchSeed = unchecked(options.Seed + (int)(gameId * 104_729L));
                var match = simulator.PlayMatch(gameId, matchSeed);
                store.Save(match);
                Console.WriteLine(
                    $"Saved {gameId + 1}/{options.Games}: {match.Winner}, " +
                    $"score {match.BottomTopScore}-{match.RightLeftScore}, " +
                    $"rounds {match.Rounds.Count}");
            }

            Console.WriteLine($"Done. {store.CompletedCount} matches are saved in {store.DirectoryPath}.");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 1;
        }
    }

    private static Options ParseArguments(string[] args)
    {
        var games = 10;
        var seed = 1;
        var targetScore = 1_000;
        var decisionSeconds = 120;
        var outputDirectory = Path.Combine(Environment.CurrentDirectory, "selfplay-output");
        string? opponentOutputDirectory = null;
        var showHelp = false;
        var train = false;
        var evaluate = false;
        var outputSpecified = false;
        var learningRate = LinearPolicyTrainer.DefaultLearningRate;
        double? partnerCardPreserveBonus = null;
        double? kingAceControlBonus = null;
        string? analysisHandText = null;
        string? replayPath = null;
        long? replayGameId = null;
        int? replayRoundIndex = null;
        PlayerSeat analysisSeat = PlayerSeat.Bottom;
        Card? passRight = null;
        Card? passPartner = null;
        Card? passLeft = null;
        Card? receivedPartner = null;
        Card? receivedRight = null;
        Card? receivedLeft = null;

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            if (argument is "--help" or "-h")
            {
                showHelp = true;
                continue;
            }

            if (argument == "--train")
            {
                train = true;
                continue;
            }

            if (argument == "--evaluate")
            {
                evaluate = true;
                continue;
            }

            if (index + 1 >= args.Length)
            {
                throw new ArgumentException($"Missing value for {argument}.");
            }

            var value = args[++index];
            switch (argument)
            {
                case "--games":
                    games = ParsePositiveInt(value, argument);
                    break;
                case "--seed":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seed))
                    {
                        throw new ArgumentException($"Invalid seed: {value}.");
                    }

                    break;
                case "--target-score":
                    targetScore = ParsePositiveInt(value, argument);
                    break;
                case "--decision-seconds":
                    decisionSeconds = ParsePositiveInt(value, argument);
                    break;
                case "--output":
                    outputDirectory = value;
                    outputSpecified = true;
                    break;
                case "--opponent-output":
                    opponentOutputDirectory = value;
                    break;
                case "--learning-rate":
                    if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedValue) ||
                        !double.IsFinite(parsedValue) || parsedValue <= 0.0)
                    {
                        throw new ArgumentException("--learning-rate must be a positive finite number.");
                    }

                    learningRate = parsedValue;
                    break;
                case "--partner-card-preserve-bonus":
                case "--partner-card-signal-prior":
                    partnerCardPreserveBonus = ParseFeatureBonus(value, argument);
                    break;
                case "--king-ace-control-bonus":
                case "--king-ace-control-prior":
                    kingAceControlBonus = ParseFeatureBonus(value, argument);
                    break;
                case "--analyze-tichu-hand":
                    analysisHandText = value;
                    break;
                case "--replay":
                    replayPath = value;
                    break;
                case "--game-id":
                    if (!long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedGameId) ||
                        parsedGameId < 0)
                    {
                        throw new ArgumentException("--game-id must be a non-negative integer.");
                    }

                    replayGameId = parsedGameId;
                    break;
                case "--round-index":
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedRoundIndex) ||
                        parsedRoundIndex < 0)
                    {
                        throw new ArgumentException("--round-index must be a non-negative integer.");
                    }

                    replayRoundIndex = parsedRoundIndex;
                    break;
                case "--seat":
                    if (!Enum.TryParse(value, ignoreCase: true, out analysisSeat) || !Enum.IsDefined(analysisSeat))
                    {
                        throw new ArgumentException("--seat must be Bottom, Right, Top, or Left.");
                    }

                    break;
                case "--pass-right":
                    passRight = ParseCard(value, argument);
                    break;
                case "--pass-partner":
                    passPartner = ParseCard(value, argument);
                    break;
                case "--pass-left":
                    passLeft = ParseCard(value, argument);
                    break;
                case "--received-from-partner":
                    receivedPartner = ParseCard(value, argument);
                    break;
                case "--received-from-right":
                    receivedRight = ParseCard(value, argument);
                    break;
                case "--received-from-left":
                    receivedLeft = ParseCard(value, argument);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {argument}.");
            }
        }

        if (train && evaluate)
        {
            throw new ArgumentException("Choose either --train or --evaluate, not both.");
        }

        if (replayPath is not null)
        {
            if (replayGameId is null || replayRoundIndex is null)
            {
                throw new ArgumentException("Replay requires --game-id and --round-index.");
            }

            if (train || evaluate || outputSpecified || opponentOutputDirectory is not null ||
                partnerCardPreserveBonus is not null || kingAceControlBonus is not null ||
                analysisHandText is not null || passRight is not null || passPartner is not null || passLeft is not null ||
                receivedPartner is not null || receivedRight is not null || receivedLeft is not null)
            {
                throw new ArgumentException("Replay cannot be combined with training, evaluation, or hand-analysis options.");
            }
        }
        else if (replayGameId is not null || replayRoundIndex is not null)
        {
            throw new ArgumentException("--game-id and --round-index require --replay.");
        }

        var hasPassAnalysisOptions = passRight is not null || passPartner is not null || passLeft is not null ||
                                    receivedPartner is not null || receivedRight is not null || receivedLeft is not null;
        TichuAnalysisInput? tichuAnalysisInput = null;
        if (analysisHandText is not null)
        {
            if (train || evaluate || opponentOutputDirectory is not null ||
                partnerCardPreserveBonus is not null || kingAceControlBonus is not null)
            {
                throw new ArgumentException("Tichu hand analysis cannot be combined with training or evaluation options.");
            }

            if (passRight is null || passPartner is null || passLeft is null)
            {
                throw new ArgumentException("Tichu hand analysis requires --pass-right, --pass-partner, and --pass-left.");
            }

            var hand = ParseCardList(analysisHandText, "--analyze-tichu-hand");
            if (hand.Length != 14 || hand.Distinct().Count() != hand.Length)
            {
                throw new ArgumentException("--analyze-tichu-hand must list fourteen distinct post-pass cards.");
            }

            var incoming = new[] { receivedPartner, receivedRight, receivedLeft }.OfType<Card>().ToArray();
            if (incoming.Any(card => !hand.Contains(card)) || incoming.Distinct().Count() != incoming.Length)
            {
                throw new ArgumentException("Received pass cards must be distinct cards in the supplied hand.");
            }

            tichuAnalysisInput = new TichuAnalysisInput(
                analysisSeat,
                hand,
                new CardPassSelection(passRight.Value, passPartner.Value, passLeft.Value),
                receivedPartner,
                receivedRight,
                receivedLeft);
        }
        else if (hasPassAnalysisOptions)
        {
            throw new ArgumentException("Pass options require --analyze-tichu-hand.");
        }

        if (opponentOutputDirectory is not null && !train && !evaluate)
        {
            throw new ArgumentException("--opponent-output can only be used with --train or --evaluate.");
        }

        if ((partnerCardPreserveBonus is not null || kingAceControlBonus is not null) && !evaluate)
        {
            throw new ArgumentException("Evaluation feature bonuses can only be used with --evaluate.");
        }

        if ((train || evaluate) && !outputSpecified)
        {
            outputDirectory = Path.Combine(Environment.CurrentDirectory, "training-output");
        }

        return new Options(
            games,
            seed,
            targetScore,
            decisionSeconds,
            outputDirectory,
            opponentOutputDirectory,
            showHelp,
            train,
            evaluate,
            learningRate,
            partnerCardPreserveBonus,
            kingAceControlBonus,
            tichuAnalysisInput,
            replayPath,
            replayGameId,
            replayRoundIndex,
            analysisSeat);
    }

    private static RoundSnapshot CreateInitialAnalysisSnapshot() => new(
        new TrickSnapshot(
            null,
            null,
            null,
            null,
            Array.Empty<TrickPlay>(),
            Enum.GetValues<PlayerSeat>(),
            Array.Empty<PlayerSeat>(),
            Array.Empty<PlayerSeat>(),
            CanCollect: false,
            IsDoubleVictory: false,
            IsRoundEnded: false),
        new[] { 14, 14, 14, 14 },
        new[] { 0, 0, 0, 0 })
    {
        PlayedCardsBySeat = Array.AsReadOnly(Enum.GetValues<PlayerSeat>()
            .Select(_ => (IReadOnlyList<Card>)Array.Empty<Card>())
            .ToArray())
    };

    private static Card[] ParseCardList(string value, string optionName)
    {
        var values = value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (values.Length == 0)
        {
            throw new ArgumentException($"{optionName} must contain cards separated by commas.");
        }

        return values.Select(card => ParseCard(card, optionName)).ToArray();
    }

    private static Card ParseCard(string value, string optionName)
    {
        var components = value.Split(':', StringSplitOptions.TrimEntries);
        if (components.Length is < 1 or > 2)
        {
            throw new ArgumentException($"Invalid card '{value}' for {optionName}. Use Rank or Rank:Suit.");
        }

        var rankText = components[0] switch
        {
            "2" => nameof(CardRank.Two),
            "3" => nameof(CardRank.Three),
            "4" => nameof(CardRank.Four),
            "5" => nameof(CardRank.Five),
            "6" => nameof(CardRank.Six),
            "7" => nameof(CardRank.Seven),
            "8" => nameof(CardRank.Eight),
            "9" => nameof(CardRank.Nine),
            "10" => nameof(CardRank.Ten),
            "J" => nameof(CardRank.Jack),
            "Q" => nameof(CardRank.Queen),
            "K" => nameof(CardRank.King),
            "A" => nameof(CardRank.Ace),
            _ => components[0]
        };
        if (!Enum.TryParse(rankText, ignoreCase: true, out CardRank rank) || !Enum.IsDefined(rank))
        {
            throw new ArgumentException($"Invalid card rank '{components[0]}' for {optionName}.");
        }

        if (rank is >= CardRank.Two and <= CardRank.Ace)
        {
            if (components.Length != 2 ||
                !Enum.TryParse(components[1], ignoreCase: true, out CardSuit suit) || !Enum.IsDefined(suit))
            {
                throw new ArgumentException($"Ordinary card '{value}' must include a valid suit for {optionName}.");
            }

            return new Card(rank, suit);
        }

        if (components.Length != 1)
        {
            throw new ArgumentException($"Special card '{value}' must not include a suit for {optionName}.");
        }

        return new Card(rank);
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }

        return result;
    }

    private static double ParseFeatureBonus(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            !double.IsFinite(result) || result is < -8.0 or > 8.0)
        {
            throw new ArgumentException($"{name} must be a finite bonus from -8 through 8.");
        }

        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Tichu self-play match generator");
        Console.WriteLine("Options: [--train | --evaluate] --games N --seed N --target-score N --decision-seconds N --output PATH [--learning-rate N] [--opponent-output PATH] [--partner-card-preserve-bonus N] [--king-ace-control-bonus N]");
        Console.WriteLine("--games is the total target for this output directory. Re-run with the same settings to resume.");
        Console.WriteLine("--train trains a linear policy against the random baseline, or against a fixed checkpoint when --opponent-output is set.");
        Console.WriteLine("--evaluate measures saved weights against the random baseline, or a fixed checkpoint when --opponent-output is set.");
        Console.WriteLine("--analyze-tichu-hand accepts fourteen post-pass cards (comma-separated) plus --pass-right, --pass-partner, and --pass-left; it prints sampled lead risks, candidate hand partitions, and paired order replays.");
        Console.WriteLine("--replay PATH --game-id N --round-index N [--seat Bottom|Right|Top|Left] prints a perspective-safe match replay from a journal file or its directory.");
        Console.WriteLine("Card notation uses names such as Dragon, Phoenix, King:Star, 10:Jade, or 2:Pagoda. Optional received-card flags are --received-from-partner, --received-from-right, and --received-from-left.");
        Console.WriteLine("Evaluation bonus options override the matching partner-card or King/Ace-control feature weight for an ablation run.");
        Console.WriteLine("The older --partner-card-signal-prior and --king-ace-control-prior option names remain accepted as aliases.");
    }

    private sealed record Options(
        int Games,
        int Seed,
        int TargetScore,
        int DecisionSeconds,
        string OutputDirectory,
        string? OpponentOutputDirectory,
        bool ShowHelp,
        bool Train,
        bool Evaluate,
        double LearningRate,
        double? PartnerCardPreserveBonus,
        double? KingAceControlBonus,
        TichuAnalysisInput? TichuAnalysisInput,
        string? ReplayPath,
        long? ReplayGameId,
        int? ReplayRoundIndex,
        PlayerSeat ReplaySeat);

    private sealed record TichuAnalysisInput(
        PlayerSeat Seat,
        Card[] Hand,
        CardPassSelection OutgoingPass,
        Card? ReceivedFromPartner,
        Card? ReceivedFromRightOpponent,
        Card? ReceivedFromLeftOpponent);
}
