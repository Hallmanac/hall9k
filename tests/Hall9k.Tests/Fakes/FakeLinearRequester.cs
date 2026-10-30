using Hall9k.Connectors.WorkItems;

namespace Hall9k.Tests.Fakes;

/// <summary>
/// The Linear-side counterpart to <see cref="FakeJiraRequester"/>: a requester for a test that builds
/// a real <see cref="LinearWorkItemProvider"/> but only exercises a member that must never reach the
/// network, so "this test never calls Linear" is enforced rather than assumed.
/// </summary>
public static class FakeLinearRequester
{
    /// <summary>
    /// The refusal quotes only the request's URL, never its <see cref="LinearRequest.Authorization"/>,
    /// so a test that trips this guard reports what it asked for without spilling the key it asked with.
    /// </summary>
    public static LinearRequester NeverInvoked() => (request, _) => throw new InvalidOperationException(
        $"this test's LinearRequester was not expected to be called, but was invoked for '{request.Url}'; "
        + "if that changed, give it a real fake instead of this guard");
}
