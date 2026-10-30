using System.Text.Json;
using Hall9k.Connectors.WorkItems;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// One Linear request as the double saw it: the GraphQL text, its variables, and the raw body, so a
/// test can pin both what a request asked for and the exact JSON that left the process.
/// </summary>
public sealed record RecordedLinearRequest(string Query, JsonElement Variables, string Body)
{
    public bool IsWrite => Query.Contains("issueUpdate", StringComparison.Ordinal);
}

/// <summary>
/// A recorded stand-in for one Linear issue's assignee: it answers the gate's assignee read and the
/// take's <c>issueUpdate</c> from state, so a read-back after a write sees the write (or, with
/// <see cref="WriteLands"/> off, honestly does not). The shapes are written from Linear's public
/// schema (<c>Issue.assignee</c>, <c>issueUpdate { success }</c>) and its documented error envelope.
/// No test using this reaches a live workspace.
/// </summary>
public sealed class RecordingLinearTracker
{
    public const string ViewerId = "11111111-aaaa-4bbb-8ccc-000000000001";
    public const string ViewerName = "Brian Hall";
    public const string OtherId = "22222222-aaaa-4bbb-8ccc-000000000002";
    public const string OtherName = "Someone Else";

    /// <summary>Who the issue's single assignee is right now; null is nobody.</summary>
    public string? AssigneeId { get; set; }

    public string? AssigneeName { get; set; }

    /// <summary>Whether an accepted <c>issueUpdate</c> changes the assignee; off means accepted but not applied.</summary>
    public bool WriteLands { get; set; } = true;

    /// <summary>When set, answers a request instead of the state machine; null falls through to it.</summary>
    public Func<RecordedLinearRequest, LinearResponse?>? Override { get; set; }

    public List<RecordedLinearRequest> Requests { get; } = [];

    public IEnumerable<RecordedLinearRequest> Reads => Requests.Where(request => !request.IsWrite);

    public IEnumerable<RecordedLinearRequest> Writes => Requests.Where(request => request.IsWrite);

    public static RecordingLinearTracker Unassigned() => new();

    public static RecordingLinearTracker HeldBy(string id, string name) => new() { AssigneeId = id, AssigneeName = name };

    public LinearRequester Requester => (request, _) =>
    {
        using JsonDocument body = JsonDocument.Parse(request.JsonBody);
        RecordedLinearRequest recorded = new(
            body.RootElement.GetProperty("query").GetString() ?? string.Empty,
            body.RootElement.TryGetProperty("variables", out JsonElement variables) ? variables.Clone() : default,
            request.JsonBody);
        Requests.Add(recorded);
        return Task.FromResult(Override?.Invoke(recorded) ?? Answer(recorded));
    };

    public static LinearResponse Json(string body, int status = 200) => new(status, body);

    public static LinearResponse Errors(string message, string? code = null, int status = 200) =>
        Json(
            code is null
                ? $$"""{"errors":[{"message":"{{message}}"}]}"""
                : $$$"""{"errors":[{"message":"{{{message}}}","extensions":{"code":"{{{code}}}"}}]}""",
            status);

    /// <summary>The assignee read's answer for an issue held by the given user, or by nobody when the id is null.</summary>
    public static LinearResponse IssueAssignedTo(string? id, string? name) =>
        Json(JsonSerializer.Serialize(new
        {
            data = new
            {
                issue = new
                {
                    identifier = "ENG-123",
                    assignee = id is null ? null : (object)new { id, name },
                },
            },
        }));

    private LinearResponse Answer(RecordedLinearRequest request)
    {
        if (!request.IsWrite)
        {
            return IssueAssignedTo(AssigneeId, AssigneeName);
        }

        if (WriteLands)
        {
            JsonElement assignee = request.Variables.GetProperty("assigneeId");
            AssigneeId = assignee.ValueKind == JsonValueKind.Null ? null : assignee.GetString();
            AssigneeName = AssigneeId == ViewerId ? ViewerName : AssigneeId is null ? null : "Granted User";
        }

        return Json("""{"data":{"issueUpdate":{"success":true}}}""");
    }
}
