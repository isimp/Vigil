param(
  [string]$Out = (Join-Path $env:TEMP ("StartSamples-" + (Get-Date -Format "yyyyMMdd-HHmmss"))),
  [string]$Report = (Join-Path $env:USERPROFILE "AppData\LocalLow\IronGate\Valheim\Vigil\Vigil-startup.txt"),
  [string]$ProcessName = "valheim",
  [int]$IntervalMs = 500, [int]$Frames = 60, [int]$StackKb = 128,
  [int]$WaitMinutes = 60, [int]$AfterMenuSeconds = 3,
  [int]$TestSamples = 0, [int]$ProcessId = 0, [switch]$TestNames, [switch]$ThroughJoin)
# Samples what the game's main thread runs through a whole start, for a start that is slow
# without a log line to say why. Armed before the game starts, it waits for a new game process
# and from then on, every IntervalMs, pauses the main thread for a moment (as a debugger does),
# copies where it is and its stack, and lets it run again: nothing runs inside the game while it
# starts. Once the menu shows (Vigil writes Vigil-startup.txt then), it asks the game's own Mono
# runtime which C# method each kept code address belongs to (mono_pmip, through a short thread
# started inside the game, as FreezeStack.ps1 does), so stay at the menu until it says done.
# Out gets samples.tsv (time since the process started, where the thread was, its stack's
# compiled-code addresses), names.tsv and modules.tsv.
# -TestSamples N takes N samples of the running game (or of -ProcessId) and names nothing,
# unless -TestNames is given too. -ThroughJoin goes on sampling past the menu until a join has
# the player in the world (Vigil writes the report again then), and names in the world.
# Run: powershell -File StartSamples.ps1 -Out <folder>
Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
public static class Ss {
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenThread(int a, bool i, int t);
  [DllImport("kernel32.dll", SetLastError=true)] public static extern IntPtr OpenProcess(int a, bool i, int p);
  [DllImport("kernel32.dll", SetLastError=true)] static extern int SuspendThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] static extern int ResumeThread(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetThreadContext(IntPtr h, IntPtr c);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool ReadProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr r);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool WriteProcessMemory(IntPtr p, IntPtr a, byte[] b, IntPtr s, out IntPtr w);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualAllocEx(IntPtr p, IntPtr a, IntPtr s, int t, int pr);
  [DllImport("kernel32.dll", SetLastError=true)] static extern bool VirtualFreeEx(IntPtr p, IntPtr a, IntPtr s, int t);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr CreateRemoteThread(IntPtr p, IntPtr sa, IntPtr ss, IntPtr start, IntPtr arg, int f, IntPtr id);
  [DllImport("kernel32.dll")] static extern int WaitForSingleObject(IntPtr h, int ms);
  [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
  [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr VirtualQueryEx(IntPtr p, IntPtr a, out Mbi m, IntPtr len);

  [StructLayout(LayoutKind.Sequential)]
  struct Mbi { public ulong BaseAddress; public ulong AllocationBase; public uint AllocationProtect; public uint Pad1; public ulong RegionSize; public uint State; public uint Protect; public uint Type; public uint Pad2; }

  static IntPtr ph, th, ctxRaw, ctx, mem;
  static byte[] stack;
  static int maxFrames;
  static ulong stackTop, slot, pmip;
  static readonly List<ulong> regBase = new List<ulong>(), regEnd = new List<ulong>();
  static readonly List<bool> regCode = new List<bool>();

  public static void Begin(IntPtr process, IntPtr thread, int stackBytes, int frames) {
    ph = process; th = thread; stack = new byte[stackBytes]; maxFrames = frames;
    ctxRaw = Marshal.AllocHGlobal(1248 + 16);
    ctx = new IntPtr((ctxRaw.ToInt64() + 15) & ~15L);
  }

  static bool Query(ulong a, out Mbi m) {
    return VirtualQueryEx(ph, new IntPtr((long)a), out m, new IntPtr(Marshal.SizeOf(typeof(Mbi)))) != IntPtr.Zero;
  }

  // Code the runtime compiled: committed, executable, and in no loaded module. Free or reserved
  // memory is asked again next time, since it may hold compiled code later.
  public static bool IsCompiled(ulong a) {
    for (int i = 0; i < regBase.Count; i++) if (a >= regBase[i] && a < regEnd[i]) return regCode[i];
    Mbi m;
    if (!Query(a, out m) || m.State != 0x1000) return false;
    bool code = (m.Protect & 0xF0) != 0 && m.Type != 0x1000000;
    regBase.Add(m.BaseAddress); regEnd.Add(m.BaseAddress + m.RegionSize); regCode.Add(code);
    return code;
  }

  // Pauses the thread, copies where it is and its stack, lets it run again; then keeps the
  // stack's compiled-code addresses, innermost first: "rip <tab> rsp <tab> a,b,c".
  public static string Sample() {
    for (int i = 0; i < 1232; i++) Marshal.WriteByte(ctx, i, 0);
    Marshal.WriteInt32(ctx, 0x30, 0x100003);
    ulong rip = 0, rsp = 0;
    int got = 0;
    if (SuspendThread(th) < 0) return null;
    try {
      if (GetThreadContext(th, ctx)) {
        rip = (ulong)Marshal.ReadInt64(ctx, 0xF8);
        rsp = (ulong)Marshal.ReadInt64(ctx, 0x98);
        Mbi m;
        if ((stackTop == 0 || rsp >= stackTop) && Query(rsp, out m)) stackTop = m.BaseAddress + m.RegionSize;
        ulong want = Math.Min((ulong)stack.Length, stackTop > rsp ? stackTop - rsp : 0);
        IntPtr r;
        if (want > 0 && ReadProcessMemory(ph, new IntPtr((long)rsp), stack, new IntPtr((long)want), out r)) got = (int)r.ToInt64();
      }
    } finally { ResumeThread(th); }
    if (rip == 0) return null;
    var sb = new StringBuilder();
    sb.Append(rip.ToString("X")).Append('\t').Append(rsp.ToString("X")).Append('\t');
    int n = 0;
    if (IsCompiled(rip)) { sb.Append(rip.ToString("X")); n++; }
    for (int o = 0; o + 8 <= got && n < maxFrames; o += 8) {
      ulong v = BitConverter.ToUInt64(stack, o);
      if (v < 0x100000000UL || v > 0x7FFFFFFFFFFFUL || !IsCompiled(v)) continue;
      if (n > 0) sb.Append(',');
      sb.Append(v.ToString("X"));
      n++;
    }
    return sb.ToString();
  }

  public static void BeginNames(ulong pmipAddress) {
    pmip = pmipAddress;
    mem = VirtualAllocEx(ph, IntPtr.Zero, new IntPtr(4096), 0x3000, 0x40);
    slot = (ulong)mem.ToInt64() + 0x100;
  }

  // The block is freed only when every lookup thread has finished with it.
  public static void EndNames() { if (mem != IntPtr.Zero) VirtualFreeEx(ph, mem, IntPtr.Zero, 0x8000); mem = IntPtr.Zero; }

  // Runs mono_pmip(ip) on a short thread inside the game: the method's text, null for an address
  // that is no method. finished is false when the thread did not end within 5 s.
  public static string Name(ulong ip, out bool finished) {
    finished = false;
    var code = new List<byte>();
    code.AddRange(new byte[] { 0x48, 0x83, 0xEC, 0x28, 0x48, 0xB9 }); code.AddRange(BitConverter.GetBytes(ip));
    code.AddRange(new byte[] { 0x48, 0xB8 }); code.AddRange(BitConverter.GetBytes(pmip));
    code.AddRange(new byte[] { 0xFF, 0xD0, 0x48, 0xA3 }); code.AddRange(BitConverter.GetBytes(slot));
    code.AddRange(new byte[] { 0x48, 0x83, 0xC4, 0x28, 0x31, 0xC0, 0xC3 });
    IntPtr w, rd;
    byte[] c = code.ToArray();
    WriteProcessMemory(ph, new IntPtr((long)slot), new byte[8], new IntPtr(8), out w);
    WriteProcessMemory(ph, mem, c, new IntPtr(c.Length), out w);
    IntPtr t = CreateRemoteThread(ph, IntPtr.Zero, IntPtr.Zero, mem, IntPtr.Zero, 0, IntPtr.Zero);
    if (t == IntPtr.Zero) return null;
    int done = WaitForSingleObject(t, 5000);
    CloseHandle(t);
    if (done != 0) return null;
    finished = true;
    var pb = new byte[8];
    ReadProcessMemory(ph, new IntPtr((long)slot), pb, new IntPtr(8), out rd);
    ulong sp = BitConverter.ToUInt64(pb, 0);
    if (sp == 0) return null;
    var text = new byte[600];
    int len = text.Length;
    if (!ReadProcessMemory(ph, new IntPtr((long)sp), text, new IntPtr(len), out rd)) {
      len = (int)(4096 - (sp % 4096));
      if (!ReadProcessMemory(ph, new IntPtr((long)sp), text, new IntPtr(len), out rd)) return null;
    }
    int n = Array.IndexOf(text, (byte)0, 0, len);
    return Encoding.UTF8.GetString(text, 0, n < 0 ? len : n);
  }
}
'@

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

function Main-Thread($p) {
  $p.Threads | ForEach-Object { try { [pscustomobject]@{ T = $_; S = $_.StartTime } } catch { } } | Sort-Object S | Select-Object -First 1 | ForEach-Object { $_.T }
}

function Report-Written($p) {
  try { (Get-Item -LiteralPath $Report -ErrorAction Stop).LastWriteTime -gt $p.StartTime } catch { $false }
}

$armed = Get-Date
if ($TestSamples -gt 0) {
  if ($ProcessId -gt 0) { $p = Get-Process -Id $ProcessId -ErrorAction Stop }
  else { $p = Get-Process $ProcessName -ErrorAction Stop | Select-Object -First 1 }
} else {
  "Waiting for a new {0} process (armed {1:HH:mm:ss}, up to {2} min)" -f $ProcessName, $armed, $WaitMinutes
  $p = $null
  while (-not $p) {
    if ((Get-Date) -gt $armed.AddMinutes($WaitMinutes)) { "No new game process came."; exit 2 }
    $p = Get-Process $ProcessName -ErrorAction SilentlyContinue | Where-Object { try { $_.StartTime -gt $armed } catch { $false } } | Select-Object -First 1
    if (-not $p) { Start-Sleep -Milliseconds 200 }
  }
}

$main = Main-Thread $p
$th = [Ss]::OpenThread(0x2 -bor 0x8 -bor 0x40, $false, $main.Id)
$ph = [Ss]::OpenProcess(0x2 -bor 0x8 -bor 0x10 -bor 0x20 -bor 0x400, $false, $p.Id)
if ($th -eq [IntPtr]::Zero -or $ph -eq [IntPtr]::Zero) { "Could not open the game process (run as the same user)."; exit 1 }
"{0} pid {1}, started {2:HH:mm:ss.fff}, main thread {3}" -f $p.ProcessName, $p.Id, $p.StartTime, $main.Id
[Ss]::Begin($ph, $th, $StackKb * 1024, $Frames)

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$samples = New-Object IO.StreamWriter((Join-Path $Out "samples.tsv"), $false, (New-Object Text.UTF8Encoding($false)))
$samples.WriteLine("ms`trip`trsp`tcode")
$taken = 0; $menuAt = $null; $sw = [Diagnostics.Stopwatch]::StartNew(); $lastCheck = -1000
try {
  while ($true) {
    $ms = ((Get-Date) - $p.StartTime).TotalMilliseconds
    $line = [Ss]::Sample()
    if ($line) { $samples.WriteLine(("{0:0}`t{1}" -f $ms, $line)); $taken++ }
    if ($TestSamples -gt 0) { if ($taken -ge $TestSamples) { break } }
    elseif ($sw.ElapsedMilliseconds - $lastCheck -ge 1000) {
      $lastCheck = $sw.ElapsedMilliseconds
      if ($p.HasExited) { "The game process ended before the menu showed, after $taken samples."; break }
      if (-not $menuAt -and (Report-Written $p)) { $menuAt = $sw.ElapsedMilliseconds; $menuWrite = (Get-Item -LiteralPath $Report).LastWriteTime; "Menu at {0:0.0} s, {1} samples so far" -f ($ms / 1000), $taken }
      if ($menuAt -and -not $ThroughJoin -and $sw.ElapsedMilliseconds - $menuAt -ge $AfterMenuSeconds * 1000) { break }
      # Vigil writes the report again once a join has the player in the world.
      if ($menuAt -and $ThroughJoin -and -not $joinAt) {
        try { if ((Get-Item -LiteralPath $Report).LastWriteTime -gt $menuWrite) { $joinAt = $sw.ElapsedMilliseconds; "In the world at {0:0.0} s, {1} samples so far" -f ($ms / 1000), $taken } } catch { }
      }
      if ($joinAt -and $sw.ElapsedMilliseconds - $joinAt -ge $AfterMenuSeconds * 1000) { break }
      if ($sw.Elapsed.TotalMinutes -gt $(if ($ThroughJoin) { 25 } else { 10 })) { "Stopped: no menu, or no join, in time."; break }
    }
    Start-Sleep -Milliseconds $IntervalMs
  }
} finally { $samples.Dispose() }
"$taken samples in $Out"

$p.Refresh()
$mods = $p.Modules
$mods | ForEach-Object { "{0:X}`t{1:X}`t{2}" -f $_.BaseAddress.ToInt64(), ($_.BaseAddress.ToInt64() + $_.ModuleMemorySize), $_.ModuleName } |
  Set-Content -Encoding UTF8 (Join-Path $Out "modules.tsv")

if (($TestSamples -gt 0 -and -not $TestNames) -or $p.HasExited) { [Ss]::CloseHandle($th) | Out-Null; [Ss]::CloseHandle($ph) | Out-Null; exit 0 }

$mono = $mods | Where-Object { $_.ModuleName -like "mono*.dll" } | Select-Object -First 1
$pmip = [uint64]$mono.BaseAddress.ToInt64() + (Export-Rva $mono.FileName "mono_pmip")
$addresses = Import-Csv -Delimiter "`t" (Join-Path $Out "samples.tsv") | ForEach-Object { $_.code -split ',' } | Where-Object { $_ } | Sort-Object -Unique
"Naming {0} code addresses" -f @($addresses).Count
[Ss]::BeginNames($pmip)
$names = New-Object IO.StreamWriter((Join-Path $Out "names.tsv"), $false, (New-Object Text.UTF8Encoding($false)))
$named = 0; $allDone = $true
try {
  foreach ($a in $addresses) {
    $finished = $false
    $text = [Ss]::Name([Convert]::ToUInt64($a, 16), [ref]$finished)
    if (-not $finished) { "A lookup did not finish; naming stopped at $named."; $allDone = $false; break }
    if ($text) {
      $text = $text -replace '\s+\(0000[0-9A-F]+ 0000[0-9A-F]+\) \[0000[0-9A-F]+ - [^\]]*\]', '' -replace '\[\{[0-9A-F]+\} ', '['
      $names.WriteLine("$a`t" + $text.Trim()); $named++
    }
  }
} finally {
  $names.Dispose()
  if ($allDone) { [Ss]::EndNames() }
  [Ss]::CloseHandle($th) | Out-Null; [Ss]::CloseHandle($ph) | Out-Null
}
"Done: $named of {0} addresses named; the game can go on." -f @($addresses).Count
