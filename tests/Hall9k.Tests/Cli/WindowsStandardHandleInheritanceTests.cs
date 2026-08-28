using System.Runtime.InteropServices;
using FluentAssertions;
using Hall9k.Cli.DaemonControl;
using Xunit;

namespace Hall9k.Tests.Cli;

public sealed class WindowsStandardHandleInheritanceTests
{
    private const int StdOutputHandle = -11;
    private const uint HandleFlagInherit = 0x00000001;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint PipeAccessDuplex = 0x00000003;
    private const uint FileFlagOverlapped = 0x40000000;

    [Fact]
    public void Suppression_clears_the_inherit_flag_and_dispose_restores_it()
    {
        // dotnet test's own stdout is not a reliable fixture — whether it happens to be
        // inheritable depends on how the test host was launched. This installs a real,
        // known-inheritable pipe handle as the process's STD_OUTPUT_HANDLE so the guard's
        // actual effect (dotnet/runtime#19569's fix: strip the inherit flag before a child
        // is created, put it back after) is observed directly rather than assumed.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string pipeName = $@"\\.\pipe\h9k-test-{Guid.NewGuid():N}";
        IntPtr original = GetStdHandle(StdOutputHandle);
        IntPtr readEnd = CreateNamedPipeW(
            pipeName,
            PipeAccessDuplex | FileFlagOverlapped,
            0, 1, 4096, 4096, 0, IntPtr.Zero);
        readEnd.Should().NotBe(new IntPtr(-1));

        SecurityAttributes inheritable = new()
        {
            Length = Marshal.SizeOf<SecurityAttributes>(),
            InheritHandle = true,
        };
        IntPtr writeEnd = CreateFileW(
            pipeName, GenericWrite, 0, ref inheritable, OpenExisting, 0, IntPtr.Zero);
        writeEnd.Should().NotBe(new IntPtr(-1));

        try
        {
            SetStdHandle(StdOutputHandle, writeEnd).Should().BeTrue();
            GetHandleInformation(writeEnd, out uint before).Should().BeTrue();
            (before & HandleFlagInherit).Should().Be(HandleFlagInherit, "the fixture handle was opened inheritable");

            using (WindowsStandardHandleInheritance.SuppressForChildProcesses())
            {
                GetHandleInformation(writeEnd, out uint suppressed).Should().BeTrue();
                (suppressed & HandleFlagInherit).Should().Be(0u, "a child created while the guard is open must not inherit this handle");
            }

            GetHandleInformation(writeEnd, out uint restored).Should().BeTrue();
            (restored & HandleFlagInherit).Should().Be(HandleFlagInherit, "dispose must put the original inheritability back");
        }
        finally
        {
            SetStdHandle(StdOutputHandle, original);
            CloseHandle(writeEnd);
            CloseHandle(readEnd);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr SecurityDescriptor;
        public bool InheritHandle;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetHandleInformation(IntPtr hObject, out uint lpdwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateNamedPipeW(
        string lpName, uint dwOpenMode, uint dwPipeMode, uint nMaxInstances,
        uint nOutBufferSize, uint nInBufferSize, uint nDefaultTimeOut, IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFileW(
        string lpFileName, uint dwDesiredAccess, uint dwShareMode, ref SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);
}
