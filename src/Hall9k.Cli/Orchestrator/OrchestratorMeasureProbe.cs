using System.Diagnostics;
using System.Text.Json;
using Hall9k.Domain.Shared.Exceptions;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// <c>h9k orchestrator measure</c>'s one-turn probe (task: an operator starts a lean node or
/// project orchestrator window) — a fixed method, so a turn-one number recorded today is
/// comparable to one recorded next month, or on a different project. The probe reuses the same
/// launch-relevant flags a real recipe window starts with (<c>--strict-mcp-config</c>,
/// <c>--setting-sources project</c>, the scope's own <c>--settings</c> and
/// <c>--append-system-prompt-file</c>), swaps the interactive opening message for a fixed,
/// minimal print-mode prompt, and fixes the model to the cheapest tier so the probe itself costs
/// as little as the number it is measuring.
/// </summary>
public static class OrchestratorMeasureProbe
{
    /// <summary>The fixed prompt every measurement uses — minimal output, so the probe's own answer costs nothing worth counting.</summary>
    public const string ProbePrompt = "Reply with exactly one word: ready.";

    /// <summary>The fixed, cheapest model every measurement runs on, regardless of the recipe's own configured model — comparability, not the recipe's real cost.</summary>
    public const string ProbeModel = "haiku";

    private const int TimeoutSeconds = 120;

    /// <summary>
    /// Runs the probe against <paramref name="workingDirectory"/> using the anchor and settings
    /// files already generated there, and returns the observed turn-one input token total.
    /// </summary>
    public static async Task<int> RunAsync(
        string workingDirectory, string anchorRelativePath, string settingsRelativePath, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(workingDirectory))
        {
            // Checked up front rather than left to surface as a Win32Exception from Process.Start:
            // that failure looks identical to `claude` missing from PATH, and OrchestratorRecipeContext
            // hands this exact placeholder string ("<no home recorded yet - run h9k project init …>")
            // straight through as a working directory, so the honest diagnosis was one keystroke
            // away in the placeholder's own text and the probe still blamed PATH instead
            // (independent pre-PR review, cycle 3, adversarial lens).
            throw new DomainValidationException(
                $"The measurement probe's working directory does not exist: {workingDirectory}");
        }

        ProcessStartInfo startInfo = new("claude")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--strict-mcp-config");
        startInfo.ArgumentList.Add("--setting-sources");
        startInfo.ArgumentList.Add("project");
        startInfo.ArgumentList.Add("--settings");
        startInfo.ArgumentList.Add(settingsRelativePath);
        startInfo.ArgumentList.Add("--append-system-prompt-file");
        startInfo.ArgumentList.Add(anchorRelativePath);
        startInfo.ArgumentList.Add("--model");
        startInfo.ArgumentList.Add(ProbeModel);
        startInfo.ArgumentList.Add("--output-format");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add("-p");
        startInfo.ArgumentList.Add(ProbePrompt);

        using Process process = new() { StartInfo = startInfo };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

        string stdout;
        string stderr;
        try
        {
            process.Start();
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            Task<string> stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            stdout = await stdoutTask;
            stderr = await stderrTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The timeout cancels the reads and the wait, not the process itself: left alone, a
            // hung `claude` (a stalled auth prompt, a network stall) outlives this command and
            // holds its pipes open, and every retry leaves another one behind (independent pre-PR
            // review, cycle 1, conformance lens). Best-effort — the process may have exited on its
            // own between the timeout firing and this catch running.
            TryKill(process);
            throw new DomainValidationException(
                $"The measurement probe did not finish within {TimeoutSeconds}s. Is `claude` on PATH "
                + "and able to run non-interactively here?");
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new DomainValidationException(
                "Could not run `claude` for the measurement probe. It needs to be on PATH in this shell. "
                + $"({exception.Message})");
        }

        if (process.ExitCode != 0)
        {
            throw new DomainValidationException(
                $"The measurement probe exited {process.ExitCode}: {(stderr.Length > 0 ? stderr : stdout)}");
        }

        return ParseTurnOneTokens(stdout);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited between the check above and the kill itself — nothing left to do.
        }
    }

    /// <summary>
    /// Sums every input-side usage field <c>claude --output-format json</c> reports (fresh,
    /// cache-creation, and cache-read input tokens) — the same total the platform's own spend
    /// accounting already treats as one number. Never guesses: a response with no recognizable
    /// usage object is reported as exactly that rather than a fabricated count.
    /// </summary>
    public static int ParseTurnOneTokens(string claudeJsonOutput)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(claudeJsonOutput);
        }
        catch (JsonException exception)
        {
            throw new DomainValidationException(
                $"The measurement probe's output was not the JSON `--output-format json` promises: {exception.Message}");
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("usage", out JsonElement usage) || usage.ValueKind != JsonValueKind.Object)
            {
                throw new DomainValidationException(
                    "The measurement probe's output carried no 'usage' object to read a token count from.");
            }

            int total = 0;
            foreach (string field in (string[])["input_tokens", "cache_creation_input_tokens", "cache_read_input_tokens"])
            {
                if (usage.TryGetProperty(field, out JsonElement value) && value.TryGetInt32(out int count))
                {
                    total += count;
                }
            }

            return total;
        }
    }
}
