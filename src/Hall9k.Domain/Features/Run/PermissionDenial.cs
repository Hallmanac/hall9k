namespace Hall9k.Domain.Features.Run;

/// <summary>
/// One tool call Claude Code refused under a session's own real permission file (security review
/// idea 6be68ee2, process-injection finding 1) — never <c>--dangerously-skip-permissions</c>,
/// which voids the permission engine outright and so can never produce one of these. Under
/// <c>-p</c> with <c>defaultMode: dontAsk</c>, a disallowed tool call returns an error tool
/// result and the session carries on; its own terminal result line reports every denial it hit in
/// <c>permission_denials</c> rather than stalling (verified against Claude Code 2.1.283).
/// Recorded on the run so <c>h9k task show</c> and the findings report can name them — the same
/// surface that lets a project's allow list grow by evidence instead of by guess.
/// </summary>
/// <param name="ToolName">The tool Claude Code refused to run, exactly as the result line named it (e.g. <c>Bash</c>, <c>WebFetch</c>).</param>
/// <param name="ToolInput">The input the refused call carried, as Claude Code reported it — raw JSON text, not reparsed, since its shape depends on which tool was denied.</param>
public sealed record PermissionDenial(string ToolName, string ToolInput);
