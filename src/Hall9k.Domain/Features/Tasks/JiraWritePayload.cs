using System.Text.Json;
using System.Text.Json.Nodes;
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
    public string EffectiveFormat => Format.IsNotBlank() ? Format.Trim().ToLowerInvariant() : "markdown";

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

            if (!HasField(Fields, "summary"))
            {
                throw new DomainValidationException(
                    "A create write needs a \"summary\" field — twg's own jira workitem create refuses "
                    + "without one, so this is refused here instead, before an intent is even recorded.");
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

    /// <summary>
    /// A composed field, matched case-insensitively the same way <see cref="Hall9k.Connectors.WorkItems.TwgJiraExecutor"/>
    /// itself pulls a first-class field out of the composed dictionary, with actual text in it.
    /// </summary>
    private static bool HasField(IReadOnlyDictionary<string, string>? fields, string name)
    {
        if (fields is null)
        {
            return false;
        }

        foreach ((string key, string value) in fields)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase) && value.IsNotBlank())
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Built by hand rather than through reflection so a field's value can be embedded as the
    /// genuine nested JSON node <see cref="FromJson"/> parsed it from (a quoted string stays a
    /// quoted string, a bare number stays a bare number) instead of being re-escaped as the
    /// contents of an outer JSON string — the shape that would otherwise double-quote every
    /// composed field on the very first round trip through this method and back through
    /// <see cref="FromJson"/> (the daemon's own retry after an expired twg login, for one).
    /// </summary>
    public string ToJson()
    {
        JsonObject root = [];
        if (WorkItemType is not null)
        {
            root["workItemType"] = WorkItemType;
        }

        if (Fields is not null)
        {
            JsonObject fields = [];
            foreach ((string name, string value) in Fields)
            {
                fields[name] = ParseFieldNode(value);
            }

            root["fields"] = fields;
        }

        if (Comment is not null)
        {
            root["comment"] = Comment;
        }

        if (ProjectKey is not null)
        {
            root["projectKey"] = ProjectKey;
        }

        if (Format is not null)
        {
            root["format"] = Format;
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// A field's stored text is itself valid JSON whenever it came from <see cref="FromJson"/>,
    /// which keeps the exact value a composer wrote rather than collapsing it to plain text — so
    /// it round-trips here as the same typed node. A caller that built a payload directly with
    /// plain, unquoted text (this file's own tests among them) is tolerated the way it always was,
    /// carried through as a bare string.
    /// </summary>
    private static JsonNode? ParseFieldNode(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (JsonException)
        {
            return JsonValue.Create(value);
        }
    }

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
                // Kept as the value's own raw JSON text — quotes and all for a string — rather
                // than unwrapped to plain text: twg's own --field parses its argument as JSON when
                // valid ("use quoted JSON strings to force string IDs" per its own help), so a
                // custom field composed as the JSON string "10501" has to reach AppendFields still
                // carrying its quotes, or twg parses it as the number 10501 instead of the select-
                // list option id it actually is (independent pre-PR review, cycle 6). A first-class
                // field (summary, description) is unwrapped back to plain text at the one place
                // that reads it for its own dedicated, non-JSON-coercing flag
                // (TwgJiraExecutor.ExtractField), not here.
                fields = [];
                foreach (JsonProperty property in fieldsElement.EnumerateObject())
                {
                    fields[property.Name] = property.Value.GetRawText();
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
