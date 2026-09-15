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

    [Theory]
    [InlineData("messages/5.json", 5L)]
    [InlineData("messages/0.json", 0L)]
    [InlineData("messages/9999999999.json", 9999999999L)]
    public void ParseSeqFromPath_AcceptsTheExactCanonicalName(string path, long expectedSeq)
    {
        long? seq = GitLedgerMessageTransport.ParseSeqFromPath(path);

        seq.Should().Be(expectedSeq);
    }

    [Theory]
    [InlineData("messages/5.txt", "wrong extension")]
    [InlineData("messages/05.json", "a leading zero could collide with the canonical name for 5")]
    [InlineData("messages/+5.json", "a leading sign could collide with the canonical name for 5")]
    [InlineData("messages/x/5.json", "a nested path is never an envelope this ref's own writer wrote")]
    [InlineData("messages/5.json.bak", "wrong extension")]
    [InlineData("messages/five.json", "not a number at all")]
    [InlineData("messages/.json", "no digits at all")]
    public void ParseSeqFromPath_RefusesAnythingThatIsNotTheExactCanonicalName(string path, string because)
    {
        long? seq = GitLedgerMessageTransport.ParseSeqFromPath(path);

        seq.Should().BeNull(because);
    }

    [Fact]
    public void HasSshSignatureHeader_TrueForAnSshSignedCommit()
    {
        string rawCommit =
            "tree deadbeef\n"
            + "parent cafebabe\n"
            + "author A <a@example.com> 1700000000 +0000\n"
            + "committer A <a@example.com> 1700000000 +0000\n"
            + "gpgsig -----BEGIN SSH SIGNATURE-----\n"
            + " U1NIU0lHAAAAAQAAADMAAAALc3NoLWVkMjU1MTkAAAAg\n"
            + " -----END SSH SIGNATURE-----\n"
            + "\n"
            + "Send message 1\n";

        GitLedgerMessageTransport.HasSshSignatureHeader(rawCommit).Should().BeTrue();
    }

    [Fact]
    public void HasSshSignatureHeader_FalseForAnOpenPgpSignedCommit()
    {
        // Exactly the shape a forged commit takes when signed with an OpenPGP key instead of the
        // sender's own registered SSH key: git verify-commit still exits 0 for it against the
        // local machine's own default keyring, so this check must never trust that exit code
        // without confirming the signature itself is SSH first.
        string rawCommit =
            "tree deadbeef\n"
            + "parent cafebabe\n"
            + "author Attacker <attacker@example.com> 1700000000 +0000\n"
            + "committer Attacker <attacker@example.com> 1700000000 +0000\n"
            + "gpgsig -----BEGIN PGP SIGNATURE-----\n"
            + " iQEzBAABCAAdFiEE\n"
            + " -----END PGP SIGNATURE-----\n"
            + "\n"
            + "Send message 1\n";

        GitLedgerMessageTransport.HasSshSignatureHeader(rawCommit).Should().BeFalse();
    }

    [Fact]
    public void HasSshSignatureHeader_FalseWhenThereIsNoSignatureAtAll()
    {
        string rawCommit =
            "tree deadbeef\n"
            + "parent cafebabe\n"
            + "author A <a@example.com> 1700000000 +0000\n"
            + "committer A <a@example.com> 1700000000 +0000\n"
            + "\n"
            + "Send message 1\n";

        GitLedgerMessageTransport.HasSshSignatureHeader(rawCommit).Should().BeFalse();
    }
}
