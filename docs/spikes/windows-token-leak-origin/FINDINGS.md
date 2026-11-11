# Is the kernel token leak left after 54c44399 a Windows defect, and which h9kd launch chains keep a token?

Research task; findings only. Nothing under `src/` changed. All times are EST (UTC-5), 2026-11-10. Everything was measured on the Windows node itself while h9kd kept running and serving its projects.

## Answers

1. **Mechanism.** On this node (Windows 11 Pro 26H2, OS build 26300.9711), a process that starts a child, and is still running when that child exits, keeps its own kernel access token (pool tag `Toke`) allocated after it exits, until reboot. A process that starts no child does not. This reproduces with a short C program that uses no Hall9k code (below).
2. **One token per launch: yes by the kernel pool trace; the live count points the same way but does not settle it.** The trace is exact: 150 launches of the spawning arm left 150 child tokens never freed (and 150 of 150 again in a second trace), against 0 of 150 for the same program started so that it starts no child. The live-count batches agree in direction and size but are noisy: 0.74 tokens per launch over 20 pairs (standard error 0.14) against 0.08 (standard error 0.20) for the null run. That is 2.7 standard errors apart, and 1.8 below one token per launch, so the batches alone neither prove nor exclude exactly one; only the trace measures it.
3. **Verdict on "is it a Windows defect": unconfirmed.** Seven vendors' third-party kernel drivers were loaded during every measurement, and excluding them needs a step this run may not take (it needs this node to restart, which ends h9kd, or a driver to be stopped). The step is named in [the verdict section](#verdict-windows-defect-unconfirmed). Nothing observed points at a driver: every frame on the kernel stack that allocates the leaked token is inside `ntoskrnl.exe`.
4. **What h9kd can avoid.** In the hour traced, the remaining leak (after taking out Git for Windows' launcher, which 54c44399 removes) was **9.1 tokens a second, about 32,800 an hour** for h9kd's own process tree; the measuring session's subtree adds 124 tokens (0.4%). Two chains h9kd can avoid make up **72.3%** of it, and a third can be halved:
   - **53.8%** is the credential-helper chain `git-remote-https > sh > gh`, which comes from the owner's `~/.gitconfig`. h9kd can avoid it by starting git with the helper switched off and the token supplied through the environment, which cut the tokens kept per remote git call from 7 to 2 in a controlled trace.
   - **18.5%** is the real `git` that h9kd starts for `verify-commit`, which in turn starts `ssh-keygen` twice (6,110 kept tokens, one per verification). h9kd can avoid it by not re-verifying a commit it has already verified, or by running `ssh-keygen -Y` itself, which it already does elsewhere with every token freed.
   - **3.9%** (h9kd's own `gh` calls) can be roughly halved: `gh` starts `tzutil /g` on every call; setting `TZ` for `gh` removes that child, though `gh` then still keeps about half of its tokens for a reason this run did not find.

   What h9kd cannot avoid, short of making fewer remote git calls, is real `git` starting its own `rev-list`, `maintenance` and `remote-https` helpers on a fetch or `ls-remote` (11.9%, plus the dispatcher's 11.9%). Details and shares are in [the chain table](#launch-chains-under-h9kd-that-keep-a-token).
5. **Not done, and why: acceptance criterion 3 is not met.** The criterion asked for a kernel trace of the running daemon that covers at least one gate run and one agent session start. The 62-minute trace contained **neither**: the node was idle (only this task was working). Chain rows 7 and 9 (gates and agent sessions) therefore carry **no measured share**; they are marked "not measured" in the table, never 0%. The two launches were instead reproduced with their exact `ProcessStartInfo` shape, started by a helper in a separate kernel trace; that gives tokens per launch (and only for one scoped test class, not a full gate), which is not a share of the rate. This document says so wherever a row rests on that stand-in. Draft 3 below re-takes the daemon trace over a busy window.

## Run identity

| | h9kd process id | start time |
|---|---|---|
| at the start of the run (2026-11-10 20:17:32) | 34684 | 2026-11-10 11:39:57.747 -05:00 |
| at the end of the run (2026-11-10 23:44:48) | 34684 | 2026-11-10 11:39:57.747 -05:00 |

They match: h9kd was never stopped, restarted or replaced. Every launch below ran in a helper started by this session, and every trace read the daemon without touching it. The daemon trace used `wpr` (a kernel logger session) and `Get-CimInstance Win32_Process`.

**The h9kd build traced** is `C:\Users\<user>\.hall9k\bin\h9kd.exe`, file version 0.18.0, product version `0.18.0+870487ce726d214305b697a412e0c40f42272c52`, written 2026-11-09 15:21, SHA-256 `5FD93FAD6B5DB4E6BA5816CBE7831947AEA3B9D9D1D583F204DBBCD62CC7BAFB`. Commit 870487ce is dated 2026-11-09 10:07; 54c44399's commits (99e54bc4 to a848a1c3) are dated 2026-11-10 20:13. **This build predates 54c44399**, so it still starts Git for Windows' launcher (`C:\Program Files\Git\cmd\git.exe`) by name. How the launcher's tokens were taken out is in [the chain table](#launch-chains-under-h9kd-that-keep-a-token).

Platform: Windows 11 Pro, version 26H2, OS build 26300.9711 (`ver`: 10.0.26300.9711), x64, Intel Core i7-14700 (28 logical processors), 29.7 GB RAM, Dell XPS 8960, booted 2026-11-10 11:31. Virtualization-based security and hypervisor-enforced code integrity are running (`Win32_DeviceGuard`: VBS status 2, security services 2 and 7). Defender real-time protection is on. Tools installed for this run: LLVM MinGW (clang 23.1.4, winget package `MartinStorsjo.LLVM-MinGW.UCRT`, user scope) to build the native repro; nothing else was installed, no driver or service was touched.

## The minimal repro (no Hall9k code)

Two C programs, built with clang from LLVM MinGW. Full source, nothing omitted:

`repro/spawn.c`: the program under test. `spawn.exe 0` starts no child. `spawn.exe 1` starts one child (itself, as `spawn.exe 0`), waits for it, and exits. Both arms are the same file.

```c
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
```

`repro/bench.c`: reads the live count of access tokens (pool tag `Toke`, allocations minus frees, `NtQuerySystemInformation` class 22) and runs the batches. Each batch is: read the count, launch the arm `<launches>` times one after another (each launch waits for its process to exit), idle until a fixed window has passed since the batch began, read the count again. Every batch therefore has the same wall-clock length. The arm that goes first swaps every pair.

```c
/*
 * bench.c - batch driver. No Hall9k code, no third-party library.
 *
 *   bench.exe <spawn.exe> <armA-arg> <armB-arg> <launches> <window-seconds> <pairs> [first]
 *
 * Runs <pairs> pairs of batches. A batch is: read the live count of kernel access
 * tokens (pool tag "Toke": allocations minus frees, NtQuerySystemInformation
 * class 22, SystemPoolTagInformation), launch "<spawn.exe> <arm-arg>" <launches>
 * times one after another (each launch waits for its process to exit), then idle
 * until <window-seconds> after the batch began, then read the count again. Every
 * batch therefore has the same wall-clock length, so the background growth of the
 * counter lands on both arms alike.
 *
 * The arm that goes first swaps every pair: pair 1 runs A then B, pair 2 runs B
 * then A, and so on (pass "B" as the optional last argument to start with B).
 *
 * Output: one CSV line per batch on stdout, then a summary on stderr.
 *   pair,order,arm,arg,launches,before,after,delta,launch_seconds,window_seconds
 *
 * Needs an elevated process on this node (the pool-tag query is refused otherwise).
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* Default: children inherit the console. Build with -DNOWINDOW for CREATE_NO_WINDOW (h9kd style). */
#ifdef NOWINDOW
#define LAUNCH_FLAGS CREATE_NO_WINDOW
#else
#define LAUNCH_FLAGS 0
#endif

typedef LONG (NTAPI *NtQuerySystemInformationFn)(ULONG, PVOID, ULONG, PULONG);

static long long toke_live(void)
{
    static NtQuerySystemInformationFn query;
    static BYTE *buf;
    static ULONG cap;
    if (!query)
        query = (NtQuerySystemInformationFn)GetProcAddress(GetModuleHandleW(L"ntdll.dll"),
                                                           "NtQuerySystemInformation");
    for (;;) {
        if (cap == 0) {
            cap = 4u << 20;
            buf = (BYTE *)malloc(cap);
        }
        ULONG ret = 0;
        LONG st = query(22, buf, cap, &ret);
        if (st == (LONG)0xC0000004) {               /* STATUS_INFO_LENGTH_MISMATCH */
            free(buf);
            cap = (ret > cap ? ret : cap) + (1u << 20);
            buf = (BYTE *)malloc(cap);
            continue;
        }
        if (st != 0) {
            fprintf(stderr, "NtQuerySystemInformation(22) failed: 0x%08lX (run elevated)\n",
                    (unsigned long)st);
            exit(1);
        }
        break;
    }
    ULONG count = *(ULONG *)buf;
    for (ULONG i = 0; i < count; i++) {
        BYTE *e = buf + 8 + (size_t)i * 40;         /* SYSTEM_POOLTAG, 40 bytes each */
        if (*(DWORD *)e == 0x656B6F54) {            /* "Toke" */
            DWORD allocs = *(DWORD *)(e + 4), frees = *(DWORD *)(e + 8);   /* paged */
            return (long long)(DWORD)(allocs - frees);
        }
    }
    return -1;
}

static double now_seconds(void)
{
    static LARGE_INTEGER freq;
    LARGE_INTEGER t;
    if (!freq.QuadPart)
        QueryPerformanceFrequency(&freq);
    QueryPerformanceCounter(&t);
    return (double)t.QuadPart / (double)freq.QuadPart;
}

static int launch_once(const wchar_t *exe, const wchar_t *arg)
{
    wchar_t cmd[1024];
    swprintf(cmd, sizeof cmd / sizeof cmd[0], L"\"%ls\" %ls", exe, arg);
    STARTUPINFOW si;
    PROCESS_INFORMATION pi;
    ZeroMemory(&si, sizeof si);
    si.cb = sizeof si;
    ZeroMemory(&pi, sizeof pi);
    if (!CreateProcessW(exe, cmd, NULL, NULL, FALSE, LAUNCH_FLAGS, NULL, NULL, &si, &pi))
        return 0;
    WaitForSingleObject(pi.hProcess, INFINITE);
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return 1;
}

typedef struct {
    int pair;
    const char *order;
    char arm;
    const wchar_t *arg;
    long long before, after;
    double launch_s;
} Batch;

static void batch(Batch *b, const wchar_t *exe, int launches, double window)
{
    double t0 = now_seconds();
    b->before = toke_live();
    for (int i = 0; i < launches; i++) {
        if (!launch_once(exe, b->arg)) {
            fprintf(stderr, "CreateProcess failed: %lu\n", GetLastError());
            exit(1);
        }
    }
    b->launch_s = now_seconds() - t0;
    if (b->launch_s > window - 8.0) {               /* tokens are freed up to ~5 s late */
        fprintf(stderr, "launches took %.1f s of a %.1f s window; raise the window\n",
                b->launch_s, window);
        exit(1);
    }
    double left = window - (now_seconds() - t0);
    if (left > 0)
        Sleep((DWORD)(left * 1000.0));
    b->after = toke_live();
}

int wmain(int argc, wchar_t **argv)
{
    if (argc < 7) {
        fprintf(stderr, "usage: bench <spawn.exe> <armA-arg> <armB-arg> <launches> "
                        "<window-seconds> <pairs> [A|B first]\n");
        return 64;
    }
    const wchar_t *exe = argv[1], *argA = argv[2], *argB = argv[3];
    int launches = _wtoi(argv[4]);
    double window = _wtof(argv[5]);
    int pairs = _wtoi(argv[6]);
    int bFirst = argc > 7 && (argv[7][0] == L'B' || argv[7][0] == L'b');

    printf("pair,order,arm,arg,launches,before,after,delta,launch_seconds,window_seconds\n");
    long long sumA = 0, sumB = 0;
    for (int p = 1; p <= pairs; p++) {
        int swap = bFirst ? (p % 2 == 1) : (p % 2 == 0);   /* swap every pair */
        Batch a = {p, swap ? "second" : "first", 'A', argA};
        Batch b = {p, swap ? "first" : "second", 'B', argB};
        if (swap) { batch(&b, exe, launches, window); batch(&a, exe, launches, window); }
        else      { batch(&a, exe, launches, window); batch(&b, exe, launches, window); }
        Batch *both[2] = {&a, &b};
        for (int k = 0; k < 2; k++) {
            Batch *x = both[k];
            printf("%d,%s,%c,%ls,%d,%lld,%lld,%lld,%.2f,%.1f\n", x->pair, x->order, x->arm,
                   x->arg, launches, x->before, x->after, x->after - x->before, x->launch_s,
                   window);
        }
        fflush(stdout);
        sumA += a.after - a.before;
        sumB += b.after - b.before;
        fprintf(stderr, "pair %d: A %+lld, B %+lld, per launch (A-B)/n = %.2f\n", p,
                a.after - a.before, b.after - b.before,
                (double)((a.after - a.before) - (b.after - b.before)) / launches);
    }
    fprintf(stderr, "TOTAL %d launches per arm: A %+lld, B %+lld, per launch %.2f\n",
            launches * pairs, sumA, sumB, (double)(sumA - sumB) / (launches * pairs));
    return 0;
}
```

**Build commands** (clang from LLVM MinGW; run in `docs/spikes/windows-token-leak-origin/repro`):

```
clang -O2 -municode -o spawn.exe spawn.c
clang -O2 -municode -o bench.exe bench.c
:: the variant that starts every child with CREATE_NO_WINDOW, the way h9kd's own children are started
clang -O2 -municode -DNOWINDOW -o spawn-nw.exe spawn.c
clang -O2 -municode -DNOWINDOW -o bench-nw.exe bench.c
```

**Run commands** (elevated; the pool-tag query is refused otherwise): `bench.exe spawn.exe 1 0 400 40 10 A` is the real run (arm A = `spawn.exe 1`, arm B = `spawn.exe 0`, 400 launches a batch, 40 s window, 10 pairs, arm A first in pair 1). `bench.exe spawn.exe 0 0 400 40 10 A` is the null run: arm A and arm B are both `spawn.exe 0`, same shape.

Why two variants: with the default flags the child inherits the console and the trace shows exactly one process per launch in arm B and two in arm A. With `CREATE_NO_WINDOW` (how h9kd starts children) the kernel also starts a `conhost.exe` for every process; its token is always freed and it is in both arms. Both variants are reported.

## Live count of tokens, in alternating batches

Every batch is 400 launches in a 40 s window; "launch s" is how long the launches took inside the window (the rest was idle, so the background growth lands on both arms alike). The counter also grows in the background (about 25 a second here, mostly h9kd's own leak), which is why every comparison is a pair with the order swapped. Per launch = (arm A delta minus arm B delta) / 400.

### Series 1: default flags (console inherited)

Started 2026-11-10 21:28:32 (real) and 21:41:52 (null); ended 21:55:13.

**Real run** (A = `spawn.exe 1`, B = `spawn.exe 0`):

| pair | arm | goes | before | after | delta | launch s | window s |
|---:|---|---|---:|---:|---:|---:|---:|
| 1 | A | first | 969414 | 970486 | 1072 | 11.23 | 40.0 |
| 1 | B | second | 970486 | 971538 | 1052 | 7.82 | 40.0 |
| 2 | A | second | 972597 | 973604 | 1007 | 13.91 | 40.0 |
| 2 | B | first | 971538 | 972597 | 1059 | 5.79 | 40.0 |
| 3 | A | first | 973604 | 974928 | 1324 | 12.05 | 40.0 |
| 3 | B | second | 974928 | 975915 | 987 | 5.60 | 40.0 |
| 4 | A | second | 976908 | 977923 | 1015 | 12.85 | 40.0 |
| 4 | B | first | 975915 | 976908 | 993 | 6.14 | 40.0 |
| 5 | A | first | 977923 | 979228 | 1305 | 12.48 | 40.0 |
| 5 | B | second | 979228 | 980365 | 1137 | 6.72 | 40.0 |
| 6 | A | second | 981142 | 982454 | 1312 | 12.20 | 40.0 |
| 6 | B | first | 980365 | 981142 | 777 | 4.94 | 40.0 |
| 7 | A | first | 982454 | 983670 | 1216 | 12.18 | 40.0 |
| 7 | B | second | 983670 | 984722 | 1052 | 6.57 | 40.0 |
| 8 | A | second | 985337 | 986720 | 1383 | 13.61 | 40.0 |
| 8 | B | first | 984722 | 985337 | 615 | 6.34 | 40.0 |
| 9 | A | first | 986720 | 987962 | 1242 | 13.65 | 40.0 |
| 9 | B | second | 987962 | 988809 | 847 | 5.32 | 40.0 |
| 10 | A | second | 989723 | 991104 | 1381 | 12.60 | 40.0 |
| 10 | B | first | 988809 | 989723 | 914 | 5.95 | 40.0 |

Per launch by pair: 1: 0.05; 10: 1.17; 2: -0.13; 3: 0.84; 4: 0.06; 5: 0.42; 6: 1.34; 7: 0.41; 8: 1.92; 9: 0.99

**Null run** (A = B = `spawn.exe 0`):

| pair | arm | goes | before | after | delta | launch s | window s |
|---:|---|---|---:|---:|---:|---:|---:|
| 1 | A | first | 991098 | 992140 | 1042 | 6.76 | 40.0 |
| 1 | B | second | 992140 | 992554 | 414 | 5.65 | 40.0 |
| 2 | A | second | 993628 | 994603 | 975 | 6.27 | 40.0 |
| 2 | B | first | 992554 | 993628 | 1074 | 6.91 | 40.0 |
| 3 | A | first | 994603 | 995436 | 833 | 6.24 | 40.0 |
| 3 | B | second | 995436 | 996294 | 858 | 5.61 | 40.0 |
| 4 | A | second | 997369 | 998213 | 844 | 5.15 | 40.0 |
| 4 | B | first | 996294 | 997369 | 1075 | 7.22 | 40.0 |
| 5 | A | first | 998213 | 998884 | 671 | 5.74 | 40.0 |
| 5 | B | second | 998884 | 999873 | 989 | 4.78 | 40.0 |
| 6 | A | second | 1000823 | 1001484 | 661 | 7.43 | 40.0 |
| 6 | B | first | 999873 | 1000823 | 950 | 4.11 | 40.0 |
| 7 | A | first | 1001485 | 1002541 | 1056 | 6.05 | 40.0 |
| 7 | B | second | 1002541 | 1003575 | 1034 | 6.18 | 40.0 |
| 8 | A | second | 1004193 | 1005214 | 1021 | 5.79 | 40.0 |
| 8 | B | first | 1003575 | 1004193 | 618 | 5.99 | 40.0 |
| 9 | A | first | 1005214 | 1006169 | 955 | 6.44 | 40.0 |
| 9 | B | second | 1006168 | 1007227 | 1059 | 5.55 | 40.0 |
| 10 | A | second | 1007588 | 1008806 | 1218 | 5.04 | 40.0 |
| 10 | B | first | 1007227 | 1007587 | 360 | 6.57 | 40.0 |

Per launch by pair: 1: 1.57; 10: 2.15; 2: -0.25; 3: -0.06; 4: -0.58; 5: -0.80; 6: -0.72; 7: 0.06; 8: 1.01; 9: -0.26

### Series 2: `CREATE_NO_WINDOW` (`spawn-nw.exe`, `bench-nw.exe`)

Started 2026-11-10 20:22:19 (real) and 20:35:40 (null); ended 20:49:00. (These ran with the first build of the programs, which had `CREATE_NO_WINDOW` hard-wired; it is the same code as the `-DNOWINDOW` build.)

**Real run:**

| pair | arm | goes | before | after | delta | launch s | window s |
|---:|---|---|---:|---:|---:|---:|---:|
| 1 | A | first | 875694 | 876757 | 1063 | 24.50 | 40.0 |
| 1 | B | second | 876757 | 877536 | 779 | 9.70 | 40.0 |
| 2 | A | second | 878584 | 879964 | 1380 | 23.43 | 40.0 |
| 2 | B | first | 877536 | 878584 | 1048 | 12.58 | 40.0 |
| 3 | A | first | 879964 | 880987 | 1023 | 25.38 | 40.0 |
| 3 | B | second | 880987 | 881896 | 909 | 7.74 | 40.0 |
| 4 | A | second | 882919 | 884224 | 1305 | 23.85 | 40.0 |
| 4 | B | first | 881897 | 882919 | 1022 | 12.56 | 40.0 |
| 5 | A | first | 884224 | 885347 | 1123 | 21.72 | 40.0 |
| 5 | B | second | 885347 | 886166 | 819 | 12.73 | 40.0 |
| 6 | A | second | 887235 | 888301 | 1066 | 23.41 | 40.0 |
| 6 | B | first | 886165 | 887235 | 1070 | 12.81 | 40.0 |
| 7 | A | first | 888301 | 889665 | 1364 | 23.79 | 40.0 |
| 7 | B | second | 889665 | 890620 | 955 | 12.13 | 40.0 |
| 8 | A | second | 891558 | 892633 | 1075 | 21.84 | 40.0 |
| 8 | B | first | 890620 | 891558 | 938 | 11.51 | 40.0 |
| 9 | A | first | 892633 | 894015 | 1382 | 23.62 | 40.0 |
| 9 | B | second | 894016 | 895091 | 1075 | 12.25 | 40.0 |
| 10 | A | second | 895535 | 896937 | 1402 | 23.77 | 40.0 |
| 10 | B | first | 895092 | 895535 | 443 | 12.50 | 40.0 |

Per launch by pair: 1: 0.71; 10: 2.40; 2: 0.83; 3: 0.28; 4: 0.71; 5: 0.76; 6: -0.01; 7: 1.02; 8: 0.34; 9: 0.77

**Null run:**

| pair | arm | goes | before | after | delta | launch s | window s |
|---:|---|---|---:|---:|---:|---:|---:|
| 1 | A | first | 896931 | 897948 | 1017 | 11.56 | 40.0 |
| 1 | B | second | 897948 | 898896 | 948 | 11.88 | 40.0 |
| 2 | A | second | 899575 | 900632 | 1057 | 12.62 | 40.0 |
| 2 | B | first | 898896 | 899575 | 679 | 10.15 | 40.0 |
| 3 | A | first | 900633 | 901664 | 1031 | 13.23 | 40.0 |
| 3 | B | second | 901664 | 902308 | 644 | 12.04 | 40.0 |
| 4 | A | second | 903177 | 904259 | 1082 | 12.26 | 40.0 |
| 4 | B | first | 902308 | 903177 | 869 | 13.04 | 40.0 |
| 5 | A | first | 904258 | 905140 | 882 | 12.89 | 40.0 |
| 5 | B | second | 905140 | 905975 | 835 | 9.63 | 40.0 |
| 6 | A | second | 906917 | 907856 | 939 | 11.34 | 40.0 |
| 6 | B | first | 905975 | 906916 | 941 | 12.44 | 40.0 |
| 7 | A | first | 907856 | 908492 | 636 | 12.83 | 40.0 |
| 7 | B | second | 908492 | 909516 | 1024 | 13.05 | 40.0 |
| 8 | A | second | 910465 | 911197 | 732 | 11.63 | 40.0 |
| 8 | B | first | 909516 | 910465 | 949 | 12.65 | 40.0 |
| 9 | A | first | 911197 | 912045 | 848 | 12.63 | 40.0 |
| 9 | B | second | 912045 | 913025 | 980 | 12.33 | 40.0 |
| 10 | A | second | 914039 | 914462 | 423 | 11.51 | 40.0 |
| 10 | B | first | 913025 | 914039 | 1014 | 12.67 | 40.0 |

Per launch by pair: 1: 0.17; 10: -1.48; 2: 0.94; 3: 0.97; 4: 0.53; 5: 0.12; 6: -0.01; 7: -0.97; 8: -0.54; 9: -0.33

### Summary of the batches

```
repro-batches.csv: 10 pairs of 400 launches; per launch (A-B): mean 0.71, sd 0.66, standard error 0.21, min -0.13, max 1.92; pairs above 0: 9
    A first in 5 pairs (mean 0.54), A second in 5 pairs (mean 0.87); total A 12257, total B 9433
null-batches.csv: 10 pairs of 400 launches; per launch (A-B): mean 0.21, sd 1.01, standard error 0.32, min -0.80, max 2.15; pairs above 0: 4
    A first in 5 pairs (mean 0.10), A second in 5 pairs (mean 0.32); total A 9276, total B 8431
repro-batches-nowindow.csv: 10 pairs of 400 launches; per launch (A-B): mean 0.78, sd 0.65, standard error 0.20, min -0.01, max 2.40; pairs above 0: 9
    A first in 5 pairs (mean 0.71), A second in 5 pairs (mean 0.85); total A 12183, total B 9058
null-batches-nowindow.csv: 10 pairs of 400 launches; per launch (A-B): mean -0.06, sd 0.79, standard error 0.25, min -1.48, max 0.97; pairs above 0: 5
    A first in 5 pairs (mean -0.01), A second in 5 pairs (mean -0.11); total A 8647, total B 8883
```

Pooled over both series: real run 20 pairs, mean 0.74 per launch, sd 0.64, standard error 0.14, 18 of 20 pairs above zero, 15 of 20 above 0.33. Null run 20 pairs, mean 0.08, sd 0.90, standard error 0.20, 9 above zero, 6 above 0.33 and 6 below -0.33. Real minus null: 0.67, standard error 0.25 (2.7). The real run against exactly one token per launch: 1.8 standard errors below. The null run shows the noise: single pairs of 400 launches swing by plus or minus one token a launch, and one null pair reads +2.15. The first-arm bias the spike measured (up to about half a token per launch) is visible too: in the real runs arm A scores higher when it goes second (0.87 and 0.85) than first (0.54 and 0.71). Read the batches as consistent with one token per launch, not as a measurement of it; the pool trace is the measurement.

## Kernel pool trace of the launches

`analysis/controlled-trace.ps1` starts the node's kernel logger with `analysis/toke.wprp` (process, image-load and pool events for tag `Toke`, with pool stacks), runs `bench.exe spawn.exe 1 0 150 20 1` (150 launches of each arm, arm A then arm B, each in a 20 s window), waits 75 seconds, stops the trace, and converts it with `tracerpt`. `analysis/TokeTrace/Program.cs.txt` (a small C# reader, kept as `.txt` so no build picks it up; build it in a directory outside the repository) ties each token allocation to the process creation that made it, and says whether the allocation was freed. Each launch's child token is the last `Toke` allocation by the creating thread before that creation's process-start event.

**Check on the matching, not assumed:** every allocation matched to a process creation has the same kernel call chain (two chain variants, differing only in deeper frames); of the 5,382 allocations in the whole trace that carry the main chain, none is left unmatched (0), and every one of the 16 kernel frames on that chain lies in `ntoskrnl.exe` (no third-party driver on the allocating stack). Four never-freed allocations made by `spawn.exe` processes were not matched to any creation as its child token; they are reported, not hidden.

**Default-flags trace** (`results/pool-trace-controlled-analysis.txt`; 118 s of trace, the launches ended at 20:54:52 and the trace ran 75 s more; "never freed" = the process ended at least 60 s before the trace stopped and the token was still allocated at the end):

| creator > child (the token counted is the child's) | launches | freed | never freed |
|---|---:|---:|---:|
| `bench.exe` > `spawn.exe 1` (arm A: starts one child, waits, exits) | 150 | 0 | **150** |
| `bench.exe` > `spawn.exe 0` (arm B: starts no child) | 150 | 150 | **0** |
| `spawn.exe 1` > `spawn.exe 0` (arm A's own child) | 150 | 150 | 0 |

Rule check over the same trace: processes that started a child which exited while they were still running: 150, never freed 150. Processes that started no child: 300, never freed 0.

**`CREATE_NO_WINDOW` trace** (`results/pool-trace-controlled-nowindow-analysis.txt`, launches ended 20:50:27, 75 s wait): arm A 150 launches, 150 never freed; arm B 150 launches, 0 never freed; arm A's child 150, 0 never freed; the console hosts the kernel started (450) all freed.

**So the spawning arm keeps one token per launch by the pool trace (150 of 150, twice), the control keeps none (0 of 150, twice), and the live-count batches are consistent with that (0.74 per launch) without being able to prove it.**

## Third-party kernel drivers, and the verdict

Every kernel module loaded during the measurements: 234, listed with company and signer in `results/kernel-modules-during-measurements.csv`. The list from the process-and-image trace at its start (all runs from 20:50 on) and the list read at 23:00 by `repro/modules.c` (`NtQuerySystemInformation` class 11) are the same 234 names. 211 are Microsoft's. The rest, which are all the **non-Microsoft kernel drivers running on this node** (all but one signed through Microsoft's hardware compatibility program, which does not make them Microsoft's code):

| File | Vendor | Version | What it is |
|---|---|---|---|
| `KfeCo11X64.sys` | Rivet Networks (Killer) | 10.3.9.33 | network filter and callout |
| `dbx.sys` | Dropbox | 1.0.25.0 | file system filter |
| `ProtonVPN.CalloutDriver.sys` | Proton AG | 1.0.5.0 | network callout; signed by Proton AG itself |
| `wireguard.sys` | WireGuard LLC | 0.10 | network tunnel |
| `DellInstrumentation.sys` | Dell (no version resource) | n/a | hardware instrumentation |
| `iaStorVD.sys`, `dump_iaStorVD.sys` | Intel | 20.2.13.1038 | storage (and its crash-dump copy) |
| `igdkmdn64.sys` | Intel | 32.0.101.7088 | graphics |
| `gna.sys` | Intel | 03.05.00.1578 | neural accelerator |
| `Netwtw14.sys` | Intel | 23.50.20.1 | Wi-Fi |
| `iaLPSS2_I2C_ADL.sys`, `iaLPSS2_GPIO2_ADL.sys` | Intel | 30.100.2531.31 | serial I/O |
| `TeeDriverW10x64.sys` | Intel | 2433.6.3.0 | management engine interface |
| `ipf_acpi.sys`, `ipf_cpu.sys` | Intel | 2.3.20306.4 | platform framework |
| `PRI-Driver.sys` | Intel | 01.00.1023.0 | performance monitoring provider |
| `ibtusb.sys` | Intel | 24.40.0.3 | Bluetooth |
| `RtsPer.sys` | Realtek | 10.0.26100.21374 | card reader |
| `e3k25cx21x64.sys` | Realtek | 1125.013.0424.2023 | Ethernet |
| `RTKVHD64.sys` | Realtek | 6.0.9780.1 | audio |

(`globmerger.sys` has no company string and is signed "Microsoft Windows"; `dump_diskdump.sys` and `dump_dumpfve.sys` are the crash-dump copies of Microsoft's own drivers.)

### Verdict: Windows defect: unconfirmed

**Unconfirmed.** The repro leaks with all of those drivers loaded. Showing that it leaks with no third-party kernel code in play needs one of these, none of which this run may take:

- Run `repro/spawn.c` and `bench.c` in a Windows guest of the same build with only Microsoft drivers (a Hyper-V virtual machine, or Windows Sandbox). Both features are disabled on this node (`Microsoft-Hyper-V`, `Containers-DisposableClientVM` are `Disabled`); enabling them needs a restart, which ends h9kd. Only `VirtualMachinePlatform` and the Linux subsystem are on, and a Linux guest says nothing about Windows.
- Or start this node with the vendor drivers above not loaded: that means disabling their services or uninstalling them and restarting, which the boundaries forbid.
- Or run the repro on a second machine at build 26300.9711 with a clean driver set (this one runs the Killer, Dropbox, ProtonVPN, WireGuard and Dell drivers, and Intel and Realtek hardware drivers).

What the run does show, short of that: the allocating stack of the leaked token has 16 frames, all in `ntoskrnl.exe`; the same stack frees the token for processes that started no child; a Windows tool that starts no child (`curl.exe`, `hostname.exe`, `whoami.exe`, `docker.exe`) frees nearly all of its tokens; the leak follows one rule across 147,000 processes in the daemon trace (next section). The spike's earlier check, that stopping the Killer and Dell services changed nothing, does not exclude their drivers, since stopping a service does not unload its driver. The holder of the extra reference was not identified (that needs a kernel debugger, which needs the node in debug mode and a restart).

## Launch chains under h9kd that keep a token

### How it was measured

`analysis/daemon-trace.ps1` ran the kernel trace (`analysis/toke-nostacks.wprp`: process, image-load and pool events for tag `Toke`, no stacks, since a stacked trace of an hour would be about 20 GB; the one-hour trace without stacks is 3.8 GB) on the running daemon from 2026-11-10 21:57:07 to 22:59:18 (the events span 3,692 s), while watching h9kd's children for a new `cmd.exe` (a gate run or an agent session start, `results/daemon-trace-watch.log`). The trace holds 154,073 process creations (149,335 of them below h9kd) and 2,225,541 `Toke` allocations. `analysis/TokeTrace` ties each creation to its child token, builds the process tree from the start-of-trace process list and the start events, and takes the tree below h9kd (pid 34684). Two things were left out of the daemon's tree, and both are reported:

- **This session.** The measuring session is itself a child of h9kd (`h9kd > cmd.exe 42564 > claude.exe`), so its subtree (288 processes) is left out of the daemon rate below and reported separately (`results/daemon-trace-my-session-analysis.txt`). Its settled never-freed tokens are 126; 2 of them are the same Git for Windows launcher left out above, so **124** are added back in the sentence after the rate (33,169 in all) and in row 10's share. It is the only agent session in the hour, and a measuring one at that, so its 124 are a floor for what an agent session costs, not an average.
- **The launcher.** The traced h9kd predates 54c44399, so it starts `C:\Program Files\Git\cmd\git.exe` (the Git for Windows launcher, which starts the real `ucrt64\bin\git.exe`). Every creation whose child image is `C:\Program Files\Git\cmd\git.exe` or `C:\Program Files\Git\bin\git.exe` is left out of the remaining rate: 47,095 tokens from `h9kd > cmd\git.exe` and 240 from `gh.exe > cmd\git.exe`, **47,335 in all, 58.9% of everything kept**. Nothing else is subtracted. In a build with 54c44399 the real git is started directly by h9kd, so rows 1a and 1b ("real git") below are the same rows either way (here it sits one level down, behind the launcher).

**What the trace did not cover (acceptance criterion 3 is not met).** No gate run and no agent session start occurred in the 62 minutes (h9kd started no `cmd.exe` after the trace began; the only `cmd.exe` was this session's, which was already running). The criterion asked for a kernel trace of the running daemon covering at least one of each, so rows 7 and 9 below have **no measured share**; they show "not measured", not 0%. They rest on a stand-in trace (below), which gives tokens per launch and no share, and on the code. The hour is also an idle one, dominated by the git ledger and mailbox sync, so shares will shift on a busy hour. Draft 3 re-takes the trace.

**The remaining rate.** Never freed = the child's process ended at least 60 s before the trace stopped and its token was still allocated at the end. Tokens never freed and made in the tree, less the launcher: 80,364 - 47,335 + 16 (never-freed allocations made by tree processes that are not any process's child token: 12 in real git, 3 in `gh`, 1 in `ssh-keygen`) = **33,045 over the 3,632 s that can settle: 9.10 tokens a second, 32,750 an hour**. 1,753 more were still pending (the process was alive or had ended less than 60 s before the end) and are in neither count. Adding this session's 124 gives **33,169 (9.13 a second, about 32,900 an hour)**; no other row's share moves by more than 0.1 point, so the shares below are of the 33,045 except row 10, which is of the 33,169.

**The rule, checked on every process in the daemon's tree** (`results/daemon-trace-analysis.txt`): of the processes whose token state had settled, those that started no child: 67,218 freed, 0 never freed. Those that started a child which exited while they were still running: 0 freed, **80,025 never freed**. A further 339 started a child but ended before it (or it never ended in the trace) and were all kept; they are the only place the rule is not simply read off the timing, and they are 0.4%.

### The chain table

Each row is a process whose token is kept: its parent, the spawner itself, the child the spawner starts and the arguments that make it start one. "Kept" counts settled tokens in the trace hour; share is of the 33,045 remaining (row 10: of 33,169, which adds this session). Full image paths; `PF` = `C:\Program Files`. Counts come from `results/daemon-trace-chains.tsv`; the split of real git into rows 1a and 1b comes from `results/daemon-trace-verify-commit.txt`.

| # | parent > spawner (its token is kept) | the child the spawner starts, and the arguments that make it start one | created | kept | share |
|---|---|---|---:|---:|---:|
| 1a | `PF\Git\cmd\git.exe` (launcher; **in a post-54c44399 build: `h9kd.exe`**) > `PF\Git\ucrt64\bin\git.exe` (real git), for a fetch or an ls-remote | `PF\Git\ucrt64\libexec\git-core\git.exe` with `rev-list --objects --stdin --not --exclude-hidden=fetch --all --quiet --alternate-refs` (2,335, git's connectivity check after a fetch), `maintenance run --auto --no-quiet --detach` (2,335), `remote-https <url>` (3,997: the 2,335 fetches plus about 1,662 ls-remotes over https). The real git that starts no child leaves nothing (38,154 freed) | 3,997 (the gits that start a helper) | 3,925 | 11.9% |
| 1b | the same real git, started for **`verify-commit`**: `git -c gpg.format=ssh -c gpg.ssh.allowedSignersFile=<temp file> verify-commit <sha>` (`GitLedgerCommitReader.cs:225`, `GitLedgerMessageTransport.cs:510`; counts in `results/daemon-trace-verify-commit.txt`) | `PF\Git\usr\bin\ssh-keygen.exe`, twice per verification: `-Y find-principals`, then `-Y verify` or `-Y check-novalidate` (12,506 launches, 2 x 6,253). The `ssh-keygen` itself starts nothing and its own token is freed (12,506 of 12,506); the **git that started it** is the token kept here: 6,110 of 6,253 settled, 143 still pending | 6,253 | 6,110 | 18.5% |
| 2 | `PF\Git\ucrt64\bin\git.exe` > `PF\Git\ucrt64\libexec\git-core\git.exe` (git's `remote-https` dispatcher) | `PF\Git\ucrt64\libexec\git-core\git-remote-https.exe <remote> <url>` | 8,667 | 3,925 | 11.9% |
| 3 | `PF\Git\ucrt64\libexec\git-core\git.exe` > `PF\Git\ucrt64\libexec\git-core\git-remote-https.exe` | `PF\Git\usr\bin\sh.exe -c "'C:\Program Files\GitHub CLI\gh.exe' auth git-credential get"` (and `store`): git runs the owner's `credential.helper = !'C:\Program Files\GitHub CLI\gh.exe' auth git-credential` through sh | 3,997 | 3,553 | 10.8% |
| 4 | `git-remote-https.exe` > `PF\Git\usr\bin\sh.exe` | `PF\GitHub CLI\gh.exe auth git-credential get` (3,609) and `... store` (3,609) | 7,218 | 7,108 | 21.5% |
| 5 | `PF\Git\usr\bin\sh.exe` > `PF\GitHub CLI\gh.exe` (the helper) | `C:\Windows\System32\tzutil.exe /g` (7,218) | 7,218 | 7,108 | 21.5% |
| 6 | `C:\Users\<user>\.hall9k\bin\h9kd.exe` > `PF\GitHub CLI\gh.exe` (h9kd's own `gh` calls: `api user -q .login`, `api graphql`, `pr list`, `repo view`, `pr view`, `auth token`) | `C:\Windows\System32\tzutil.exe /g` (2,835: two per `api`, `pr list` and `repo view` call, three per `pr view`, one per `auth token`) and, for `pr view` without `--repo`, `PF\Git\cmd\git.exe remote -v` and `... config --get-regexp ^remote\..*\.gh-resolved$` (126 each) | 1,365 | 1,300 | 3.9% |
| 7 | `C:\Users\<user>\.hall9k\bin\h9kd.exe` > `C:\Windows\System32\cmd.exe`, for **gate commands** (`VerificationRunner.cs:1458`, `cmd.exe /c "<gate command>"`) | the gate's own program: `dotnet.exe`, then `testhost.exe` and more `dotnet.exe` below it (stand-in below). **Not in the daemon trace** | not measured | not measured | not measured |
| 8 | `h9k.exe` (the CLI) > `C:\Windows\System32\cmd.exe`, for **worktree shells** (`WorktreeShell.cs:200`) | the run skill's command. **Not started by h9kd**: only `h9k task run-local` (a person at a terminal) calls it; the daemon's `LocalLaunchSweepEngine` only stops launches. Not traced | 0 | 0 | 0% |
| 9 | `C:\Users\<user>\.hall9k\bin\h9kd.exe` > `C:\Windows\System32\cmd.exe`, for **agent sessions** (`WindowsProcessManager.cs:28`, `cmd.exe /c ""claude" -p ... < in > out 2> err"`) | `C:\Users\<user>\.local\bin\claude.exe`. **Start not in the daemon trace** (stand-in below); the session's own children are in row 10 | not measured | not measured | not measured |
| 10 | `claude.exe` > `PF\Git\bin\bash.exe` > `PF\Git\usr\bin\bash.exe` > `PF\Git\usr\bin\bash.exe` (**bash > bash**, an agent session's Bash tool) | each Bash tool call: `claude.exe` starts the launcher `bin\bash.exe`, which starts `usr\bin\bash.exe`, which starts a nested `usr\bin\bash.exe -c` for the command. Sample is this session's own subtree (35 Bash calls in the hour): launcher 35 of 35 kept, `bin\bash.exe > usr\bin\bash.exe` 35 of 35, nested `usr\bin\bash.exe > usr\bin\bash.exe` 34 of 35: **3 tokens per Bash tool call**. `claude.exe > cmd.exe /d /s /c "chcp 65001 & pwsh ..."` (the PowerShell tool): `cmd.exe` 9 of 10 kept, `cmd.exe > pwsh.exe` 7 of 10: about 1.6 per PowerShell call | 125 in this session | 120 | 0.4% (of 33,169; this one session) |
| - | `h9kd.exe` > `PF\Git\cmd\git.exe` (the launcher; **left out**: 54c44399) | the real git (the child of the launcher; rows 1a and 1b) | 48,179 | 47,095 | left out |
| - | `PF\GitHub CLI\gh.exe` > `PF\Git\cmd\git.exe` (the launcher; **left out**) | the real git, for `gh pr view` without `--repo` | 252 | 240 | left out |

The programs at the bottom of a chain keep nothing, because they start nothing: `h9kd > ssh-keygen.exe` (1,449 launches), `gh > tzutil.exe` (10,053) and `real git > ssh-keygen.exe` (12,506) each have 0 never freed. That counts the token of the `ssh-keygen` or `tzutil` itself. It does not count the token of the process that started it: the real git above `ssh-keygen` keeps its own (row 1b, 18.5%), as the `gh` above `tzutil` does (rows 5 and 6).

Shares of the 33,045: real git 30.4 (fetch and ls-remote 11.9, `verify-commit` 18.5), git's dispatcher 11.9, `git-remote-https` 10.8, `sh` 21.5, the helper's `gh` 21.5, h9kd's own `gh` 3.9. They sum to 100.0 (the 16 allocations that are not a child token are 0.05%). Rows 4 and 5 (the helper's `sh` and `gh`) are **43.0%**; row 3 (`git-remote-https`) keeps its token only because it started that `sh`, so the credential-helper chain is **53.8% together** (rows 3, 4, 5). With row 1b (`verify-commit`, 18.5%) the two chains h9kd can avoid are **72.3%**. This session's other 4 settled kept tokens (two real git, one `git-core`, one `gh`) are the same shapes as rows 1, 2 and 6; they are in the 33,169 and in no row's count. One credentialed remote call keeps 7 tokens: real git 1, dispatcher 1, `git-remote-https` 1, two `sh`, two `gh` (the `get` and the `store`).

### What each row can avoid, and what avoiding it changes

Measured arms are in `results/avoidance-trace-analysis.txt` (`analysis/avoidance-trace.ps1`): 20 launches of each arm in one trace, started like the runner starts them (no `CREATE_NO_WINDOW`, output redirected).

| Row | Can h9kd avoid the nested launch? | What it would change |
|---|---|---|
| 3, 4, 5: credential helper | **Yes.** The nested launch comes from the owner's `~/.gitconfig` helper, not from h9kd code, but h9kd decides how it starts git: `GIT_CONFIG_COUNT=2`, `GIT_CONFIG_KEY_0=credential.helper` with an empty value, `GIT_CONFIG_KEY_1=http.extraHeader` with `Authorization: Basic <base64 of x-access-token:<token>>`, the token read once from `gh auth token`. The token stays out of the command line. The traced arm used the unscoped key, which git sends to every HTTPS host; that was safe only because the call contacted nothing but github.com. A production start must use the URL-scoped key `http.https://github.com/.extraHeader` (Draft 1); the scoped key was not traced, and it changes the configuration git reads, not the processes it starts. Measured on `git ls-remote` of a private repository: **7 tokens kept per call with the helper chain (real git 1, dispatcher 1, `git-remote-https` 1, two `sh`, two `gh`), 5 with `TZ=UTC` set (the helper's `gh` then starts no `tzutil` and its token is freed, 20 of 20), 2 with the helper off and the header in the environment** (real git and its dispatcher; `git-remote-https` starts nothing and is freed, 20 of 20) | removes rows 3, 4 and 5: 53.8% of the remaining rate (5 of the 7 tokens a credentialed call keeps; real git and its dispatcher still keep theirs). Costs: h9kd has to read and hold the token, and has to refresh it when it changes |
| 6: h9kd's `gh` | **Partly.** `tzutil /g` comes from `gh`'s own library (`thlib/go-timezone-local`, string found in `gh.exe` 2.83.1); with `TZ` set in the environment `gh` does not start it (fake-`tzutil` counter: 2 starts per `api`, `repo view` or `pr view --repo` call without `TZ`, 0 with it). Passing `--repo` stops the `git remote -v` and `config` children. Not fully: with `TZ` set, 13 of 20 `gh api` runs still kept their token (11 of 20 for `gh --version`, 44 of 80 across four commands) although they start nothing and `curl.exe` and `docker.exe` free all of theirs; that residual in `gh.exe` is not explained | at best halves row 6 (3.9%, so about 2 points of the rate). Calling the REST and GraphQL endpoints from h9kd directly with the token would remove the launch altogether |
| 1a, 1b, 2: real git's own helpers | **Row 1a and row 2: no, short of making fewer remote git calls.** `rev-list` is git's connectivity check after every fetch; `remote-https` is how git talks https; `maintenance run --auto --detach` is started after a fetch by the same git process that already started `rev-list`, so switching it off would not free that git's token. The calls that start no child (`rev-parse`, `for-each-ref`, 38,154 an hour here) keep nothing. **Row 1b (`verify-commit`): yes.** The nested `ssh-keygen` launches are inside git, but h9kd chooses whether to run `git verify-commit` at all. It can skip a commit it has already verified (a commit's signature does not change; the traced command lines are cut before the sha, so how often the same commit was verified again was not measured), or call `ssh-keygen -Y find-principals` and `-Y verify` itself on the commit's signature, as it already runs `ssh-keygen` directly for key reads (1,449 launches, 1,447 freed, none keeps a token because `ssh-keygen` starts nothing). `git verify-commit` does more than the two calls (it extracts the signed payload and signature), so running `ssh-keygen` directly means reproducing that step, which is the cost | rows 1a and 2 (23.8% together) fall only with the number of fetch and ls-remote calls: 3,609 credentialed calls an hour here (the ledger and mailbox sync of nine repositories every few seconds), so a longer sync interval or one fetch per repository per cycle is the lever. **Row 1b is 18.5% of the rate** (6,110 tokens an hour here, one per verification): the second-largest chain h9kd can avoid, after the credential helper (Draft 5). Avoiding rows 3, 4, 5 and 1b together removes 72.3% |
| 7, 9: `cmd.exe` for gates and sessions | **In principle yes, not worth it.** `cmd.exe /c` is there so that a returned pid stays alive for the command's whole life and redirection is native (the comment in `WindowsProcessManager.cs`). One token per launch. Stand-in: 1 kept for the session wrapper, 1 for the gate wrapper | the wrapper alone is one token per gate run and per session; no share is claimed, because no gate run or session start was traced. The real cost of a gate is below `cmd.exe`: `dotnet.exe` and the test host start children and keep theirs (stand-in below), which h9kd does not control |
| 8: worktree shells | Not h9kd's | none; same shape as row 7 |
| 10: bash > bash | **One of three.** The nested `usr\bin\bash.exe > usr\bin\bash.exe` and the PowerShell tool's `cmd.exe` are Claude Code's own. The launcher `bin\bash.exe` is chosen through `CLAUDE_CODE_GIT_BASH_PATH`, which `GitForWindowsLauncher.ApplyToCurrentProcess` already sets (to `bin\bash.exe`); pointing it at `usr\bin\bash.exe` would drop the launcher's token and the launcher > `usr\bin\bash` pair, 2 of 3 per Bash call. Whether Claude Code works with `usr\bin\bash.exe` there was not tested | 35 to 70 tokens an hour per busy session |

**Stand-in for gate and session** (`analysis/standin-gate-session.ps1`, `results/standin-gate-session-analysis.txt`; 23:11 to 23:17): a helper started, with the same `ProcessStartInfo` shape as the code (`cmd.exe`, `/c "<command>"`, `UseShellExecute=false`, `CreateNoWindow=true`, `MSBUILDDISABLENODEREUSE=1` for the gate), first a session wrapper running `claude --version` with the session's `< in > out 2> err` redirection, then a gate wrapper running `dotnet test tests/Hall9k.Tests --filter "FullyQualifiedName~GitForWindowsLauncherTests"` (the one scoped test class this branch's rules allow). Result: both `cmd.exe` wrappers kept their token (2 of 2); `cmd.exe > dotnet.exe` 1 of 1 kept; `dotnet.exe > dotnet.exe` 2 of 6; `dotnet.exe > testhost.exe` 1 of 1; `claude.exe` (which started nothing) freed. A real gate runs many more test hosts and the tests start git: that number was not measured.

### Code sites that start processes h9kd launches

| Site | Program started | Starts a child that outlives... |
|---|---|---|
| `ExternalProcess.cs:136,180` (the shared runner: ledger, GitHub, key tool callers) | `git`, `gh`, `ssh-keygen` by name | git, gh (rows 1-6); ssh-keygen does not. The two `git verify-commit` starts (`GitLedgerCommitReader.cs:225`, `GitLedgerMessageTransport.cs:510`) go through this runner and are row 1b: git keeps its token because it starts `ssh-keygen` |
| `GitLedger.cs:755`, `GitLedgerMessageTransport.cs:793`, `GitWorktreeManager.cs:1291`, `CheckoutCleanliness.cs:91`, `ReviewEngine.cs:8350,8407`, `TestScopeResolver.cs:305`, `VerificationRunner.cs:1897`, `PullRequestOpener.cs:1149` | `git` (or `gh`, `PullRequestOpener`) | rows 1 to 6 |
| `VerificationRunner.cs:1458` | `cmd.exe /c "<gate command>"` | row 7 |
| `WindowsProcessManager.cs:28` | `cmd.exe /c ""claude" ..."` | row 9, then row 10 below the session |
| `WindowsProcessManager.cs:139` | `powershell.exe -NoProfile -Command Get-CimInstance Win32_Process ...` (a process-tree query) | not seen in the trace; not exercised in the idle hour |
| `InstalledCliTaskCommands.cs:79` | `h9k.exe task revise` / `task publish` | `h9k.exe` starts git and gh in turn; not seen in the hour |
| `NodeBootstrap.cs:223` | tools named by the caller (git) | as git |
| `WorktreeShell.cs:200`, `AdHocGateRunner.cs:166`, `TaskVerifyCommand.cs:547`, `HeadlessLaunch.cs:262` | `cmd.exe` | **CLI only**, never started by h9kd |

## Windows Feedback Hub report (ready to file)

**Title:** A process that starts a child and is still running when the child exits leaves its access token (pool tag `Toke`) allocated forever

**Suggested category:** Developer Platform, or Security and Privacy > Kernel (the choice is the owner's).

**Windows version:** Windows 11 Pro, version 26H2, OS build 26300.9711 (`ver` shows 10.0.26300.9711), x64. Intel Core i7-14700, 29.7 GB, Dell XPS 8960. Virtualization-based security with hypervisor-enforced code integrity running.

**What happens:** every process that starts a child process and is still running when that child exits keeps one kernel access token (pool tag `Toke`, with its `SeTl`, `SeAt`, `SeTd`, `SeDt` companions) allocated after the process itself has exited. It is never freed; only a restart frees it. A process that starts no child frees its token a second or two after it exits. On this machine h9kd, a long-running service whose process tree starts about 42 programs a second, leaked about 9 tokens a second (about 32,000 an hour) and ran the paged pool up by roughly 0.75 GB a day until a restart.

**Expected:** the token of an exited process is freed when the last reference to it is released, whether or not the process had a child.

**Repro (no third-party code; full source in `FINDINGS.md` of this repository, `docs/spikes/windows-token-leak-origin/repro/`):** build `spawn.c` and `bench.c` with `clang -O2 -municode -o spawn.exe spawn.c` and `clang -O2 -municode -o bench.exe bench.c` (any C compiler works). `spawn.exe 0` returns at once; `spawn.exe 1` starts one child (`spawn.exe 0`), waits for it, and returns. From an elevated prompt run `bench.exe spawn.exe 1 0 400 40 10 A`, and for comparison `bench.exe spawn.exe 0 0 400 40 10 A`. To see exactly which tokens are kept, take a kernel trace with the process, image-load and pool keywords restricted to pool tag `Toke` (`wpr` with a `PoolTags` entry for `Toke`) around 150 launches of each arm, wait 75 seconds, and stop it: match each token allocated by the creating thread just before a process-start event to its free event.

**Numbers (this machine):**

- Pool trace, 150 launches of each arm, then 75 s of waiting: `spawn.exe 1` child tokens never freed: **150 of 150** (and 150 of 150 in a second trace); `spawn.exe 0` child tokens never freed: **0 of 150** (0 of 150 in the second); the child that `spawn.exe 1` itself started: 0 of 150 never freed. Over 147,000 processes in a one-hour trace of a busy service tree: started no child, 67,218 freed and 0 never freed; started a child that exited while it ran, 0 freed and 80,025 never freed.
- Live count of `Toke` (allocations minus frees from `NtQuerySystemInformation(SystemPoolTagInformation)`), 10 pairs of 400 launches with the arm order swapped each pair, 40 s per batch: arm A minus arm B per launch, series 1 mean 0.71 (standard error 0.21), series 2 (children created with `CREATE_NO_WINDOW`) 0.78 (0.20); the same program in both arms (null run), series 1 0.21 (0.32) and series 2 -0.06 (0.25). The background growth is about 25 tokens a second, so single pairs swing by plus or minus one a launch; the trace above is the exact measurement.
- The allocating stack of the leaked token is 16 frames, all in `ntoskrnl.exe` (no other driver on it).

**Third-party kernel drivers loaded while this was measured (so it is not shown to be free of them):** Rivet Networks (Killer) `KfeCo11X64.sys` 10.3.9.33; Dropbox `dbx.sys` 1.0.25.0; Proton AG `ProtonVPN.CalloutDriver.sys` 1.0.5.0; WireGuard `wireguard.sys` 0.10; Dell `DellInstrumentation.sys`; Intel `iaStorVD.sys`, `igdkmdn64.sys`, `gna.sys`, `Netwtw14.sys`, `iaLPSS2_I2C_ADL.sys`, `iaLPSS2_GPIO2_ADL.sys`, `TeeDriverW10x64.sys`, `ipf_acpi.sys`, `ipf_cpu.sys`, `PRI-Driver.sys`, `ibtusb.sys`; Realtek `RtsPer.sys`, `e3k25cx21x64.sys`, `RTKVHD64.sys`. Stopping the Killer and Dell services earlier did not change the leak rate (which does not unload their drivers).

**Attach:** `spawn.c`, `bench.c`, `results/pool-trace-controlled-analysis.txt`, `results/kernel-modules-during-measurements.csv`, and, if a trace file is wanted, a fresh `wpr` capture on the same build.

## Proposed follow-ups (drafts; each is recorded as an idea in the project backlog, none is a task yet)

**Draft 1: Start daemon git calls without the credential-helper chain.**
Objective: on Windows, every git call h9kd starts that talks to a remote runs with the credential helper off and the token supplied through git's environment configuration, so the `git-remote-https > sh > gh > tzutil` chain stops keeping four to five tokens per call.
Acceptance criteria:
- The runner and every daemon git start site that can reach a remote start git with `GIT_CONFIG_COUNT`, `credential.helper` set empty and the **URL-scoped** header key `http.https://github.com/.extraHeader` (never the unscoped `http.extraHeader`, which git sends to every HTTPS host, so a project with a non-GitHub remote would receive the owner's GitHub token) carrying the token from `gh auth token`; the token appears on no command line and in no log. A test pins that no environment h9kd builds contains the unscoped key, and a project whose remote is not on github.com gets no header.
- A token that is missing or rejected is refreshed once and the call retried; a call with no token falls back to today's behaviour.
- A kernel trace of 20 `git ls-remote` calls of a private repository through the runner, with the scoped key, shows at most 2 tokens kept per call (today 7), and no `sh.exe` or `gh.exe` started by `git-remote-https` (the traced arm in this spike used the unscoped key).
- A trace of an hour of the running daemon shows `git-remote-https > sh` and `sh > gh` at zero.
- The unit tests cover the environment the runner builds, and non-Windows behaviour is unchanged.

**Draft 2: Start `gh` with `TZ` set and `--repo` named.**
Objective: stop the avoidable nested launches in h9kd's own `gh` calls (`tzutil /g`, `git remote -v`, `git config --get-regexp`).
Acceptance criteria:
- Every `gh` the runner starts has `TZ` set (UTC), and every `gh pr view` names `--repo`.
- A trace of 20 calls of each `gh` command h9kd makes shows no `tzutil.exe` and no `git.exe` child, and records how many tokens each still keeps (with `TZ` set, 13 of 20 `gh api` runs kept theirs in this run; the residual is expected to stay and is reported, not claimed away).
- No output of `gh` that h9kd parses changes (the JSON fields h9kd reads are timestamps in UTC already).

**Draft 3: Re-take the daemon trace over a busy window and measure the session's Bash launcher.**
Objective: cover what this run could not: a gate run and an agent session start in a kernel trace of the running daemon, and what `CLAUDE_CODE_GIT_BASH_PATH=...\usr\bin\bash.exe` changes.
Acceptance criteria:
- A kernel trace of the daemon, from `analysis/daemon-trace.ps1`, that contains at least one gate run and one agent session start and the gate's `cmd.exe` exit, on a build with 54c44399, with the chain table above re-filled (rows 7, 9 and 10 with real counts and measured shares, replacing "not measured"). This is the acceptance criterion this run did not meet.
- A trace of 20 Bash tool calls of a throw-away Claude Code session with `CLAUDE_CODE_GIT_BASH_PATH` at `bin\bash.exe` and at `usr\bin\bash.exe`, with tokens kept per call for each; the draft records whether the Bash tool works with `usr\bin\bash.exe`.
- The daemon's node and h9kd process id and start time are the same before and after.

**Draft 4: Settle whether this is a Windows defect (owner action; needs a restart).**
Objective: run `repro/spawn.c` and `bench.c` where no third-party kernel driver is loaded and record the verdict as confirmed, or name the driver that stops it.
Acceptance criteria:
- The repro is run in a Windows guest of build 26300.9711 (Windows Sandbox or a Hyper-V virtual machine, both need a restart of this node to enable) or on a second machine, with the driver list recorded; at least 5 pairs of 400 launches and one pool trace of 100 launches of each arm followed by 60 s.
- If the guest leaks, the Feedback Hub report above is filed and its link recorded; if it does not, the drivers are added back one vendor at a time (Killer, Dropbox, ProtonVPN, WireGuard, Dell) until the leak returns, and the vendor is named.
- Nothing about h9kd's process is touched beforehand: the restart is scheduled by the owner when no project is mid-run.

**Draft 5: Stop starting `git verify-commit` for commits h9kd has already verified, or verify with `ssh-keygen` directly.**
Objective: on Windows, remove the 18.5% of the remaining token leak that comes from the real `git` h9kd starts for `verify-commit` (`GitLedgerCommitReader.cs:225`, `GitLedgerMessageTransport.cs:510`), which keeps its token because it starts `ssh-keygen` twice.
Acceptance criteria:
- A count, from a trace of the running daemon with the full command line captured (this run's lines were cut before the sha), of how many `verify-commit` calls an hour verify a commit that was already verified; the draft records it before choosing between caching and calling `ssh-keygen` directly.
- The chosen change keeps the check as strict as today: the `find-principals` guard in `GitLedgerMessageTransport` and the exit-code handling in `GitLedgerCommitReader` behave the same, with tests on a forged signature and on an unsigned commit.
- A kernel trace of an hour of the running daemon shows `git.exe > ssh-keygen.exe` at zero, or at no more than the number of first-time verifications; the tokens kept per verification fall from 1 to 0 for a cached commit.

## Limits and what was not done

- **Gate run and agent session start were not in the daemon trace** (above), so acceptance criterion 3 (a kernel trace of the running daemon covering at least one of each) is **not met**. Rows 7 and 9 carry no measured share ("not measured"); they rest on the stand-in (tokens per launch, one test class) and on the code, and row 10 on this session's own subtree (0.4% of the rate with this session included, one session in an idle hour).
- **The root cause is not identified**, only the condition and its rule. The holder of the extra reference needs a kernel debugger. The `gh.exe` residual (kept with no child, 55% of the time, not for `docker.exe` or `curl.exe`) is not explained either, and it means the rule "kept if a child exited first" is a very good description of what h9kd's tree does, not a complete law.
- **The 62-minute hour is not a typical hour.** Row shares will move when sessions and gates run; the chain table is for the ledger-sync-dominated idle hour.
- **Time is wall-clock EST** on one node. Pairs of 400 launches have a noise of about plus or minus one token a launch; conclusions that rest on them are labelled.
- **Installed on this node:** LLVM MinGW (user scope, winget). No other change; no driver, service, h9kd or setting was touched, and no task was created (the follow-up drafts are ideas in the project backlog).

## Files

`repro/spawn.c`, `bench.c`, `modules.c` (kernel module list), `faketzutil.c` (the counter used for the `TZ` check). `analysis/toke.wprp` and `toke-nostacks.wprp` (the trace profiles), `controlled-trace.ps1`, `daemon-trace.ps1`, `standin-gate-session.ps1`, `avoidance-trace.ps1`, `gh-probe-trace.ps1`, `go-probe-trace.ps1`, `summarize-batches.ps1`, `TokeTrace/Program.cs.txt` and `TokeTrace.csproj.txt` (copy both into an empty directory outside the repository, drop the `.txt`, run `dotnet build -c Release -o out`, then `dotnet out/TokeTrace.dll <trace.csv> --root <pid> --by-cmd --stacks`; the options are listed at the top of `Program.cs.txt`; the CSV comes from `tracerpt <trace.etl> -of CSV -o <trace.csv>`). `results/`: every batch CSV, the analysis output of every trace, the chain tables as TSV, the `verify-commit` split of row 1, the kernel module list, the watcher log. The raw trace files (4 GB for the daemon hour) stay on the node under `%TEMP%\tokeleak` and are not committed. Because this repository is public, the names of the private repositories that h9kd syncs appear in `results/` as `<org-a>/<private-repo-1>` and so on (the numbers only tell repositories apart), the Windows user name as `<user>`, and a Claude Code shell-snapshot file name as `<id>`; `analysis/avoidance-trace.ps1` takes the private repository to read as `-Remote`. Counts and shares are unchanged by the substitution.
