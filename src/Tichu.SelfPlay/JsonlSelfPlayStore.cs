using System.Text;
using System.Text.Json;

namespace Tichu.SelfPlay;

/// <summary>
/// Appends one complete match per JSON line. A partial final line from a power
/// loss is truncated on resume; the most recent complete match is preserved.
/// </summary>
public sealed class JsonlSelfPlayStore : IDisposable
{
    private const int StoreSchemaVersion = 2;
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly string _directory;
    private readonly string _dataPath;
    private readonly string _metadataPath;
    private readonly string _checkpointPath;
    private readonly FileStream _dataStream;
    private readonly HashSet<long> _completedGameIds;
    private bool _disposed;

    public JsonlSelfPlayStore(string directory, int baseSeed, int targetScore, int decisionSeconds = 120)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (targetScore <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetScore));
        }

        if (decisionSeconds <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(decisionSeconds));
        }

        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _dataPath = Path.Combine(_directory, "matches.jsonl");
        _metadataPath = Path.Combine(_directory, "run.json");
        _checkpointPath = Path.Combine(_directory, "checkpoint.json");

        EnsureMetadata(baseSeed, targetScore, decisionSeconds);
        _completedGameIds = RecoverCompletedGames(_dataPath);
        WriteCheckpoint();
        _dataStream = new FileStream(_dataPath, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public string DirectoryPath => _directory;

    public int CompletedCount => _completedGameIds.Count;

    public IReadOnlySet<long> CompletedGameIds => new HashSet<long>(_completedGameIds);

    public bool IsCompleted(long gameId) => _completedGameIds.Contains(gameId);

    public void Save(SelfPlayMatchRecord match)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(match);
        if (match.SchemaVersion != RandomSelfPlaySimulator.RecordSchemaVersion)
        {
            throw new ArgumentException("The match record schema is not supported by this store.", nameof(match));
        }

        if (_completedGameIds.Contains(match.GameId))
        {
            throw new InvalidOperationException($"Game {match.GameId} has already been saved.");
        }

        var json = JsonSerializer.SerializeToUtf8Bytes(match, JsonOptions);
        _dataStream.Write(json);
        _dataStream.WriteByte((byte)'\n');
        _dataStream.Flush(flushToDisk: true);
        _completedGameIds.Add(match.GameId);
        WriteCheckpoint();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _dataStream.Dispose();
        _disposed = true;
    }

    private void EnsureMetadata(int baseSeed, int targetScore, int decisionSeconds)
    {
        var expected = new RunMetadata(
            StoreSchemaVersion,
            RandomSelfPlaySimulator.RecordSchemaVersion,
            baseSeed,
            targetScore,
            decisionSeconds,
            "random-baseline-v1");
        if (File.Exists(_metadataPath))
        {
            var existing = JsonSerializer.Deserialize<RunMetadata>(File.ReadAllText(_metadataPath), JsonOptions);
            if (existing != expected)
            {
                throw new InvalidOperationException(
                    "This output directory belongs to a run with different seed, target score, or schema. Use a new directory for a new run.");
            }

            return;
        }

        WriteAtomic(_metadataPath, JsonSerializer.SerializeToUtf8Bytes(expected, JsonOptions));
    }

    private void WriteCheckpoint()
    {
        var lastCompleted = _completedGameIds.Count == 0 ? (long?)null : _completedGameIds.Max();
        var checkpoint = new RunCheckpoint(
            StoreSchemaVersion,
            _completedGameIds.Count,
            lastCompleted,
            DateTimeOffset.UtcNow);
        WriteAtomic(_checkpointPath, JsonSerializer.SerializeToUtf8Bytes(checkpoint, JsonOptions));
    }

    private static HashSet<long> RecoverCompletedGames(string path)
    {
        var completed = new HashSet<long>();
        using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
        using var line = new MemoryStream();
        var lastCompleteLineEnd = 0L;
        int nextByte;
        while ((nextByte = stream.ReadByte()) >= 0)
        {
            if (nextByte != '\n')
            {
                line.WriteByte((byte)nextByte);
                continue;
            }

            var bytes = line.ToArray();
            line.SetLength(0);
            if (bytes.Length == 0)
            {
                throw new InvalidDataException("The self-play journal contains an empty line.");
            }

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(bytes);
            }
            catch (JsonException) when (stream.Position == stream.Length)
            {
                stream.SetLength(lastCompleteLineEnd);
                stream.Flush(flushToDisk: true);
                return completed;
            }

            using (document)
            {
                var root = document.RootElement;
                var schema = root.GetProperty("SchemaVersion").GetInt32();
                if (schema != RandomSelfPlaySimulator.RecordSchemaVersion)
                {
                    throw new InvalidDataException($"Unsupported match record schema {schema} in the self-play journal.");
                }

                var gameId = root.GetProperty("GameId").GetInt64();
                if (gameId < 0 || !completed.Add(gameId))
                {
                    throw new InvalidDataException($"Invalid or duplicate game id {gameId} in the self-play journal.");
                }
            }

            lastCompleteLineEnd = stream.Position;
        }

        if (line.Length > 0)
        {
            stream.SetLength(lastCompleteLineEnd);
            stream.Flush(flushToDisk: true);
        }

        return completed;
    }

    private static void WriteAtomic(string destination, byte[] bytes)
    {
        var temporary = destination + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, destination, overwrite: true);
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new CompactCardJsonConverter());
        return options;
    }

    private sealed record RunMetadata(
        int StoreSchemaVersion,
        int RecordSchemaVersion,
        int BaseSeed,
        int TargetScore,
        int DecisionSeconds,
        string Policy);

    private sealed record RunCheckpoint(
        int StoreSchemaVersion,
        int CompletedGames,
        long? LastCompletedGameId,
        DateTimeOffset UpdatedAt);
}
