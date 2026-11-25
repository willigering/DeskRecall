using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using DeskRecall.Core.Geometry;

namespace DeskRecall.Core.Storage;

public static class Json
{
    public static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var o = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            PropertyNameCaseInsensitive = true,
            NumberHandling = JsonNumberHandling.AllowReadingFromString,
        };
        o.Converters.Add(new JsonStringEnumConverter());
        o.Converters.Add(new RectIConverter());
        return o;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    private sealed class RectIConverter : JsonConverter<RectI>
    {
        public override RectI Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.StartArray)
            {
                var v = new int[4]; var i = 0;
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    if (i < 4) v[i++] = reader.GetInt32();
                return RectI.FromSize(v[0], v[1], v[2], v[3]);
            }
            int l = 0, t = 0, w = 0, h = 0;
            if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Rechteck erwartet");
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var name = reader.GetString(); reader.Read();
                switch (name?.ToLowerInvariant())
                {
                    case "x": case "left": l = reader.GetInt32(); break;
                    case "y": case "top": t = reader.GetInt32(); break;
                    case "w": case "width": w = reader.GetInt32(); break;
                    case "h": case "height": h = reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }
            return RectI.FromSize(l, t, w, h);
        }

        public override void Write(Utf8JsonWriter writer, RectI value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("x", value.Left);
            writer.WriteNumber("y", value.Top);
            writer.WriteNumber("w", value.Width);
            writer.WriteNumber("h", value.Height);
            writer.WriteEndObject();
        }
    }
}
