using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Hall9k.Connectors.Processes;

/// <summary>What a command-line tool actually reported, kept whole so the caller can read it.</summary>
public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

/// <summary>
/// The tool itself exited, but something it started was still holding its output pipe open when
/// <see cref="ExternalProcess.DrainGrace"/> expired — distinct from the plain
/// <see cref="TimeoutException"/> thrown when the tool never answered at all, because here an
/// exit code was actually observed. A caller for whom the call being timed can have a real,
/// externally-visible effect (creating something, not just reading it) can use
/// <see cref="ExitCode"/> to tell a genuine success (0), whose result was merely never read, from
/// a genuine failure.
/// </summary>
public sealed class ProcessOutputStuckException(int exitCode, string message) : TimeoutException(message)
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// How a connector reaches a command-line tool. It is a delegate rather than an interface
/// because a connector needs exactly one verb from the operating system, and a seam this small
/// is what lets the provider tests assert mapping and refusal behaviour against recorded tool
/// output instead of against a live account.
/// </summary>
public delegate Task<ProcessResult> ProcessRunner(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    CancellationToken cancellationToken);

public static class ExternalProcess
{
    /// <summary>The real one: spawn the tool and read both streams to exhaustion, giving it
    /// <see cref="Deadline"/> to answer.</summary>
    public static readonly ProcessRunner Runner = (fileName, arguments, workingDirectory, cancellationToken) =>
        RunAsync(fileName, arguments, workingDirectory, Deadline, cancellationToken);

    /// <summary>
    /// A <see cref="ProcessRunner"/> bound to a caller-supplied deadline instead of
    /// <see cref="Deadline"/>, for the rare tool call that is not the short metadata read
    /// <see cref="Deadline"/> was sized for — a multi-tens-of-megabyte release archive
    /// download, for one, which a two-minute limit kills mid-transfer on an ordinary
    /// connection.
    /// </summary>
    public static ProcessRunner RunnerWithDeadline(TimeSpan deadline) =>
        (fileName, arguments, workingDirectory, cancellationToken) =>
            RunAsync(fileName, arguments, workingDirectory, deadline, cancellationToken);

    /// <summary>
    /// How long a tool gets before Hall9k stops waiting for it. The caller's token is not enough
    /// on its own: it carries a human pressing Ctrl-C, and the runs that matter here have no
    /// human at the keyboard — an import driven by the daemon, or by a script in CI, waits on a
    /// wedged tool for as long as that tool feels like being wedged, and gh has real ways to
    /// wedge (a credential helper prompting a terminal that is not there, a network call with no
    /// timeout of its own).
    /// <para>
    /// Deliberately generous rather than tight. Reading one issue takes well under a second, but
    /// a first call can pay for an interactive-looking auth refresh, and a deadline that fires on
    /// a slow-but-working import would turn a working setup into an unexplained failure. Two
    /// minutes is far past any healthy call and far short of hanging a queue.
    /// </para>
    /// </summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(120);

    /// <summary>
    /// How long the output is still worth waiting for once the tool itself has exited. Exiting
    /// and finishing are not the same event when the output is a pipe: the write end is inherited
    /// by everything the tool started, so a credential helper or a pager that outlives gh holds
    /// the pipe open and the read never sees end-of-file, however long ago gh answered.
    /// <para>
    /// Short, because by this point the tool has already said everything it had to say and the
    /// only question left is whether the operating system will let go of the pipe. Waiting out
    /// <see cref="Deadline"/> here would spend two minutes on a call that took half a second and
    /// then report it as a tool that never answered, which is the opposite of what happened.
    /// </para>
    /// </summary>
    public static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How the tool is spawned. It is named rather than inlined because one line of it is a
    /// decision rather than boilerplate: redirecting a stream without naming its encoding decodes
    /// the child's bytes with the console's own, which on Windows is the OEM code page while gh
    /// emits UTF-8 whatever the console is set to. An issue title or body carrying anything
    /// outside ASCII would arrive as mojibake and be stored that way, so the verbatim promise the
    /// import makes would be broken at the moment of import, permanently, on a platform CI builds
    /// for — and nothing downstream could tell the mangling from the source's own words.
    /// <para>
    /// The arguments are added one at a time rather than joined into a command line, and the
    /// shell is left out of it, so a reference a human typed reaches gh as one argument whatever
    /// quoting or spaces it carries.
    /// </para>
    /// </summary>
    internal static ProcessStartInfo StartInfoFor(
        string fileName, IReadOnlyList<string> arguments, string workingDirectory)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<ProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        TimeSpan deadline,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadlineSource = new(deadline);
        using CancellationTokenSource attempt =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineSource.Token);

        using Process process = new();
        process.StartInfo = StartInfoFor(fileName, arguments, workingDirectory);
        process.Start();

        // The reads get their own source, and it is linked to the caller's token alone rather
        // than to the attempt. Two things follow from that. A Ctrl-C still stops them, because
        // that token is in it. And the deadline does not, which is what gives the drain grace
        // below its full duration measured from the exit: a tool that answers in the deadline's
        // last second is owed the same seconds for its pipe as one that answers in the first, and
        // letting the deadline cut the drain short would report a tool that answered fine as one
        // that never answered at all. The reads cannot simply be awaited after the exit either:
        // the pipes are inherited by whatever the tool started, so their end-of-file can be
        // arbitrarily later than it. The finally below closes them on every path that leaves
        // without them, since nothing else would.
        using CancellationTokenSource reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(reading.Token);
            Task<string> standardError = process.StandardError.ReadToEndAsync(reading.Token);
            await process.WaitForExitAsync(attempt.Token);
            reading.CancelAfter(DrainGrace);

            return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
        }
        // The tool answered and something it started is still holding the answer's pipe open. It
        // is checked before the deadline below because it is a different fact about a different
        // process, and reporting it as "gh did not answer" would send a human to look at gh, which
        // by then has been gone for as long as the grace period lasted. The condition is exact
        // rather than approximate: this source is cancelled by the caller, which the first clause
        // excludes, or by the grace timer, which is only ever started once the exit has been
        // observed — so arriving here is proof the tool exited, whatever the deadline was doing.
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && reading.IsCancellationRequested)
        {
            int exitCode = process.ExitCode;
            await TerminateAsync(process);
            throw new ProcessOutputStuckException(
                exitCode,
                $"{fileName} exited with code {exitCode.ToString(CultureInfo.InvariantCulture)}, but "
                + $"{DrainGrace.TotalSeconds.ToString("F0", CultureInfo.InvariantCulture)} seconds later "
                + "something it started was still holding its output open, so Hall9k never received the "
                + "answer. Hall9k asked the operating system to end the tool's process tree; on Linux "
                + "and macOS that ends nothing once the tool itself has exited, because a surviving "
                + "child has already been reparented away from it, so the process holding the pipe may "
                + "still be running.");
        }
        // The deadline and the caller's cancellation arrive as the same exception type through
        // the linked source, and they are not the same event: one is Hall9k giving up on a tool
        // that stopped answering, which a caller has to be able to explain to a human, and the
        // other is the human who asked for the stop already knowing. The caller's token is
        // checked first so a Ctrl-C during the last moments of the deadline still reads as a
        // Ctrl-C.
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested && deadlineSource.IsCancellationRequested)
        {
            await TerminateAsync(process);
            throw new TimeoutException(string.Create(
                CultureInfo.InvariantCulture,
                $"{fileName} did not answer within {deadline.TotalSeconds:F0} seconds, so Hall9k stopped waiting and ended it."));
        }
        catch
        {
            await TerminateAsync(process);
            throw;
        }
        finally
        {
            // On the way out of any failure the reads are still outstanding and nothing else
            // would ever end them: disposing this source does not cancel it, and the pipes are
            // held by a process Hall9k has stopped waiting for. On the way out of the success
            // path both have already completed, so this is a no-op there.
            await reading.CancelAsync();
        }
    }

    /// <summary>
    /// Cancelling the wait only stops Hall9k waiting: the child is a real operating-system
    /// process and keeps running, and disposing the <see cref="Process"/> wrapper detaches from
    /// it rather than ending it. So a Ctrl-C during an import — or a tool that ran past
    /// <see cref="Deadline"/> — would leave gh talking to GitHub with nobody left to read the
    /// answer. The tree goes too, because gh spawns helpers (a credential helper, a pager) that
    /// outlive it.
    /// <para>
    /// It takes no <see cref="CancellationToken"/> deliberately: this runs because waiting has
    /// already been abandoned — the caller cancelled, or the deadline passed — so honouring a
    /// token here would mean skipping the cleanup that is the whole point of arriving. Its own
    /// wait is bounded separately instead, and every failure is swallowed — the process exited on
    /// its own between the check and the kill, or the system refused, and either way the
    /// exception worth propagating is the one already in flight.
    /// </para>
    /// <para>
    /// There is deliberately no "the root has already exited, so there is nothing to do" check.
    /// The root exiting says nothing about its children, and a child outliving it is precisely
    /// what brings the drain-grace path here: the descendant still holding the output pipe is the
    /// process worth ending. Windows still finds it, because the children of a dead pid are
    /// enumerable while this object holds the handle; on Unix an exited root has been reaped and
    /// the call throws instead, which the swallow above turns into the no-op the old check made
    /// unconditional.
    /// </para>
    /// <para>
    /// So on Linux and macOS the drain-grace path reaches nothing at all: the surviving child was
    /// reparented to init the moment the root was reaped, and no tree remains to walk. That is a
    /// real limit rather than a rounding error, and the message on that path says so instead of
    /// claiming a cleanup that did not happen. Reaching the holder there would mean putting the
    /// child in its own process group (setsid) at spawn and signalling the group, which is a
    /// change to how every tool is started rather than to how one is cleaned up; it is noted here
    /// and deliberately not built.
    /// </para>
    /// </summary>
    private static async Task TerminateAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(5));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (Exception)
        {
            // Nothing here is recoverable and nothing here is the caller's problem.
        }
    }
}
