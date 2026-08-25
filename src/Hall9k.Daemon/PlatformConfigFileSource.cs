using System.Text.Json;
using Hall9k.Domain.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.FileProviders;

namespace Hall9k.Daemon;

/// <summary>
/// Wires the "hall9k" section of <see cref="Hall9kDatabase.ConfigFile"/> into the daemon's own
/// <see cref="IConfiguration"/> pipeline (backlog 59), so <see cref="DaemonOptions"/> —
/// concurrency, model-by-role, and any sibling member — binds from a durable per-machine setting
/// rather than only ever from the environment. That is what makes an autostart-launched daemon
/// (no operator shell to export anything into) run with the operator's own policy instead of
/// silently falling back to built-in defaults.
/// <para>
/// <c>Host.CreateApplicationBuilder</c> registers <em>two</em>
/// <see cref="EnvironmentVariablesConfigurationSource"/> instances by the time this runs: a
/// <c>DOTNET_</c>-prefixed host source added before <c>appsettings.json</c>, and the ordinary
/// unprefixed one added after it. The config file is inserted immediately ahead of the
/// <em>last</em> of the two — never the first, which would land the file ahead of
/// <c>appsettings.json</c> instead of ahead of the env source that actually carries an operator's
/// <c>Hall9k__</c> variables, and never appended after either source, which would make the file
/// outrank an environment variable. Either mistake inverts the documented precedence (env, then
/// config file, then <c>appsettings.json</c>/built-in default — the same order
/// <see cref="Hall9kDatabase"/> already uses for the connection string that lives in this same
/// file).
/// </para>
/// <para>
/// The file is parsed once, up front, purely to decide whether it is well-formed: letting
/// <c>Microsoft.Extensions.Configuration.Json</c> discover a syntax error itself would throw a raw
/// <see cref="FormatException"/> out of <c>builder.Build()</c>, which reads as an unrelated startup
/// crash rather than the config.json typo it actually is. A malformed file is reported to the
/// daemon's own log and skipped — environment variables and built-in defaults still apply —
/// rather than refused outright, because <c>h9k daemon status</c> and <c>h9k config show</c> run
/// the identical parse and name the same file, so the fix is one command away either way.
/// </para>
/// </summary>
public static class PlatformConfigFileSource
{
    public static void Insert(IConfigurationBuilder configuration)
    {
        if (!File.Exists(Hall9kDatabase.ConfigFile))
        {
            return;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Hall9kDatabase.ConfigFile));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                Console.Error.WriteLine(
                    $"The platform config file ({Hall9kDatabase.ConfigFile}) exists but its top level is not a "
                    + "JSON object — daemon operating settings from it are skipped this run; environment "
                    + "variables and built-in defaults still apply. Fix it (h9k config show explains the shape) "
                    + "and restart to pick it up.");
                return;
            }

            // JsonDocument accepts a key that repeats under a different case without complaint,
            // but JsonConfigurationFileParser's own keys are ordinal-ignore-case and throws a raw
            // FormatException on the collision — the exact unguarded startup crash this pre-parse
            // check exists to turn into a graceful skip.
            if (PlatformConfigFile.HasCaseInsensitiveDuplicateKeys(document.RootElement))
            {
                Console.Error.WriteLine(
                    $"The platform config file ({Hall9kDatabase.ConfigFile}) has a key that repeats under a "
                    + "different case (for example \"hall9k\" and \"Hall9k\") — daemon operating settings from "
                    + "it are skipped this run; environment variables and built-in defaults still apply. Fix it "
                    + "(h9k config show explains the shape) and restart to pick it up.");
                return;
            }
        }
        catch (JsonException exception)
        {
            Console.Error.WriteLine(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) is not valid JSON "
                + $"({exception.Message}) — daemon operating settings from it are skipped this run; "
                + "environment variables and built-in defaults still apply. Fix it (h9k config show "
                + "explains the shape) and restart to pick it up.");
            return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(
                $"The platform config file ({Hall9kDatabase.ConfigFile}) could not be read "
                + $"({exception.Message}) — daemon operating settings from it are skipped this run; "
                + "environment variables and built-in defaults still apply. Fix it (h9k config show "
                + "explains the shape) and restart to pick it up.");
            return;
        }

        int index = configuration.Sources.ToList()
            .FindLastIndex(source => source is EnvironmentVariablesConfigurationSource);
        if (index < 0)
        {
            index = configuration.Sources.Count;
        }

        configuration.Sources.Insert(index, new JsonConfigurationSource
        {
            FileProvider = new PhysicalFileProvider(Path.GetFullPath(Path.GetDirectoryName(Hall9kDatabase.ConfigFile)!)),
            Path = Path.GetFileName(Hall9kDatabase.ConfigFile),
            Optional = true,
            ReloadOnChange = false,
        });
    }
}
