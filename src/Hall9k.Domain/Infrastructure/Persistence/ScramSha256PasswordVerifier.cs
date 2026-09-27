using System.Security.Cryptography;
using System.Text;

namespace Hall9k.Domain.Infrastructure.Persistence;

/// <summary>
/// Builds the exact string Postgres itself stores in <c>pg_authid.rolpassword</c> for a
/// SCRAM-SHA-256 role password (<c>src/common/scram-common.c</c>'s own
/// <c>scram_build_verifier</c>), so the password migration's <c>ALTER ROLE ... PASSWORD '…'</c>
/// (<see cref="Hall9k.Cli.Diagnostics.DatabaseDoctor"/>) can hand Postgres an already-encrypted
/// value instead of a plaintext literal. Postgres detects a value already shaped like
/// <c>SCRAM-SHA-256$…</c> (or <c>md5…</c>) and stores it as-is rather than re-encrypting it, which
/// is what keeps the generated password itself off the wire, off <c>pg_stat_activity</c>, and out
/// of the server's own statement log for that command (security review idea 6be68ee2,
/// secrets-files-network finding 1: the live server logs failed statements at
/// <c>log_min_error_statement=error</c>, so a cleartext literal in a failed <c>ALTER ROLE</c> would
/// land there).
/// <para>
/// The algorithm is RFC 5802's <c>Hi()</c> and the two HMAC derivations that follow it, exactly as
/// RFC 7677 (SCRAM-SHA-256) and Postgres's own implementation apply them — no shortcuts, since a
/// verifier that does not match what a real <c>psql \password</c> would produce fails
/// authentication silently until someone diffs it against a known-good one.
/// </para>
/// </summary>
public static class ScramSha256PasswordVerifier
{
    /// <summary>Postgres's own default (<c>scram_common.c</c>'s <c>SCRAM_DEFAULT_SALT_LEN</c>).</summary>
    private const int SaltLengthBytes = 16;

    /// <summary>Postgres's own default (<c>scram_common.c</c>'s <c>SCRAM_SHA_256_DEFAULT_ITERATIONS</c>).</summary>
    private const int Iterations = 4096;

    private static readonly byte[] ClientKeyLabel = Encoding.ASCII.GetBytes("Client Key");
    private static readonly byte[] ServerKeyLabel = Encoding.ASCII.GetBytes("Server Key");

    /// <summary>
    /// <c>SCRAM-SHA-256$&lt;iterations&gt;:&lt;salt&gt;$&lt;StoredKey&gt;:&lt;ServerKey&gt;</c>,
    /// salt/StoredKey/ServerKey each standard base64 — the identical layout
    /// <c>scram_build_verifier</c> emits, byte for byte.
    /// </summary>
    public static string Build(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLengthBytes);
        byte[] saltedPassword = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, Iterations, HashAlgorithmName.SHA256, SHA256.HashSizeInBytes);

        byte[] clientKey = HMACSHA256.HashData(saltedPassword, ClientKeyLabel);
        byte[] storedKey = SHA256.HashData(clientKey);
        byte[] serverKey = HMACSHA256.HashData(saltedPassword, ServerKeyLabel);

        return $"SCRAM-SHA-256${Iterations}:{Convert.ToBase64String(salt)}$"
            + $"{Convert.ToBase64String(storedKey)}:{Convert.ToBase64String(serverKey)}";
    }
}
