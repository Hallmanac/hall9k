/*
 * spawn.c - the program under test. No Hall9k code, no third-party library.
 *
 *   spawn.exe 0   starts no child and exits at once (the control arm).
 *   spawn.exe 1   starts ONE child, which is this same image run as "spawn.exe 0",
 *                 waits for it to exit, then exits (the spawning arm).
 *
 * Both arms are the same file; only the first argument differs.
 */
#include <windows.h>
#include <wchar.h>

/* Default: the child inherits the console (no extra console host process appears).
   Build with -DNOWINDOW to start it with CREATE_NO_WINDOW the way h9kd starts its children. */
#ifdef NOWINDOW
#define LAUNCH_FLAGS CREATE_NO_WINDOW
#else
#define LAUNCH_FLAGS 0
#endif

int wmain(int argc, wchar_t **argv)
{
    if (argc < 2 || argv[1][0] != L'1')
        return 0;                                   /* arm "0": no child */

    wchar_t self[MAX_PATH * 2];
    DWORD n = GetModuleFileNameW(NULL, self, (DWORD)(sizeof self / sizeof self[0]));
    if (n == 0 || n >= sizeof self / sizeof self[0])
        return 2;

    wchar_t cmd[MAX_PATH * 2 + 8];
    swprintf(cmd, sizeof cmd / sizeof cmd[0], L"\"%ls\" 0", self);

    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    ZeroMemory(&pi, sizeof pi);

    /* no handle inheritance: nothing about the child is unusual */
    if (!CreateProcessW(self, cmd, NULL, NULL, FALSE, LAUNCH_FLAGS, NULL, NULL, &si, &pi))
        return 3;

    WaitForSingleObject(pi.hProcess, INFINITE);     /* still running when the child exits */
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 0;
}
