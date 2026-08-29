using System.Text.Json;
using System.Text.Json.Serialization;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Domain.Features.Tasks;

/// <summary>
/// The card an agent or an operator composed: a work item type, the built-in and custom fields a
/// create or an update carries, and the text a comment carries. This is deliberately the only
/// thing composition is trusted with — the compose/execute split (Brian's design, 2026-08-28)
/// puts issue-type and field judgment with whoever knows the organisation's Jira configuration,
/// and keeps everything about whether the write is safe and whether it actually happened with the
/// executor.
/// <para>
/// Serialized whole onto <see cref="Events.JiraWriteRequested"/> before anything is sent to twg,
/// so the stream carries the exact payload a write was attempted with — the write-audit-only
/// scope Brian's design calls for: hall9k never mirrors a card's state, only what it did to it.
/// </para>
/// </summary>
public sealed record JiraWritePayload(
    string? WorkItemType,
    IReadOnlyDictionary<string, string>? Fields,
    string? Comment,
    string? ProjectKey = null,
    string? Format = null)
{
    /// <summary>
    /// Field names that move a card between workflow states rather than describing it. Refused
    /// wherever they appear in a composed payload's fields, regardless of the operation and
    /// regardless of who composed it — the same guardrail <see cref="JiraWriteOperation.Parse"/>
    /// applies to the operation name itself, applied to the one other place a transition could be
    /// smuggled in: an ordinary-looking field update.
    /// </summary>
    private static readonly string[] ForbiddenFieldKeys =
        ["status", "transition", "resolution", "resolutiondate"];

    /// <summary>The only values twg's own <c>--description-format</c>/<c>--body-format</c> accept.</summary>
    private static readonly string[] AllowedFormats = ["html", "markdown", "plain"];

    /// <summary>
    /// The format a composed description or comment is actually written in, told to twg
    /// explicitly rather than left to its own default of html: a payload that names none is
    /// assumed markdown, since a composing session's own card-authoring skills (this repo's
    /// story-authoring, for one) produce headings, bullets, and Given/When/Then blocks that render
    /// correctly as markdown and mangle as literal HTML source (independent pre-PR review, cycle
    /// 2). A caller writing genuinely plain text — closeout's own merge comment — names "plain"
    /// explicitly rather than relying on this default.
    /// </summary>
    [JsonIgnore]
    public string EffectiveFormat => Format.IsNotBlank() ? Format.Trim().ToLowerInvariant() : "markdown";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// Refuse a payload the executor will not carry out, whatever operation it is paired with.
    /// This is checked before anything reaches twg and before the intent is even recorded, so a
    /// disallowed field never makes it onto the stream disguised as a legitimate request.
    /// </summary>
    public void Validate(JiraWriteOperation operation)
    {
        if (operation == JiraWriteOperation.Comment)
        {
            if (Comment.IsBlank())
            {
                throw new DomainValidationException("A comment write needs comment text.");
            }
        }
        else if (operation == JiraWriteOperation.Create)
        {
            if (WorkItemType.IsBlank())
            {
                throw new DomainValidationException(
                    "A create write needs a work item type (for example \"Dev Task\") — hall9k models "
                    + "nothing about an organisation's Jira configuration, so this has to come from "
                    + "whoever composed the payload.");
            }
        }

        if (Format.IsNotBlank() && !AllowedFormats.Contains(Format.Trim().ToLowerInvariant()))
        {
            throw new DomainValidationException(
                $"\"{Format}\" is not a text format twg accepts for a description or a comment — use "
                + "\"markdown\", \"plain\", or \"html\" (or leave it out, which defaults to markdown).");
        }

        if (Fields is null)
        {
            return;
        }

        foreach (string key in Fields.Keys)
        {
            if (ForbiddenFieldKeys.Contains(key.Trim().ToLowerInvariant()))
            {
                throw new DomainValidationException(
                    $"The '{key}' field moves a card between workflow states, and the executor refuses "
                    + "a transition or a close through a field write regardless of who composed it: "
                    + "which state a card belongs in is this team's own workflow, done in Jira "
                    + "directly — never a write hall9k performs on anyone's behalf.");
            }
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// The payload a file named to a write surface actually carries — tolerant of a document that
    /// is missing fields the way <see cref="Hall9k.Connectors.WorkItems.JiraWorkItemProvider"/>'s
    /// own reads are tolerant of a Jira answer that is missing some: a composed file with no
    /// custom fields, or no comment, has none, rather than failing to parse.
    /// </summary>
    public static JiraWritePayload FromJson(string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(
                $"The composed payload is not valid JSON: {exception.Message}. It needs a JSON object "
                + "carrying (as needed) workItemType, fields (an object of field name to value), and "
                + "comment.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new DomainValidationException(
                    $"The composed payload is {document.RootElement.ValueKind}, not a JSON object. It "
                    + "needs an object carrying (as needed) workItemType, fields, and comment.");
            }

            string? workItemType = ReadString(document.RootElement, "workItemType");
            string? comment = ReadString(document.RootElement, "comment");
            string? projectKey = ReadString(document.RootElement, "projectKey");
            string? format = ReadString(document.RootElement, "format");
            Dictionary<string, string>? fields = null;
            if (document.RootElement.TryGetProperty("fields", out JsonElement fieldsElement)
                && fieldsElement.ValueKind == JsonValueKind.Object)
            {
                fields = [];
                foreach (JsonProperty property in fieldsElement.EnumerateObject())
                {
                    fields[property.Name] = property.Value.ValueKind switch
                    {
                        JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                        JsonValueKind.Number => property.Value.GetRawText(),
                        JsonValueKind.True or JsonValueKind.False => property.Value.GetRawText(),
                        _ => property.Value.GetRawText(),
                    };
                }
            }

            return new JiraWritePayload(workItemType, fields, comment, projectKey, format);
        }
    }

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
