using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// Reads <c>TokenElevation</c> off any process's token: <c>OpenProcess</c> with
/// <c>PROCESS_QUERY_LIMITED_INFORMATION</c>, <c>OpenProcessToken</c> with <c>TOKEN_QUERY</c>, then
/// <c>GetTokenInformation</c>. All three succeed from a non-elevated process against an elevated
/// one of the same user, so a same-user window is always readable; a failure anywhere (another
/// account's process, a pid already gone) reads as <see cref="ProcessElevation.Unknown"/>.
/// Not <c>WindowsPrincipal.IsInRole(Administrators)</c>: that answers whether the account is an
/// administrator, which a non-elevated shell under UAC also is.
/// </summary>
public sealed class WindowsProcessElevationReader : IProcessElevationReader
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    public bool IsSupported => OperatingSystem.IsWindows();

    public ProcessElevation Read(int processId)
    {
        if (!OperatingSystem.IsWindows())
        {
            return ProcessElevation.Unknown;
        }

        using SafeProcessHandle process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process.IsInvalid)
        {
            return ProcessElevation.Unknown;
        }

        if (!OpenProcessToken(process, TokenQuery, out SafeAccessTokenHandle token))
        {
            token.Dispose();
            return ProcessElevation.Unknown;
        }

        using (token)
        {
            if (!GetTokenInformation(token, TokenElevationClass, out int isElevated, sizeof(int), out _))
            {
                return ProcessElevation.Unknown;
            }

            return isElevated != 0
                ? ProcessElevation.Elevated
                : ProcessElevation.NotElevated;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(
        SafeProcessHandle process, uint desiredAccess, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
