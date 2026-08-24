using FluentAssertions;
using Hall9k.Cli.Commands;
using Hall9k.Domain.Features.Project;
using Hall9k.Domain.Shared.Exceptions;
using Xunit;

namespace Hall9k.Tests.Cli;

/// <summary>
/// What <c>--link</c> accepts. Origin: the pre-PR review of the project-home branch found
/// <c>ParseLink</c> calling <c>new Uri(...)</c> directly on a malformed value (a link with no
/// scheme, e.g. <c>design=github.com/you/proj/wiki</c>), so a caller got an unhandled
/// <c>UriFormatException</c> — a type the CLI's exception mapping does not know — instead of a
/// rule to self-correct from. This is the same defect class <see cref="GitRemoteUrl"/> was
/// introduced to fix for <c>--repo-url</c>.
/// </summary>
public sealed class ProjectSetCommandTests
{
    [Fact]
    public void A_well_formed_link_is_recorded_as_its_named_url()
    {
        ContextLink link = ProjectSetCommand.ParseLink("wiki=https://github.com/you/proj/wiki");

        link.Name.Should().Be("wiki");
        link.Url.Should().Be(new Uri("https://github.com/you/proj/wiki"));
    }

    [Fact]
    public void A_link_with_no_scheme_is_a_refusal_rather_than_an_unhandled_exception()
    {
        Action act = () => ProjectSetCommand.ParseLink("design=github.com/you/proj/wiki");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*--link*", "the message has to teach its own correction");
    }

    [Fact]
    public void A_link_with_no_separator_is_a_refusal_naming_the_expected_shape()
    {
        Action act = () => ProjectSetCommand.ParseLink("not-a-name-value-pair");

        act.Should().Throw<DomainValidationException>()
            .WithMessage("*name=url*");
    }
}
