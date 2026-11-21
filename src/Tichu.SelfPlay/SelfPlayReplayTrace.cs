using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Formats a match as a player's private information plus the public action history.</summary>
public static class SelfPlayReplayTrace
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static string FormatFromJournal(string journalPath, long gameId, int roundIndex, PlayerSeat perspective)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalPath);
        if (gameId < 0 || roundIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(gameId), "Game and round indexes must be non-negative.");
        }

        perspective.ToIndex();
        var fullPath = Path.GetFullPath(journalPath);
        var path = Directory.Exists(fullPath)
            ? Path.Combine(fullPath, "matches.jsonl")
            : fullPath;
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("The selected self-play match journal was not found.", path);
        }

        using var reader = new StreamReader(path);
        string? line;
        var lineNumber = 0;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            using var document = JsonDocument.Parse(line);
            if (ReadGameId(document.RootElement) != gameId)
            {
                continue;
            }

            var match = JsonSerializer.Deserialize<SelfPlayMatchRecord>(line, JsonOptions)
                        ?? throw new InvalidDataException($"Match record on line {lineNumber} is empty.");
            return Format(match, roundIndex, perspective);
        }

        throw new KeyNotFoundException($"Game {gameId} was not found in '{path}'.");
    }

    public static string Format(SelfPlayMatchRecord match, int roundIndex, PlayerSeat perspective)
    {
        ArgumentNullException.ThrowIfNull(match);
        perspective.ToIndex();
        if (roundIndex < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundIndex));
        }

        var round = match.Rounds.SingleOrDefault(candidate => candidate.RoundIndex == roundIndex)
                    ?? throw new ArgumentOutOfRangeException(nameof(roundIndex), $"Match {match.GameId} has no round {roundIndex}.");
        if (round.Passes.Count != 4)
        {
            throw new InvalidDataException("The selected round does not contain all four pass selections.");
        }

        var passes = round.Passes.ToDictionary(pass => pass.Seat);
        var deal = TichuDealer.FromDeckOrder(round.DeckOrder);
        var prepared = TichuRoundSetup.Prepare(
            deal,
            passes.ToDictionary(
                pair => pair.Key,
                pair => new CardPassSelection(pair.Value.ToRight, pair.Value.ToPartner, pair.Value.ToLeft)));
        var ownPasses = passes[perspective];
        var receivedFromPartner = passes[perspective.Partner()].ToPartner;
        var receivedFromRight = passes[perspective.ToTheRight()].ToLeft;
        var receivedFromLeft = passes[perspective.ToTheLeft()].ToRight;
        var ownInitialEight = deal.InitialEightHands[perspective];
        var output = new StringBuilder();

        output.AppendLine($"Match {match.GameId}, round {round.RoundIndex}, viewed from {perspective}");
        output.AppendLine(
            $"Match score before round: bottom/top {round.BottomTopScoreBefore}, " +
            $"right/left {round.RightLeftScoreBefore}; target {match.TargetScore}.");
        output.AppendLine($"Your 14 dealt cards before passing: {FormatCards(deal.FourteenCardHands[perspective])}");
        output.AppendLine(
            $"You passed: right {FormatCard(ownPasses.ToRight)}, partner {FormatCard(ownPasses.ToPartner)}, " +
            $"left {FormatCard(ownPasses.ToLeft)}.");
        output.AppendLine(
            $"You received: right {FormatCard(receivedFromRight)}, partner {FormatCard(receivedFromPartner)}, " +
            $"left {FormatCard(receivedFromLeft)}.");
        output.AppendLine($"Your post-pass hand: {FormatCards(prepared.Hands[perspective])}");
        output.AppendLine($"Mah Jong holder and opening leader: {prepared.OpeningSeat}.");
        output.AppendLine("Other players' hands and their outgoing passes are hidden.");

        var callsBeforeActions = round.Calls.Where(call => call.BeforeActionIndex is null).ToArray();
        if (callsBeforeActions.Length > 0)
        {
            output.AppendLine("Declarations before the exchange:");
            foreach (var call in callsBeforeActions)
            {
                AppendCall(output, call, perspective, ownInitialEight);
            }
        }

        output.AppendLine("Public action history:");
        output.AppendLine("Trick numbers follow the public history, so passes from earlier tricks remain visible above.");
        var tichuCalls = round.Calls
            .Where(call => call.BeforeActionIndex is not null)
            .GroupBy(call => call.BeforeActionIndex!.Value)
            .ToDictionary(group => group.Key, group => group.ToArray());
        var collectionsByAction = IndexCollectionsByFinalAction(round);
        var trickIndex = 0;
        for (var actionIndex = 0; actionIndex < round.Actions.Count; actionIndex++)
        {
            if (tichuCalls.TryGetValue(actionIndex, out var callsAtAction))
            {
                foreach (var call in callsAtAction)
                {
                    AppendCall(output, call, perspective, ownInitialEight);
                }
            }

            var action = round.Actions[actionIndex];
            if (action.PlaysOnTable.Count == 0)
            {
                output.AppendLine($"\nTrick {++trickIndex} begins");
            }

            output.AppendLine($"\nAction {actionIndex + 1}: {action.Seat} to act");
            AppendPublicState(output, action);
            if (action.Seat == perspective)
            {
                output.AppendLine($"  Your hand: {FormatCards(action.HandBefore)}");
            }
            else
            {
                output.AppendLine($"  {action.Seat}'s hand: hidden ({action.HandSizes[action.Seat.ToIndex()]} cards)");
            }

            if (action.IsPass)
            {
                output.AppendLine($"  Decision: {action.Seat} passes.");
            }
            else
            {
                var bombLabel = action.IsOutOfTurnBomb ? " out-of-turn bomb" : string.Empty;
                var combination = action.ChosenType?.ToString() ?? "play";
                output.AppendLine(
                    $"  Decision: {action.Seat} plays {FormatCards(action.PlayedCards)} as {combination}{bombLabel}." +
                    (action.WishForMahJong is { } wish ? $" Mah Jong wish: {wish}." : string.Empty));
            }

            if (collectionsByAction.TryGetValue(actionIndex, out var collection))
            {
                output.AppendLine(
                    $"  Trick collected by {collection.WinnerSeat}; points went to {collection.PointsRecipientSeat}; " +
                    $"cards: {FormatCards(collection.Cards)}; next lead: {collection.NextLeadSeat?.ToString() ?? "none"}.");
            }
        }

        output.AppendLine($"\nFinish order: {string.Join(" -> ", round.FinishOrder)}.");
        output.AppendLine(
            $"Round score: bottom/top {round.Score.BottomTopTotal} " +
            $"({round.Score.BottomTopCardPoints} card, {round.Score.BottomTopTichuPoints} Tichu); " +
            $"right/left {round.Score.RightLeftTotal} " +
            $"({round.Score.RightLeftCardPoints} card, {round.Score.RightLeftTichuPoints} Tichu).");
        output.AppendLine("Only your private hand and your pass cards are shown as private information.");
        return output.ToString().TrimEnd();
    }

    private static IReadOnlyDictionary<int, SelfPlayCollectionRecord> IndexCollectionsByFinalAction(
        SelfPlayRoundRecord round)
    {
        var result = new Dictionary<int, SelfPlayCollectionRecord>();
        foreach (var collection in round.Collections)
        {
            int? finalActionIndex = null;
            for (var actionIndex = 0; actionIndex < round.Actions.Count; actionIndex++)
            {
                var action = round.Actions[actionIndex];
                var afterAction = action.PlaysOnTable.ToList();
                if (!action.IsPass)
                {
                    afterAction.Add(new SelfPlayTrickPlayRecord(
                        action.Seat,
                        action.ChosenType ?? throw new InvalidDataException("A recorded play has no combination type."),
                        0m,
                        action.PlayedCards));
                }

                if (SamePlays(afterAction, collection.Plays))
                {
                    finalActionIndex = actionIndex;
                }
            }

            if (finalActionIndex is { } index)
            {
                result[index] = collection;
            }
        }

        return result;
    }

    private static bool SamePlays(
        IReadOnlyList<SelfPlayTrickPlayRecord> left,
        IReadOnlyList<SelfPlayTrickPlayRecord> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Seat != right[index].Seat ||
                left[index].Type != right[index].Type ||
                !left[index].Cards.SequenceEqual(right[index].Cards))
            {
                return false;
            }
        }

        return true;
    }

    private static void AppendCall(
        StringBuilder output,
        SelfPlayCallRecord call,
        PlayerSeat perspective,
        IReadOnlyList<Card> ownInitialEight)
    {
        output.Append($"  Public declaration: {call.Seat} called {call.Call}.");
        if (call.PendingWish is { } wish)
        {
            output.Append($" Pending wish: {wish}.");
        }

        if (call.Seat == perspective && call.HandBeforeCall is { } ownHand)
        {
            output.Append($" Your hand at the call: {FormatCards(ownHand)}.");
        }
        else if (call.Seat == perspective && call.Call == TichuCall.Grand)
        {
            output.Append($" Your first eight cards at the Grand call: {FormatCards(ownInitialEight)}.");
        }

        output.AppendLine();
    }

    private static void AppendPublicState(StringBuilder output, SelfPlayActionRecord action)
    {
        if (action.CurrentTopCards.Count == 0)
        {
            output.AppendLine("  Current top: none; this is a lead.");
        }
        else
        {
            var topOwner = action.PlaysOnTable.Count == 0
                ? "unknown"
                : action.PlaysOnTable[^1].Seat.ToString();
            output.AppendLine(
                $"  Current top: {FormatCards(action.CurrentTopCards)} ({action.CurrentTopType}, " +
                $"strength {action.CurrentTopStrength}) owned by {topOwner}.");
        }

        output.AppendLine(
            $"  Passed this trick: {FormatSeats(action.PassedSeats)}; " +
            $"active: {FormatSeats(action.ActiveSeats)}.");
        output.AppendLine(
            $"  Public hand sizes: {FormatSeatValues(action.HandSizes)}; " +
            $"points already collected: {FormatSeatValues(action.CollectedPointsBySeat)}.");
        if (action.PendingWish is { } wish)
        {
            output.AppendLine($"  Pending Mah Jong wish: {wish}.");
        }
    }

    private static string FormatSeats(IEnumerable<PlayerSeat> seats)
    {
        var values = seats.ToArray();
        return values.Length == 0 ? "none" : string.Join(", ", values);
    }

    private static string FormatSeatValues(IReadOnlyList<int> values) =>
        string.Join(", ", Enum.GetValues<PlayerSeat>().Select(seat => $"{seat} {values[seat.ToIndex()]}"));

    private static string FormatCards(IEnumerable<Card> cards) =>
        string.Join(" ", cards.OrderBy(CardSortKey).Select(FormatCard));

    private static string FormatCard(Card card) => card.IsOrdinary
        ? $"{FormatRank(card.Rank)}-{card.Suit}"
        : card.Rank.ToString();

    private static string FormatRank(CardRank rank) => rank switch
    {
        CardRank.MahJong => "MahJong",
        CardRank.Two => "2",
        CardRank.Three => "3",
        CardRank.Four => "4",
        CardRank.Five => "5",
        CardRank.Six => "6",
        CardRank.Seven => "7",
        CardRank.Eight => "8",
        CardRank.Nine => "9",
        CardRank.Ten => "10",
        CardRank.Jack => "J",
        CardRank.Queen => "Q",
        CardRank.King => "K",
        CardRank.Ace => "A",
        _ => rank.ToString()
    };

    private static int CardSortKey(Card card) => (int)card.Rank * 4 + (int)(card.Suit ?? CardSuit.Jade);

    private static long ReadGameId(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, nameof(SelfPlayMatchRecord.GameId), StringComparison.OrdinalIgnoreCase) &&
                property.Value.TryGetInt64(out var gameId))
            {
                return gameId;
            }
        }

        throw new InvalidDataException("A match journal line does not contain a valid game ID.");
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        options.Converters.Add(new ReplayCardJsonConverter());
        return options;
    }

    private sealed class ReplayCardJsonConverter : JsonConverter<Card>
    {
        private readonly CompactCardJsonConverter _compactConverter = new();

        public override Card Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                return _compactConverter.Read(ref reader, typeToConvert, options);
            }

            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException("A replay card must be a compact card ID or a legacy card object.");
            }

            CardRank? rank = null;
            CardSuit? suit = null;
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    break;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw new JsonException("Invalid legacy card object.");
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    throw new JsonException("Unexpected end of legacy card object.");
                }

                if (string.Equals(propertyName, nameof(Card.Rank), StringComparison.OrdinalIgnoreCase))
                {
                    if (!reader.TryGetByte(out var rankValue))
                    {
                        throw new JsonException("A legacy card rank must be an integer.");
                    }

                    rank = (CardRank)rankValue;
                }
                else if (string.Equals(propertyName, nameof(Card.Suit), StringComparison.OrdinalIgnoreCase))
                {
                    if (reader.TokenType == JsonTokenType.Null)
                    {
                        suit = null;
                    }
                    else if (reader.TryGetByte(out var suitValue))
                    {
                        suit = (CardSuit)suitValue;
                    }
                    else
                    {
                        throw new JsonException("A legacy card suit must be an integer or null.");
                    }
                }
                else
                {
                    reader.Skip();
                }
            }

            if (rank is null)
            {
                throw new JsonException("A legacy card object has no rank.");
            }

            try
            {
                return new Card(rank.Value, suit);
            }
            catch (ArgumentException exception)
            {
                throw new JsonException("Invalid legacy card object.", exception);
            }
        }

        public override void Write(Utf8JsonWriter writer, Card value, JsonSerializerOptions options) =>
            _compactConverter.Write(writer, value, options);
    }
}
