using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// Distinguishes "not provided" from "explicitly set (possibly to null)" in update commands
/// and settings-changed events. Absent means "leave unchanged"; present means "apply this".
/// </summary>
[JsonConverter(typeof(OptionalJsonConverterFactory))]
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T? _value;

    public Optional(T? value)
    {
        HasValue = true;
        _value = value;
    }

    public bool HasValue { get; }

    public T? Value => _value;

    public static Optional<T> None => default;

    public static Optional<T> Of(T? value) => new(value);

    public static implicit operator Optional<T>(T? value) => new(value);

    public bool Equals(Optional<T> other) =>
        HasValue == other.HasValue && EqualityComparer<T?>.Default.Equals(_value, other._value);

    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    public override int GetHashCode() => HasValue ? _value?.GetHashCode() ?? 0 : -1;
}

public sealed class OptionalJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(
            typeof(OptionalJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;

    private sealed class OptionalJsonConverter<T> : JsonConverter<Optional<T>>
    {
        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw new JsonException($"Expected object for Optional<{typeof(T).Name}>.");
            }

            bool hasValue = false;
            T? value = default;

            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                string? propertyName = reader.GetString();
                reader.Read();
                switch (propertyName)
                {
                    case "hasValue":
                        hasValue = reader.GetBoolean();
                        break;
                    case "value":
                        value = JsonSerializer.Deserialize<T>(ref reader, options);
                        break;
                    default:
                        reader.Skip();
                        break;
                }
            }

            return hasValue ? new Optional<T>(value) : Optional<T>.None;
        }

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteBoolean("hasValue", value.HasValue);
            if (value.HasValue)
            {
                writer.WritePropertyName("value");
                JsonSerializer.Serialize(writer, value.Value, options);
            }

            writer.WriteEndObject();
        }
    }
}
