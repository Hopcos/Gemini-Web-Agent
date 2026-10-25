using System.Text.Json;
using System.Text.Json.Serialization;

namespace GeminiWebAgent.Models.OpenAI;

/// <summary>
/// OpenAI's "stop" is either a single string or an array of strings.
/// </summary>
public sealed class StringOrStringListConverter : JsonConverter<List<string>?>
{
    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                return [reader.GetString() ?? string.Empty];

            case JsonTokenType.StartArray:
                var list = new List<string>();
                while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                {
                    if (reader.TokenType == JsonTokenType.String)
                        list.Add(reader.GetString() ?? string.Empty);
                    else
                        reader.Skip();
                }
                return list;

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartArray();
        foreach (var item in value) writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}

/// <summary>
/// Tool call "arguments" is a JSON string per the OpenAI spec, but some clients send the object itself.
/// Both are read as the JSON text of the arguments.
/// </summary>
public sealed class JsonArgumentsConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "{}",
            JsonTokenType.Null => "{}",
            _ => JsonElement.ParseValue(ref reader).GetRawText()
        };
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value);
    }
}
