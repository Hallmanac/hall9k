/*
 * modules.c - prints the full path of every module loaded in kernel space right now
 * (NtQuerySystemInformation class 11, SystemModuleInformation), one per line, with its base address.
 * No Hall9k code. Build: clang -O2 -o modules.exe modules.c
 */
#include <windows.h>
#include <stdio.h>
#include <stdlib.h>

typedef LONG (NTAPI *NtQuerySystemInformationFn)(ULONG, PVOID, ULONG, PULONG);

#pragma pack(push, 8)
typedef struct {
    PVOID Section, MappedBase, ImageBase;
    ULONG ImageSize, Flags;
    USHORT LoadOrderIndex, InitOrderIndex, LoadCount, OffsetToFileName;
    UCHAR FullPathName[256];
} MODULE;
#pragma pack(pop)

int main(void)
{
    NtQuerySystemInformationFn query = (NtQuerySystemInformationFn)GetProcAddress(
        GetModuleHandleW(L"ntdll.dll"), "NtQuerySystemInformation");
    ULONG cap = 1u << 20, ret = 0;
    BYTE *buf = malloc(cap);
    LONG st;
    while ((st = query(11, buf, cap, &ret)) == (LONG)0xC0000004) {
        cap = ret + 65536;
        buf = realloc(buf, cap);
    }
    if (st != 0) {
        fprintf(stderr, "NtQuerySystemInformation(11) failed: 0x%08lX\n", (unsigned long)st);
        return 1;
    }
    ULONG count = *(ULONG *)buf;
    MODULE *m = (MODULE *)(buf + 8);
    for (ULONG i = 0; i < count; i++)
        printf("%p  %s\n", m[i].ImageBase, (const char *)m[i].FullPathName);
    return 0;
}
