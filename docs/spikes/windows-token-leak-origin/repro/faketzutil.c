/* Build: clang -O2 -o tzutil.exe faketzutil.c, then put its directory first on PATH.
 * fake tzutil.exe: counts how often it is started (appends one line to the file named by TOKELEAK_LOG) and answers like tzutil /g */
#include <windows.h>
#include <stdio.h>
int main(void)
{
    char log[MAX_PATH];
    if (GetEnvironmentVariableA("TOKELEAK_LOG", log, sizeof log)) {
        FILE *f = fopen(log, "a");
        if (f) { fputs("tzutil started\n", f); fclose(f); }
    }
    puts("Eastern Standard Time");
    return 0;
}
