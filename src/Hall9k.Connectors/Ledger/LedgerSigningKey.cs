namespace Hall9k.Connectors.Ledger;

/// <summary>
/// An SSH private key file <see cref="GitLedger"/> signs every commit with, passed per git
/// invocation as <c>-c gpg.format=ssh -c user.signingkey=&lt;PrivateKeyPath&gt;</c> — never
/// written to git config, global or per-repository, and never defeated by
/// <c>NonInteractiveGit</c>'s own <c>commit.gpgsign=false</c> environment, since an explicit
/// <c>-S</c> on the command line outranks it. Optional through this task (A1); A2a makes it
/// mandatory once every node actually has a key.
/// <para>
/// Windows note: SSH commit signing shells out to <c>ssh-keygen</c> for both signing and
/// verification (git's default <c>gpg.ssh.program</c>) — on Windows that has to be the
/// <c>ssh-keygen.exe</c> Git for Windows ships with its own installation, since there is no
/// guaranteed system OpenSSH otherwise. A node without it on PATH cannot sign a ledger commit.
/// </para>
/// </summary>
public sealed record LedgerSigningKey(string PrivateKeyPath);
