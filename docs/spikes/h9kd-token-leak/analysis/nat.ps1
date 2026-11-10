param([string]$Exe="C:\Program Files\Git\cmd\git.exe", [string]$ChildArgs="--version", [int]$N=300, [int]$Pairs=3, [string]$Variant="plain")
. "$PSScriptRoot/toke.ps1"
Add-Type -TypeDefinition @"
using System; using System.Runtime.InteropServices; using System.Text;
public static class NL {
  [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)] public struct SI { public int cb; public string r, d, t; public int x,y,xs,ys,xc,yc,fill,flags; public short show, res2; public IntPtr res2p, hin, hout, herr; }
  [StructLayout(LayoutKind.Sequential)] public struct PI { public IntPtr hp, ht; public int pid, tid; }
  [StructLayout(LayoutKind.Sequential)] public struct SA { public int len; public IntPtr sd; public int inh; }
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inh, uint flags, IntPtr env, string cwd, ref SI si, out PI pi);
  [DllImport("kernel32.dll", SetLastError=true, CharSet=CharSet.Unicode)] static extern IntPtr CreateFileW(string p, uint acc, uint share, ref SA sa, uint disp, uint attrs, IntPtr tmpl);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);
  static IntPtr nul = IntPtr.Zero;
  public static void Run(string cmdline, bool inherit, bool useStd, uint flags) {
    var si = new SI(); si.cb = Marshal.SizeOf(typeof(SI));
    if (useStd) { if (nul == IntPtr.Zero) { var sa = new SA{len=Marshal.SizeOf(typeof(SA)), inh=1}; nul = CreateFileW("NUL", 0xC0000000, 3, ref sa, 3, 0x80, IntPtr.Zero); } si.flags = 0x100; si.hin = nul; si.hout = nul; si.herr = nul; }
    PI pi; var sb = new StringBuilder(cmdline);
    if (!CreateProcessW(null, sb, IntPtr.Zero, IntPtr.Zero, inherit, flags, IntPtr.Zero, null, ref si, out pi)) throw new Exception("CreateProcess "+Marshal.GetLastWin32Error());
    CloseHandle(pi.ht); WaitForSingleObject(pi.hp, 60000); CloseHandle(pi.hp);
  }
}
"@
$cl = "`"$Exe`" $ChildArgs"
$inh = $true; $std = $true; $fl = [uint32]0x400
switch ($Variant) { "plain" {} ; "noinherit" { $inh=$false; $std=$false }; "nowindow" { $fl = 0x400 -bor 0x08000000 }; "detached" { $fl = 0x400 -bor 0x8 } }
$res = 1..$Pairs | % {
  $c = Measure-Batch "cond" $N { [NL]::Run($cl, $inh, $std, $fl) }
  $k = Measure-Batch "idle" 1 { Start-Sleep -Milliseconds ([int]($c.Sec*1000)) }
  [pscustomobject]@{ Pair=$_; LaunchDelta=$c.Delta; IdleDelta=$k.Delta; Sec=$c.Sec; ExcessPerLaunch=[math]::Round(($c.Delta-$k.Delta)/$N,2) }
}
$res | ft -auto | Out-String
"variant=$Variant exe=$Exe mean excess/launch: " + [math]::Round(($res | measure ExcessPerLaunch -Average).Average,2)
