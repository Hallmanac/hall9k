using FluentAssertions;
using Hall9k.Connectors.Messaging;
using Xunit;

namespace Hall9k.Tests.Connectors.Messaging;

/// <summary>
/// <see cref="GitLedgerMessageTransport.ExtractQuotedYamlValue"/> alone — a pure string function,
/// so this needs no repository, unlike the rest of the class (Brian's 2026-09-13 testing rule:
/// only <c>GitLedgerTests</c> and the chain reader's own tests touch a real repository). It reverses
/// <c>ProjectJoinCommand</c>'s own <c>QuoteYaml</c>, so every input here is built the same way that
/// helper would have written it.
/// </summary>
public sealed class GitLedgerMessageTransportTests
{
    [Fact]
    public void ExtractsAPlainValue()
    {
        string yaml = "node_id: \"11111111-1111-1111-1111-111111111111\"\npublic_key: \"ssh-ed25519 AAAAfake test\"\n";

        string? value = GitLedgerMessageTransport.ExtractQuotedYamlValue(yaml, "public_key");

        value.Should().Be("ssh-ed25519 AAAAfake test");
    }

    [Fact]
    public void ReturnsNullWhenTheKeyIsAbsent()
    {
        string yaml = "node_id: \"11111111-1111-1111-1111-111111111111\"\n";

        string? value = GitLedgerMessageTransport.ExtractQuotedYamlValue(yaml, "public_key");

        value.Should().BeNull();
    }

    [Fact]
    public void UnescapesABackslashAndAQuoteTheSameWayQuoteYamlEscapedThem()
    {
        // QuoteYaml's own order: every backslash doubled first, then every quote escaped —
        // reproduced by hand here rather than calling the private helper this mirrors.
        string rawValue = "a \"quoted\" comment with a \\backslash";
        string escaped = rawValue.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string yaml = $"public_key: \"{escaped}\"\n";

        string? value = GitLedgerMessageTransport.ExtractQuotedYamlValue(yaml, "public_key");

        value.Should().Be(rawValue);
    }
}
