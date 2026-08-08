using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiStocks.Persistence;

public static class CanonicalJson
{
    public static string Serialize(JsonElement value)
    {
        var builder = new StringBuilder();
        Write(builder, value);
        return builder.ToString();
    }

    public static string Sha256(JsonElement value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Serialize(value))));

    private static void Write(StringBuilder builder, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var firstProperty = true;
                foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!firstProperty) builder.Append(", ");
                    firstProperty = false;
                    builder.Append(JsonSerializer.Serialize(property.Name)).Append(": ");
                    Write(builder, property.Value);
                }
                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in value.EnumerateArray())
                {
                    if (!firstItem) builder.Append(", ");
                    firstItem = false;
                    Write(builder, item);
                }
                builder.Append(']');
                break;
            case JsonValueKind.String: builder.Append(JsonSerializer.Serialize(value.GetString())); break;
            case JsonValueKind.Number: builder.Append(value.GetRawText()); break;
            case JsonValueKind.True: builder.Append("true"); break;
            case JsonValueKind.False: builder.Append("false"); break;
            case JsonValueKind.Null: builder.Append("null"); break;
            default: throw new ArgumentException("Undefined JSON cannot be canonicalized.", nameof(value));
        }
    }
}
