using System.Text.Json;
using System.Text.Json.Serialization;

namespace GIMI_ModManager.WinUI.Models;

/// <summary>
/// A JsonConverterFactory that safely handles null JSON tokens for any value type
/// (Guid, int, DateTime, bool, double, etc.) by returning default(T) instead of throwing.
/// Uses an internal clean options instance to avoid infinite recursion.
/// </summary>
public class NullValueTypeConverter : JsonConverterFactory
{
    /// <summary>Options without this converter, used internally to avoid recursion.</summary>
    internal static readonly JsonSerializerOptions CleanOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsValueType;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(Inner<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }

    private class Inner<T> : JsonConverter<T> where T : struct
    {
        public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
                return default;
            // Use clean options (without this converter) to avoid infinite recursion
            return JsonSerializer.Deserialize<T>(ref reader, CleanOptions)!;
        }

        public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, value, CleanOptions);
    }
}
