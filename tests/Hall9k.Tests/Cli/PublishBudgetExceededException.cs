using System.Globalization;
using Hall9k.Daemon.Execution;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <see cref="PublishTestSupport.RunPublishAsync"/> throws when its <c>dotnet publish</c>
/// does not finish inside <see cref="PublishTestSupport.PublishBudget"/>. A budget miss says
/// nothing about the product — the publish never reported a result at all — so it is reported as
/// an infrastructure-class timeout that names the load it saw, rather than as the bare
/// <see cref="OperationCanceledException"/> the helper used to let escape, which read to a human
/// scanning CI (and to <see cref="GateInfrastructureFailureClassifier"/>) exactly like a product
/// assertion failing (origin: the Windows full-suite baseline of 2026-09-08, where both publish
/// tests cancelled at five minutes in two consecutive full runs and the failure line said only
/// "OperationCanceledException", naming neither the timeout nor the fifteen concurrent dotnet
/// processes that caused it).
/// </summary>
internal sealed class PublishBudgetExceededException : Exception
{
    private PublishBudgetExceededException(string message) : base(message)
    {
    }

    /// <summary>
    /// The message leads with <see cref="GateInfrastructureFailureClassifier.PublishBudgetExceededMarker"/>
    /// so the daemon's gate classifier reads the miss as infrastructure and retries the gate once
    /// instead of failing the run on it, and carries the two load figures a human needs to tell a
    /// genuinely wedged publish from a loaded machine: how long the publish actually ran and how
    /// many dotnet-family processes were alive when it was killed.
    /// </summary>
    internal static PublishBudgetExceededException ForMiss(
        string project, TimeSpan budget, TimeSpan elapsed, string load, string publishOutputTail)
    {
        // Both figures are formatted invariantly: this message is read by the classifier's own
        // literal matching and by whoever is reading a CI log, and neither wants a decimal
        // separator that depends on the node's locale. Both are in seconds, and the budget is not
        // rendered in its own natural unit of minutes, so the two are directly comparable and so
        // a budget under a minute cannot read as "0-minute" (observed while exercising this path
        // against a deliberately tiny budget).
        string budgetSeconds = budget.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);
        string elapsedSeconds = elapsed.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture);

        return new PublishBudgetExceededException(
            $"{GateInfrastructureFailureClassifier.PublishBudgetExceededMarker}: publishing {project} "
            + $"did not finish inside its {budgetSeconds}s budget ({elapsedSeconds}s elapsed "
            + $"before the process tree was killed). Load at the miss: {load}. This is a timeout, "
            + "not a failed assertion — the publish reported no result about the product either way. "
            + $"Last publish output: {publishOutputTail}");
    }
}
