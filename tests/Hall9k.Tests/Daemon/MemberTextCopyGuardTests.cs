using FluentAssertions;
using Hall9k.Daemon.Plan;
using Hall9k.Domain.Features.Epic;
using Hall9k.Domain.Features.Tasks;
using Hall9k.Domain.Infrastructure.Ids;
using Xunit;

namespace Hall9k.Tests.Daemon;

/// <summary>
/// The check that keeps a refinement revision from copying another member project's task text into the draft. It is a pure
/// function of the revision text and the member tasks the session was told of, so nothing here opens a store or an agent.
/// </summary>
public sealed class MemberTextCopyGuardTests
{
    private static readonly SolutionMemberTask CartCache = new(
        Guid.Parse("01a19cbc-1fa6-72ec-b0d0-b58c9b4bfd1d"), "shop-ops", "Provision the cart cache for the storefront", TaskState.Published);

    private static string Revision(string objective, string criterion = "The page loads", string context = "Nothing more.") =>
        $"---\nproject: shop-web\nobjective: {objective}\ncriteria:\n- {criterion}\nblocked-by:\n- 01a19cbc: needs the shop-ops cache first\n---\n{context}\n";

    [Fact]
    public void A_revision_that_copies_a_member_tasks_objective_is_refused_naming_only_that_tasks_short_id()
    {
        // Case and spacing differ from the member's text; the copy is still found, and it counts in the context body too.
        MemberTextCopyGuard.Refusal(Revision("Show the cart", context: "Wire it to provision the cart\ncache for the storefront."), [CartCache])
            .Should().Contain(DomainId.Short(CartCache.Id));

        string? refusal = MemberTextCopyGuard.Refusal(
            Revision("Wire the page to PROVISION   the cart cache for the storefront today"), [CartCache]);

        refusal.Should().Contain(DomainId.Short(CartCache.Id));
        refusal.Should().NotContainEquivalentOf("provision the cart cache");
        refusal.Should().NotContain("shop-ops");
    }

    [Fact]
    public void A_revision_that_names_a_member_project_in_its_criteria_or_context_is_refused_but_a_blocked_by_reason_may_name_it()
    {
        MemberTextCopyGuard.Refusal(Revision("Show the cart", criterion: "Reads from Shop-Ops directly"), [CartCache])
            .Should().Contain(DomainId.Short(CartCache.Id));
        MemberTextCopyGuard.Refusal(Revision("Show the cart", context: "Mirror what shop-ops does."), [CartCache])
            .Should().Contain(DomainId.Short(CartCache.Id));

        // The fixture's blocked-by reason names shop-ops, and a revision that copies nothing else passes.
        MemberTextCopyGuard.Refusal(Revision("Show the cart"), [CartCache]).Should().BeNull();
    }

    [Fact]
    public void A_revision_that_copies_nothing_passes_and_a_project_name_inside_a_longer_word_is_not_a_copy()
    {
        SolutionMemberTask api = CartCache with { ProjectName = "api" };

        MemberTextCopyGuard.Refusal(Revision("Make the page rapid", context: "A capital idea."), [api]).Should().BeNull();
        MemberTextCopyGuard.Refusal(Revision("Show the cart"), []).Should().BeNull();
        MemberTextCopyGuard.Refusal(Revision("Show the cart"), null).Should().BeNull();
    }
}
