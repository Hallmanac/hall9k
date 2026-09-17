using System.Text;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Captures what the code under test writes to <see cref="Console.Error"/> or
/// <see cref="Console.Out"/> <em>for this test alone</em>, so no other test's output can land in
/// the buffer this test then asserts on.
/// <para>
/// The raw pattern this replaces — save <see cref="Console.Error"/>, <see cref="Console.SetError"/>
/// a <see cref="StringWriter"/>, restore it in a <c>finally</c> — cannot do that, because
/// <see cref="Console"/>'s writers are process-wide while xUnit runs distinct collections in
/// parallel inside one process: every line any other test (or any fixture, or any background
/// thread) writes while the redirect is installed lands in the redirecting test's buffer too.
/// Origin incident (2026-09-17 03:25 EDT, run 01a0ad80):
/// <c>TrackerAssignmentTests.Assign_records_what_the_tracker_showed_when_the_gate_passes(github)</c>
/// asserted its captured stderr was empty and found
/// <c>PostgresFixture</c>'s cross-process container-gate wait notice in it, written by an
/// altogether different class that happened to be queued at that moment; the same tip passed the
/// suite when it ran alone. That notice no longer goes to the console at all
/// (<c>CrossProcessContainerGate</c>'s own trace source), but the shape it exposed is general, and
/// this is the fix for the shape.
/// </para>
/// <para>
/// The mechanism: the console writers are replaced exactly once per process, by this type's static
/// constructor, with a router that sends each write either to the capture buffer belonging to the
/// writing code's own async flow (an <see cref="AsyncLocal{T}"/>, set here and inherited by
/// everything the test awaits) or, when that flow owns no capture, straight through to the writer
/// the console had before. So a test that captures sees only what its own flow wrote, a test that
/// does not capture is unaffected, and the swap is never undone — nothing has to be restored,
/// which is what removes the window the raw pattern leaves open.
/// </para>
/// <para>
/// The one thing it cannot capture is a write from a flow that never inherited the scope: code the
/// test starts through a mechanism that suppresses <see cref="ExecutionContext"/> flow
/// (<c>ThreadPool.UnsafeQueueUserWorkItem</c>, a raw <see cref="Thread"/> with
/// <c>ExecutionContext.SuppressFlow</c>), or a process this test spawned. Such a write goes to the
/// real console instead of into the buffer, which errs toward an assertion that fails loudly
/// rather than toward cross-talk.
/// </para>
/// </summary>
internal sealed class ScopedConsoleCapture : IDisposable
{
    private static readonly AsyncLocal<CaptureBuffer?> ErrorSlot = new();
    private static readonly AsyncLocal<CaptureBuffer?> OutputSlot = new();

    // Captured before the routers below are installed, so an uncaptured write still reaches
    // exactly the writer it would have reached had this type never been touched. Read once, in a
    // static constructor: the routers stay installed for the life of the process, so there is no
    // second install to race and nothing to put back.
    private static readonly TextWriter PassThroughError;
    private static readonly TextWriter PassThroughOutput;

    static ScopedConsoleCapture()
    {
        PassThroughError = Console.Error;
        PassThroughOutput = Console.Out;
        Console.SetError(new RoutingWriter(ErrorSlot, PassThroughError));
        Console.SetOut(new RoutingWriter(OutputSlot, PassThroughOutput));
    }

    private readonly AsyncLocal<CaptureBuffer?> slot;
    private readonly CaptureBuffer? previous;
    private readonly CaptureBuffer buffer = new();

    private ScopedConsoleCapture(AsyncLocal<CaptureBuffer?> slot)
    {
        this.slot = slot;
        previous = slot.Value;
        slot.Value = buffer;
    }

    /// <summary>Captures this flow's writes to <see cref="Console.Error"/>.</summary>
    public static ScopedConsoleCapture StandardError() => new(ErrorSlot);

    /// <summary>Captures this flow's writes to <see cref="Console.Out"/>.</summary>
    public static ScopedConsoleCapture StandardOutput() => new(OutputSlot);

    /// <summary>Everything this flow has written to the captured stream so far.</summary>
    public string Text => buffer.ToString();

    public void Dispose() => slot.Value = previous;

    /// <summary>
    /// A <see cref="StringBuilder"/> behind a lock rather than a <see cref="StringWriter"/>: the
    /// code under test may well write from more than one thread of its own inside a single scope,
    /// and <see cref="StringWriter"/> is documented as thread-unsafe, so appends and the read in
    /// <see cref="ToString"/> are serialized here instead.
    /// </summary>
    private sealed class CaptureBuffer
    {
        private readonly StringBuilder builder = new();

        public void Append(char value)
        {
            lock (builder)
            {
                builder.Append(value);
            }
        }

        public void Append(string? value)
        {
            if (value is null)
            {
                return;
            }

            lock (builder)
            {
                builder.Append(value);
            }
        }

        public void Append(char[] value, int index, int count)
        {
            lock (builder)
            {
                builder.Append(value, index, count);
            }
        }

        public override string ToString()
        {
            lock (builder)
            {
                return builder.ToString();
            }
        }
    }

    /// <summary>
    /// The one writer that ever sits in <see cref="Console.Error"/>/<see cref="Console.Out"/> once
    /// this type has been used: it resolves its destination per write rather than holding one, so a
    /// scope opening or closing anywhere changes where the next write goes without any further
    /// swap of the console itself. Overrides the whole primitive write surface rather than relying
    /// on <see cref="TextWriter"/>'s own char-at-a-time fallbacks, so a captured string arrives as
    /// one append instead of hundreds under the buffer's lock.
    /// </summary>
    private sealed class RoutingWriter(AsyncLocal<CaptureBuffer?> slot, TextWriter passThrough) : TextWriter
    {
        public override Encoding Encoding => passThrough.Encoding;

        public override void Write(char value)
        {
            if (slot.Value is { } captured)
            {
                captured.Append(value);
            }
            else
            {
                passThrough.Write(value);
            }
        }

        public override void Write(string? value)
        {
            if (slot.Value is { } captured)
            {
                captured.Append(value);
            }
            else
            {
                passThrough.Write(value);
            }
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (slot.Value is { } captured)
            {
                captured.Append(buffer, index, count);
            }
            else
            {
                passThrough.Write(buffer, index, count);
            }
        }

        public override void WriteLine(string? value)
        {
            if (slot.Value is { } captured)
            {
                captured.Append(value);
                captured.Append(CoreNewLine, 0, CoreNewLine.Length);
            }
            else
            {
                passThrough.WriteLine(value);
            }
        }

        public override void Flush()
        {
            // A capture buffer holds no unflushed state of its own; only the pass-through can
            // have anything to push, and pushing it while a scope is open would be flushing on
            // behalf of a write this writer never forwarded there.
            if (slot.Value is null)
            {
                passThrough.Flush();
            }
        }
    }
}
