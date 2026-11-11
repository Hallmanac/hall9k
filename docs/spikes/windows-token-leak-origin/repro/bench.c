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
