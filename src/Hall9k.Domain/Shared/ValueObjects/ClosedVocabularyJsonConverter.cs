using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hall9k.Domain.Shared.ValueObjects;

/// <summary>
/// The serialization every closed vocabulary here shares: a sealed record with static instances writes its
/// stored <c>Value</c> as a plain string and reads it back through its own mapping, so an unrecognized value reads as
/// the type's <c>Unknown</c> sentinel and never throws. Each vocabulary names its two directions and nothing else.
/// </summary>
/// <typeparam name="T">The vocabulary type.</typeparam>
public abstract class ClosedVocabularyJsonConverter<T> : JsonConverter<T>
    where T : class
{
    /// <summary>Maps a stored value onto its instance.</summary>
    protected abstract T FromStored(string? value);

    /// <summary>The value to store for an instance.</summary>
    protected abstract string ToStored(T value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        FromStored(reader.GetString());

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToStored(value));
}
