namespace Hall9k.Domain.Features.Project;

/// <summary>A named verification gate run in the worktree, e.g. ("test", "dotnet test").</summary>
public sealed record VerifyCommand(string Name, string Command);
