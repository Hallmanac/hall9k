using System.Globalization;
using System.Runtime.ExceptionServices;

namespace Hall9k.Tests.TestSupport;

/// <summary>
/// Runs a body under a named culture on a thread of its own, so the culture it sets belongs to a
/// thread nothing else in the process will ever run on again.
/// <para>
/// Not because the raw pattern this replaces — set <see cref="CultureInfo.CurrentCulture"/>, act,
/// restore it in a <c>finally</c> — leaks a locale across an <c>await</c>. It does not, and saying
/// otherwise misreads the runtime: <see cref="CultureInfo.CurrentCulture"/>'s setter writes an
/// <see cref="AsyncLocal{T}"/> whose change callback keeps the thread-static copy in step, so the
/// value flows through <see cref="ExecutionContext"/> into everything the test awaits and is
/// unwound again when a pool thread's context is restored — the same behaviour as .NET Framework
/// 4.6+, not a departure from it. A set-and-restore written correctly around an <c>await</c> is
/// sound, and a pool thread a finished test once ran on reads the invariant culture afterwards.
/// </para>
/// <para>
/// What earns the helper is the rest of the surface, and the cost of getting the unwind right every
/// time. <see cref="CultureInfo.DefaultThreadCurrentCulture"/> and
/// <see cref="CultureInfo.DefaultThreadCurrentUICulture"/> — which
/// <c>ProcessWideStateGuardTests</c> covers beside the two flowing properties, because a test
/// reaching for "set the culture" may reach for either — are genuine process-wide statics with no
/// flow to unwind them at all, and even for the flowing pair the unwind does not reach a raw
/// <see cref="Thread"/> the body starts for itself or an <c>ExecutionContext.SuppressFlow</c>
/// region inside it. A leaked non-invariant culture is a real cross-test coupling: any assertion
/// that formats a date or a number without naming a culture reads differently under it.
/// </para>
/// <para>
/// A dedicated <see cref="Thread"/> rather than a pool thread, deliberately: the mutation is then
/// bounded by the thread's own lifetime and needs no restore at all — no <c>finally</c> to omit, no
/// original to capture wrongly, and one answer that covers the process-wide half of that surface as
/// well as the flowing half.
/// </para>
/// </summary>
internal static class CultureScope
{
    /// <summary>
    /// Runs <paramref name="body"/> on a thread whose <see cref="CultureInfo.CurrentCulture"/> and
    /// <see cref="CultureInfo.CurrentUICulture"/> are both <paramref name="culture"/>, and rethrows
    /// whatever it threw — an assertion failure included — with its original stack intact.
    /// </summary>
    public static void Run(string culture, Action body)
    {
        ExceptionDispatchInfo? failure = null;

        Thread thread = new(() =>
        {
            try
            {
                // Inside the try with the body, not above it: a culture name this host's ICU does
                // not know throws CultureNotFoundException right here, and an unhandled exception
                // on a thread of our own tears down the whole test host rather than failing the one
                // case — dotnet test then reports a crashed testhost with no test attributed.
                // Captured, it is rethrown to the caller and read as that case's own failure,
                // naming the culture that could not be resolved.
                CultureInfo named = new(culture);
                CultureInfo.CurrentCulture = named;
                CultureInfo.CurrentUICulture = named;

                body();
            }
            catch (Exception exception)
            {
                failure = ExceptionDispatchInfo.Capture(exception);
            }
        })
        {
            // Never blocks process exit on its own: Join below is what this call waits on, and a
            // body that hangs should fail the test's own timeout rather than the test host's
            // shutdown.
            IsBackground = true,
        };

        thread.Start();
        thread.Join();
        failure?.Throw();
    }

    /// <summary>
    /// The same, for a body that returns a task: it is driven to completion on the scoped thread
    /// itself rather than awaited by the caller. Blocking there is the point — a continuation the
    /// caller awaited instead would carry this culture back out of the scope and onto a pool thread
    /// through <see cref="ExecutionContext"/> flow, which is exactly the boundedness the dedicated
    /// thread exists to provide — and it cannot deadlock, since a plain <see cref="Thread"/> carries
    /// no synchronization context to marshal back to. A body that does resume on a pool thread
    /// (there is no synchronization context here, so <c>Task.Yield</c> will) keeps the culture
    /// regardless, since it flows with the execution context; what blocking buys is that the scoped
    /// thread stays alive for the whole body and the caller never observes the culture at all.
    /// </summary>
    public static void RunToCompletion(string culture, Func<Task> body) =>
        Run(culture, () => body().GetAwaiter().GetResult());
}
