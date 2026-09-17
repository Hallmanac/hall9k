using Spectre.Console;
using Spectre.Console.Rendering;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Captures what the code under test renders through <see cref="AnsiConsole"/> <em>for this test
/// alone</em>, the same guarantee <see cref="ScopedConsoleCapture"/> gives the two
/// <see cref="Console"/> streams, and for the same reason: <see cref="AnsiConsole.Console"/> is one
/// process-wide static, so the save-swap-restore pattern nine classes in this project each carried
/// a private copy of let any two of them running in parallel collide.
/// <para>
/// Origin incident (2026-09-17 11:15 EDT, run 01a0af3c):
/// <c>ToolDoctorTests.An_unreachable_configured_database_reports_gh_as_unconfirmed_rather_than_silently_skipped</c>
/// failed with <c>StatusCommandMergedWithoutCopilotReviewTests</c>' "merged without Copilot review"
/// rows in the output it had captured for the doctor — one class swapped
/// <see cref="AnsiConsole.Console"/> while the other's swap was still installed, so the loser's
/// renders went to the winner's writer and its own assertions read someone else's terminal. Nothing
/// serialized them: the two sat in different xUnit collections, and no guard existed to notice.
/// </para>
/// <para>
/// The mechanism is <see cref="ScopedConsoleCapture"/>'s exactly: <see cref="AnsiConsole.Console"/>
/// is replaced once per process with a router that forwards every member to the console belonging
/// to the calling code's own async flow, or to the real console when that flow owns no capture. The
/// router forwards <see cref="IAnsiConsole.Profile"/> too, not just the writes, because
/// <c>LaunchLineWriter</c> reaches the writer through <c>Profile.Out.Writer</c> rather than through
/// <see cref="IAnsiConsole.Write"/>.
/// </para>
/// </summary>
internal sealed class ScopedAnsiConsoleCapture : IDisposable
{
    /// <summary>
    /// Wide enough that nothing a command prints wraps mid-phrase, so an assertion can match the
    /// sentence the code actually wrote rather than the fragment a terminal width happened to
    /// leave on one line. A caller whose subject <em>is</em> wrapping passes its own width.
    /// </summary>
    private const int UnwrappedWidth = 4096;

    private static readonly AsyncLocal<IAnsiConsole?> Current = new();

    // The real console, read before the router below is installed — see ScopedConsoleCapture's own
    // pass-through fields for why this is a static constructor's single read rather than a
    // per-scope save and restore.
    private static readonly IAnsiConsole PassThrough;

    static ScopedAnsiConsoleCapture()
    {
        PassThrough = AnsiConsole.Console;
        AnsiConsole.Console = new RoutingAnsiConsole();
    }

    private readonly StringWriter writer = new();
    private readonly IAnsiConsole? previous;

    private ScopedAnsiConsoleCapture(int width)
    {
        IAnsiConsole captured = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(writer),
        });

        captured.Profile.Width = width;

        previous = Current.Value;
        Current.Value = captured;
    }

    /// <summary>Opens a capture scope for the current async flow.</summary>
    public static ScopedAnsiConsoleCapture Begin(int width = UnwrappedWidth) => new(width);

    /// <summary>
    /// Everything this flow has rendered so far, with Spectre's markup already consumed — an
    /// assertion matches the rendered word ("git is not installed"), never the <c>[red]</c> tag
    /// that colored it, since the captured console emits neither color nor ANSI.
    /// </summary>
    public string Text => writer.ToString();

    public void Dispose() => Current.Value = previous;

    /// <summary>What every call site uses: run <paramref name="action"/> inside a scope and hand back what it rendered.</summary>
    public static async Task<string> CaptureAsync(Func<Task> action, int width = UnwrappedWidth)
    {
        using ScopedAnsiConsoleCapture capture = Begin(width);
        await action();
        return capture.Text;
    }

    /// <summary>The synchronous form, for a command whose entry point is not a task.</summary>
    public static string Capture(Action action, int width = UnwrappedWidth)
    {
        using ScopedAnsiConsoleCapture capture = Begin(width);
        action();
        return capture.Text;
    }

    /// <summary>
    /// The one console that ever sits in <see cref="AnsiConsole.Console"/> once this type has been
    /// used. Every member resolves its destination per call, so a scope opening or closing changes
    /// where the next render goes with no further swap of the static itself.
    /// </summary>
    private sealed class RoutingAnsiConsole : IAnsiConsole
    {
        private static IAnsiConsole Target => Current.Value ?? PassThrough;

        public Profile Profile => Target.Profile;

        public IAnsiConsoleCursor Cursor => Target.Cursor;

        public IAnsiConsoleInput Input => Target.Input;

        public IExclusivityMode ExclusivityMode => Target.ExclusivityMode;

        public RenderPipeline Pipeline => Target.Pipeline;

        public void Clear(bool home) => Target.Clear(home);

        public void Write(IRenderable renderable) => Target.Write(renderable);

        public void WriteAnsi(Action<AnsiWriter> write) => Target.WriteAnsi(write);
    }
}
