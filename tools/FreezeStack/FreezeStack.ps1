param([string]$ProcessName = "valheim", [int]$Snapshots = 3, [int]$Frames = 14)
# Names what a frozen game's main thread is running.
# Each snapshot pauses the main thread for a moment (as a debugger does), reads where it is and
# its stack, and asks the game's own Mono runtime which C# method each code address belongs to
# (mono_pmip, Mono's debugger helper), through a short thread started inside the game process.
# The stack is read raw, so a frame that already returned can show up between the real ones;
# frames that repeat across snapshots are the loop. Worst case the frozen game crashes, which
# ends it the same way as closing it. Run: powershell -File FreezeStack.ps1
Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class Fs {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenThread(int a, bool i, int t);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool i, int p);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern int SuspendThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern int ResumeThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool GetThreadContext(IntPtr h, IntPtr c);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr w);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, int t, int pr);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr s, int t);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr sa, IntPtr ss, IntPtr start, IntPtr arg, int f, IntPtr id);
  [DllImport("kernel32.dll")] public static extern int WaitForSingleObject(IntPtr h, int ms);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  // Pauses the thread, reads RIP and the stack above RSP, lets it run again.
  public static ulong Snapshot(IntPtr th, IntPtr ph, byte[] stack) {
    IntPtr raw = Marshal.AllocHGlobal(1248); IntPtr c = new IntPtr((raw.ToInt64() + 15) & ~15L);
    for (int i = 0; i < 1232; i++) Marshal.WriteByte(c, i, 0);
    Marshal.WriteInt32(c, 0x30, 0x100003);
    ulong rip = 0;
    if (SuspendThread(th) < 0) { Marshal.FreeHGlobal(raw); return 0; }
    try { if (GetThreadContext(th, c)) { rip = (ulong)Marshal.ReadInt64(c, 0xF8); IntPtr r; ReadProcessMemory(ph, new IntPtr(Marshal.ReadInt64(c, 0x98)), stack, new IntPtr(stack.Length), out r); } }
    finally { ResumeThread(th); Marshal.FreeHGlobal(raw); }
    return rip;
  }
}
"@

function Export-Rva([string]$Dll, [string]$Name) {
  $b = [IO.File]::ReadAllBytes($Dll)
  $u16 = { param($o) [BitConverter]::ToUInt16($b, $o) }; $u32 = { param($o) [BitConverter]::ToUInt32($b, $o) }
  $pe = & $u32 0x3c; $nsec = & $u16 ($pe + 6); $opt = $pe + 24; $optSize = & $u16 ($pe + 20)
  $secs = for ($i = 0; $i -lt $nsec; $i++) { $s = $opt + $optSize + 40 * $i; @{ Va = (& $u32 ($s + 12)); Size = (& $u32 ($s + 8)); Raw = (& $u32 ($s + 20)) } }
  $off = { param($rva) foreach ($s in $secs) { if ($rva -ge $s.Va -and $rva -lt $s.Va + [Math]::Max($s.Size, 1)) { return $rva - $s.Va + $s.Raw } } }
  $e = & $off (& $u32 ($opt + 112))
  $funcs = & $off (& $u32 ($e + 28)); $names = & $off (& $u32 ($e + 32)); $ords = & $off (& $u32 ($e + 36))
  for ($i = 0; $i -lt (& $u32 ($e + 24)); $i++) {
    $no = & $off (& $u32 ($names + 4 * $i)); $end = $no; while ($b[$end] -ne 0) { $end++ }
    if ([Text.Encoding]::ASCII.GetString($b, $no, $end - $no) -eq $Name) { return [uint64](& $u32 ($funcs + 4 * (& $u16 ($ords + 2 * $i)))) }
  }
  throw "$Name not exported by $Dll"
}

$p = Get-Process $ProcessName -ErrorAction Stop | Select-Object -First 1
"{0} (pid {1}) responding: {2}" -f $p.ProcessName, $p.Id, $p.Responding
$main = $p.Threads | Sort-Object StartTime | Select-Object -First 1
$mono = $p.Modules | Where-Object { $_.ModuleName -like "mono*.dll" } | Select-Object -First 1
$pmip = [uint64]$mono.BaseAddress.ToInt64() + (Export-Rva $mono.FileName "mono_pmip")
$mods = $p.Modules | ForEach-Object { @{ B = [uint64]$_.BaseAddress.ToInt64(); E = [uint64]$_.BaseAddress.ToInt64() + [uint64]$_.ModuleMemorySize } }
function In-Module([uint64]$a) { foreach ($m in $mods) { if ($a -ge $m.B -and $a -lt $m.E) { return $true } }; $false }

$th = [Fs]::OpenThread(0x2 -bor 0x8 -bor 0x40, $false, $main.Id)
$ph = [Fs]::OpenProcess(0x2 -bor 0x8 -bor 0x10 -bor 0x20 -bor 0x400, $false, $p.Id)
if ($th -eq [IntPtr]::Zero -or $ph -eq [IntPtr]::Zero) { "Could not open the game process (run as the same user)."; exit 1 }
$mem = [Fs]::VirtualAllocEx($ph, [IntPtr]::Zero, [IntPtr]4096, 0x3000, 0x40)
$slot = [uint64]$mem.ToInt64() + 0x100
$names = @{}

# Runs mono_pmip(ip) inside the game and returns its text, or $null for an address that is no method.
function Name-Of([uint64]$ip) {
  if ($names.ContainsKey($ip)) { return $names[$ip] }
  $code = [byte[]](0x48,0x83,0xEC,0x28, 0x48,0xB9) + [BitConverter]::GetBytes($ip) + [byte[]](0x48,0xB8) + [BitConverter]::GetBytes($pmip) +
          [byte[]](0xFF,0xD0, 0x48,0xA3) + [BitConverter]::GetBytes($slot) + [byte[]](0x48,0x83,0xC4,0x28, 0x31,0xC0, 0xC3)
  $w = [IntPtr]::Zero; $rd = [IntPtr]::Zero
  [Fs]::WriteProcessMemory($ph, [IntPtr][int64]$slot, (New-Object byte[] 8), [IntPtr]8, [ref]$w) | Out-Null
  [Fs]::WriteProcessMemory($ph, $mem, $code, [IntPtr]$code.Length, [ref]$w) | Out-Null
  $t = [Fs]::CreateRemoteThread($ph, [IntPtr]::Zero, [IntPtr]::Zero, $mem, [IntPtr]::Zero, 0, [IntPtr]::Zero)
  if ($t -eq [IntPtr]::Zero) { throw "could not start the lookup thread" }
  $done = [Fs]::WaitForSingleObject($t, 5000); [Fs]::CloseHandle($t) | Out-Null
  if ($done -ne 0) { throw "lookup did not finish" }
  $pb = New-Object byte[] 8
  [Fs]::ReadProcessMemory($ph, [IntPtr][int64]$slot, $pb, [IntPtr]8, [ref]$rd) | Out-Null
  $sp = [BitConverter]::ToUInt64($pb, 0); $text = $null
  if ($sp -ne 0) {
    $sb = New-Object byte[] 600
    [Fs]::ReadProcessMemory($ph, [IntPtr][int64]$sp, $sb, [IntPtr]600, [ref]$rd) | Out-Null
    $n = [Array]::IndexOf($sb, [byte]0); if ($n -lt 0) { $n = 600 }
    $text = [Text.Encoding]::UTF8.GetString($sb, 0, $n) -replace '\s+\(0000[0-9A-F]+ 0000[0-9A-F]+\) \[0000[0-9A-F]+ - [^\]]*\]', '' -replace '\[\{[0-9A-F]+\} ', '['
  }
  $names[$ip] = $text
  return $text
}

try {
  for ($k = 0; $k -lt $Snapshots; $k++) {
    $stack = New-Object byte[] 24576; $rip = 0
    # A snapshot taken while the thread runs C# code starts at that code.
    for ($i = 0; $i -lt 40; $i++) { $rip = [Fs]::Snapshot($th, $ph, $stack); if ($rip -and -not (In-Module $rip)) { break }; Start-Sleep -Milliseconds 60 }
    "== snapshot $($k + 1)"
    $shown = 0
    foreach ($v in @($rip) + (0..($stack.Length / 8 - 1) | ForEach-Object { [BitConverter]::ToUInt64($stack, $_ * 8) })) {
      if ($shown -ge $Frames) { break }
      if (($v -shr 32) -lt 0x100 -or ($v -shr 32) -gt 0x7FFF -or (In-Module $v)) { continue }
      $name = Name-Of $v
      if ($name) { "   " + $name.Trim(); $shown++ }
    }
    Start-Sleep -Seconds 1
  }
} finally {
  [Fs]::VirtualFreeEx($ph, $mem, [IntPtr]::Zero, 0x8000) | Out-Null
  [Fs]::CloseHandle($th) | Out-Null; [Fs]::CloseHandle($ph) | Out-Null
}
