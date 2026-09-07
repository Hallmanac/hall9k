using System.Text.Json;
using System.Text.Json.Nodes;
using Hall9k.Cli.ProjectHomes;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// The generated settings file every orchestrator launch line rides on with <c>--settings</c>
/// (task: an operator starts a lean node or project orchestrator window). Command-line scope,
/// deliberately never a generated <c>.claude/settings.json</c>: <c>crossSessionInbound</c> is a
/// security exception Claude Code honors only when a project- or local-scope value is
/// <i>stricter</i> than user scope, and a recipe window's launch line already passes
/// <c>--setting-sources project</c>, which drops user scope (and with it any accept the operator
/// set there) entirely. Riding the policy in on the command line is the only scope a recipe
/// window actually reads it from.
/// <para>
/// Platform-owned and overwritten outright on every install, update, or project-home render, the
/// same as <see cref="LaunchAnchorDocument"/> and for the same reason: every field here is either
/// a fixed platform policy (<c>crossSessionInbound</c>, the bypass-confirmation suppression) or
/// resolved from settings the operator already sets elsewhere (the model chain); there is no
/// field here an operator would hand-edit this file itself to change.
/// </para>
/// </summary>
public static class RecipeSettingsDocument
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>
    /// Renders the settings document for <paramref name="model"/> — the effective model this
    /// window's sessions should run on, already resolved by the caller from the project or node
    /// chain (Decisions Log #33). Display and notification preferences are fixed platform
    /// defaults today; there is no CLI surface to override them yet.
    /// </summary>
    public static string Render(string model)
    {
        JsonObject document = new()
        {
            // Recipe windows read only command-line and project scope, never user scope
            // (--setting-sources project), so accepting a cross-session inbound message has to be
            // set here or it is silently held for approval by a window with nobody watching it.
            ["crossSessionInbound"] = "accept",
            // Pairs with --dangerously-skip-permissions on the launch line: it suppresses the
            // interactive confirmation that flag would otherwise prompt for, since a recipe
            // window is launched non-interactively from a pasted line.
            ["skipDangerousModePermissionPrompt"] = true,
            ["model"] = model,
            ["tui"] = "fullscreen",
            ["inputNeededNotifEnabled"] = true,
            ["agentPushNotifEnabled"] = true,
            ["voiceEnabled"] = true,
        };

        return document.ToJsonString(Options);
    }

    /// <summary>Writes the settings file at <paramref name="path"/>, overwriting whatever was there.</summary>
    public static void Write(string path, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        File.WriteAllText(path, Render(model));
    }

    /// <summary>The same write, reported as a <see cref="ProjectHomeStep"/> for the recipe's own report.</summary>
    public static ProjectHomeStep WriteStep(string path, string model)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        bool existed = File.Exists(path);
        string rendered = Render(model);
        if (existed && File.ReadAllText(path) == rendered)
        {
            return ProjectHomeStep.AlreadyThere($"settings.json already current at {path}");
        }

        File.WriteAllText(path, rendered);
        return ProjectHomeStep.Created(existed
            ? $"settings.json re-rendered at {path}"
            : $"settings.json rendered at {path}");
    }
}
