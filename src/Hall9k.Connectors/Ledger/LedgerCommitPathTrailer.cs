namespace Hall9k.Connectors.Ledger;

/// <summary>
/// The one machine-readable signal that lets <see cref="GitLedgerCommitReader.ReadCommitsTouchingPathAsync"/>
/// tell "this commit is this ledger's own authorized write to path P" apart from "path P's tree
/// merely carries forward unchanged, because this commit was for some other path entirely" —
/// something no git tree diff can ever answer, since both produce a byte-identical tree for P
/// relative to the commit's own parent (independent pre-PR review, cycle 5, conformance and
/// adversarial lenses, both high: dropping the pathspec to see a same-content owner reissue
/// (cycle 3) also made every later commit — regardless of which path it actually wrote — read as
/// implicitly re-vouching for whatever every OTHER path already held in its own inherited tree).
/// <see cref="GitLedger"/> stamps every commit it builds with one line per path that commit
/// actually writes or deletes, inside the signed commit message itself (so the trailer is covered
/// by the same signature <see cref="GitLedgerCommitReader.IsSignedByAsync"/> already verifies —
/// nothing about it can be forged separately from the commit it describes).
/// </summary>
internal static class LedgerCommitPathTrailer
{
    private const string Key = "Hall9k-Ledger-Path";

    /// <summary>The commit message <c>git commit-tree -m</c> should actually receive: the
    /// caller's own free-text message, followed by one <c>Hall9k-Ledger-Path:</c> line per path
    /// this commit writes or deletes.</summary>
    public static string Compose(string message, IEnumerable<string> paths) =>
        $"{message}\n\n{string.Join('\n', paths.Select(path => $"{Key}: {path}"))}";

    /// <summary>Every path <paramref name="rawCommitBytes"/> — the exact text <c>git cat-file
    /// commit</c> prints — declares itself the author of, read from the message body alone (never
    /// a header): the body starts at the first wholly empty line, since a signature header's own
    /// continuation lines always carry at least a leading space and can never be empty themselves.</summary>
    public static IReadOnlyList<string> PathsWrittenBy(string rawCommitBytes)
    {
        string[] lines = rawCommitBytes.Split('\n');
        int bodyStart = Array.IndexOf(lines, string.Empty) + 1;
        if (bodyStart <= 0)
        {
            return [];
        }

        return [.. lines
            .Skip(bodyStart)
            .Where(line => line.StartsWith($"{Key}: ", StringComparison.Ordinal))
            .Select(line => line[(Key.Length + 2)..])];
    }
}
