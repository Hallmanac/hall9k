using FluentAssertions;
using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>--repo-url</c> accepts. The case that matters is the scp-style remote, which is the
/// form GitHub's Code button hands out and the ordinary way to reach a private repository, and
/// which is not a URI at all — so <c>new Uri</c> throws <c>UriFormatException</c> on it and the
/// CLI's exception mapping, which knows only the domain exceptions, lets it out as a stack trace.
/// </summary>
public sealed class GitRemoteUrlTests
{
    [Fact]
    public void An_scp_style_remote_is_recorded_as_its_ssh_equivalent()
    {
        Uri remote = GitRemoteUrl.Parse("git@github.com:you/myproject.git");

        remote.Should().Be(new Uri("ssh://git@github.com/you/myproject.git"));
        remote.Host.Should().Be(
            "github.com", "the render reads the host to decide the project needs gh");
    }

    [Theory]
    [InlineData("https://github.com/you/myproject.git")]
    [InlineData("ssh://git@github.com/you/myproject.git")]
    [InlineData("git://example.com/myproject.git")]
    public void A_url_that_is_already_a_url_is_left_as_it_is(string value) =>
        GitRemoteUrl.Parse(value).Should().Be(new Uri(value));

    [Fact]
    public void An_unusable_remote_is_a_refusal_that_names_the_forms_that_work()
    {
        Action act = () => GitRemoteUrl.Parse("not a remote at all");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--repo*", "the message has to teach its own correction");
    }
}
