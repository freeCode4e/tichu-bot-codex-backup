using System.Text.Json;
using System.Text.Json.Serialization;
using Tichu.Core;

namespace Tichu.SelfPlay;

/// <summary>Encodes each physical card as its 0-55 standard-deck index.</summary>
public sealed class CompactCardJsonConverter : JsonConverter<Card>
{
    private const byte MahJongId = 52;
    private const byte PhoenixId = 53;
    private const byte DragonId = 54;
    private const byte DogId = 55;

    public override Card Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number || !reader.TryGetByte(out var id) || id > DogId)
        {
            throw new JsonException("A compact Tichu card must be an integer from 0 through 55.");
        }

        if (id < MahJongId)
        {
            var suit = (CardSuit)(id / 13);
            var rank = (CardRank)(id % 13 + (int)CardRank.Two);
            return new Card(rank, suit);
        }

        return id switch
        {
            MahJongId => new Card(CardRank.MahJong),
            PhoenixId => new Card(CardRank.Phoenix),
            DragonId => new Card(CardRank.Dragon),
            DogId => new Card(CardRank.Dog),
            _ => throw new JsonException("Invalid compact Tichu card ID.")
        };
    }

    public override void Write(Utf8JsonWriter writer, Card value, JsonSerializerOptions options)
    {
        if (!value.IsValid)
        {
            throw new JsonException("Cannot encode an invalid Tichu card.");
        }

        var id = value.Rank switch
        {
            CardRank.MahJong => MahJongId,
            CardRank.Phoenix => PhoenixId,
            CardRank.Dragon => DragonId,
            CardRank.Dog => DogId,
            _ => checked((byte)((int)value.Suit!.Value * 13 + (int)value.Rank - (int)CardRank.Two))
        };
        writer.WriteNumberValue(id);
    }
}
