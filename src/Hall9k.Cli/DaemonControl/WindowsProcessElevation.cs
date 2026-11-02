using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Hall9k.Cli.DaemonControl;

/// <summary>
/// Reads <c>TokenElevation</c> off the current process's own token. Not
/// <c>WindowsPrincipal.IsInRole(Administrators)</c>: that answers whether the account is an
/// administrator, which is true of a non-elevated shell under UAC, while the question that
/// decides whether a HighestAvailable task can be registered is whether this token is elevated.
/// </summary>
public sealed class WindowsProcessElevation : IProcessElevation
{
    private const int TokenElevationClass = 20;

    public bool IsElevated()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Token elevation is read from a Windows access token.");
        }

        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        if (!GetTokenInformation(
                identity.AccessToken, TokenElevationClass, out int isElevated, sizeof(int), out _))
        {
            throw new InvalidOperationException(
                $"Could not read this process's token elevation (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        return isElevated != 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(
        SafeAccessTokenHandle token,
        int tokenInformationClass,
        out int tokenInformation,
        int tokenInformationLength,
        out int returnLength);
}
