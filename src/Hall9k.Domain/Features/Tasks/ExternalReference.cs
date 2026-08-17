using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// Link to the external work item this task adopted or published to.
/// Canonical form "&lt;provider&gt;:&lt;reference&gt;", e.g. "github:Hallmanac/hall9k#42".
/// </summary>
[JsonConverter(typeof(ExternalReferenceJsonConverter))]
public sealed record ExternalReference(WorkItemProvider Provider, string Reference)
{
    public static ExternalReference Parse(string? value)
    {
        if (value.IsBlank())
        {
            return new ExternalReference(WorkItemProvider.Unknown, string.Empty);
        }

        int separator = value.IndexOf(':');
        return separator < 0
            ? new ExternalReference(WorkItemProvider.Unknown, value)
            : new ExternalReference(value[..separator], value[(separator + 1)..]);
    }

    public override string ToString() => $"{Provider.Value}:{Reference}";

    private sealed class ExternalReferenceJsonConverter : JsonConverter<ExternalReference>
    {
        public override ExternalReference Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            Parse(reader.GetString());

        public override void Write(Utf8JsonWriter writer, ExternalReference value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString());
    }
}
