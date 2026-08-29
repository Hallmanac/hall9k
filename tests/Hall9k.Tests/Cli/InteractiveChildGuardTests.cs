using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// Program.cs's global Ctrl-C handler reads <see cref="InteractiveChildGuard.Attached"/> to
/// suppress its own escalate-to-terminate window while h9k task work's attached Claude Code
/// child is alive (adversarial review, cycle 4) — so the flag's own Enter/Dispose pairing is
/// what that suppression rests on.
/// </summary>
public sealed class InteractiveChildGuardTests
{
    [Fact]
    public void Not_attached_before_any_scope_is_entered()
    {
        InteractiveChildGuard.Attached.Should().BeFalse();
    }

    [Fact]
    public void Attached_for_the_scope_s_lifetime_and_cleared_on_dispose()
    {
        using (InteractiveChildGuard.Enter())
        {
            InteractiveChildGuard.Attached.Should().BeTrue();
        }

        InteractiveChildGuard.Attached.Should().BeFalse();
    }

    [Fact]
    public void Cleared_even_when_the_scope_exits_via_exception()
    {
        try
        {
            using IDisposable scope = InteractiveChildGuard.Enter();
            throw new InvalidOperationException("simulated failure while the child is attached");
        }
        catch (InvalidOperationException)
        {
            // Expected — the point is what happens to the flag, not the exception itself.
        }

        InteractiveChildGuard.Attached.Should().BeFalse();
    }
}
