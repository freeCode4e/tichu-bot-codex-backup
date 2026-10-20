using System.Globalization;

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
                var result = new LinearPolicyEvaluator().Evaluate(
                    options.Games,
                    evaluationSeed,
                    options.TargetScore,
                    options.DecisionSeconds,
                    checkpoint.Weights,
                    Console.WriteLine,
                    checkpoint.DeclarationWeights,
                    checkpoint.PassWeights,
                    frozenOpponent);
                Console.WriteLine(
                    $"Evaluation complete: {result.Wins} wins, {result.Losses} losses, {result.Draws} draws; " +
                    $"average team scores: policy {result.AverageCandidateScore:F1}, " +
                    $"opponent {result.AverageOpponentScore:F1}; " +
                    $"average reward {result.AverageReward:F3}.");
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
                default:
                    throw new ArgumentException($"Unknown option: {argument}.");
            }
        }

        if (train && evaluate)
        {
            throw new ArgumentException("Choose either --train or --evaluate, not both.");
        }

        if (opponentOutputDirectory is not null && !train && !evaluate)
        {
            throw new ArgumentException("--opponent-output can only be used with --train or --evaluate.");
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
            learningRate);
    }

    private static int ParsePositiveInt(string value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) || result <= 0)
        {
            throw new ArgumentException($"{name} must be a positive integer.");
        }

        return result;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("Tichu self-play match generator");
        Console.WriteLine("Options: [--train | --evaluate] --games N --seed N --target-score N --decision-seconds N --output PATH [--learning-rate N] [--opponent-output PATH]");
        Console.WriteLine("--games is the total target for this output directory. Re-run with the same settings to resume.");
        Console.WriteLine("--train trains a linear policy against the random baseline, or against a fixed checkpoint when --opponent-output is set.");
        Console.WriteLine("--evaluate measures saved weights against the random baseline, or a fixed checkpoint when --opponent-output is set.");
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
        double LearningRate);
}
