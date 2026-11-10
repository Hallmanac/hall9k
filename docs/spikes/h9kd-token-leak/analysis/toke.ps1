Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices;
public static class TokeQ {
  [DllImport("ntdll.dll")] static extern int NtQuerySystemInformation(int c, IntPtr b, int l, out int r);
  static int len = 4*1024*1024;
  public static long Live() {
    while (true) {
      IntPtr buf = Marshal.AllocHGlobal(len); int r;
      int st = NtQuerySystemInformation(22, buf, len, out r);
      if (st == unchecked((int)0xC0000004)) { Marshal.FreeHGlobal(buf); len = Math.Max(r, len*2); continue; }
      try {
        if (st != 0) throw new Exception("NtQSI 0x"+st.ToString("X"));
        int n = Marshal.ReadInt32(buf, 0);
        for (int i = 0; i < n; i++) { int o = 8 + i*40; if (Marshal.ReadInt32(buf, o) == 0x656B6F54) return (long)(Marshal.ReadInt32(buf,o+4) - Marshal.ReadInt32(buf,o+8)); }
        return -1;
      } finally { Marshal.FreeHGlobal(buf); }
    }
  }
}
"@
function Measure-Batch([string]$Label, [int]$N, [scriptblock]$Launch) {
  $a = [TokeQ]::Live(); $t = Get-Date
  for ($i = 0; $i -lt $N; $i++) { & $Launch }
  $sec = ((Get-Date) - $t).TotalSeconds
  $b = [TokeQ]::Live()
  [pscustomobject]@{ Label=$Label; N=$N; Before=$a; After=$b; Delta=$b-$a; Sec=[math]::Round($sec,1); PerLaunch=[math]::Round(($b-$a)/$N,2) }
}
