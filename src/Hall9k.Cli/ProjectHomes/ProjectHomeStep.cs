namespace Hall9k.Cli.ProjectHomes;

/// <summary>What one step of the recipe did, in the words the command prints back.</summary>
public enum ProjectHomeOutcome
{
    /// <summary>The step made something that was not there before.</summary>
    Created,

    /// <summary>The step found its work already done and left it alone — the idempotence contract.</summary>
    AlreadyThere,

    /// <summary>The step could not run and said why. Not fatal on its own; the caller decides.</summary>
    Skipped,

    /// <summary>The step tried and failed. The recipe stops here.</summary>
    Failed,
}

/// <summary>
/// One line of the recipe's report. The recipe is deterministic platform code end to end (ruled
/// at the project-home discovery: "this is a recipe, not an agent"), so what it did is a list of
/// plain statements rather than a narrative anyone has to interpret.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Message">One line, already in past tense, naming the path it touched.</param>
public sealed record ProjectHomeStep(ProjectHomeOutcome Outcome, string Message)
{
    public static ProjectHomeStep Created(string message) => new(ProjectHomeOutcome.Created, message);

    public static ProjectHomeStep AlreadyThere(string message) => new(ProjectHomeOutcome.AlreadyThere, message);

    public static ProjectHomeStep Skipped(string message) => new(ProjectHomeOutcome.Skipped, message);

    public static ProjectHomeStep Failed(string message) => new(ProjectHomeOutcome.Failed, message);
}
