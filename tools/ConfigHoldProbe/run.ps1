param(
  [string]$HostExe,
  [string]$Valheim = "H:\SteamLibrary\steamapps\common\Valheim",
  [string]$Core = (Join-Path $env:APPDATA "com.kesomannen.gale\valheim\profiles\Default\BepInEx\core"),
  [string]$Preloader = (Join-Path $PSScriptRoot "..\..\src\Preloader\bin\Release\Vigil.Preloader.dll"))
# Checks the hold of config saves against BepInEx's own ConfigFile in Valheim's own Mono: Probe.cs runs
# the same settings scenarios without the hold and with it, and every file the two runs leave must be
# the same byte for byte. host.c is the Mono host it runs in (MSVC: cl host.c, from a developer
# prompt); it loads Valheim's mono-2.0-bdwgc.dll and runs a managed program with its Managed folder.
# Run: powershell -File run.ps1 -HostExe <path to host.exe>
if (-not $HostExe -or -not (Test-Path $HostExe)) { "Give -HostExe, the host built from host.c."; exit 1 }
$out = Join-Path $PSScriptRoot "out"
New-Item -ItemType Directory -Force -Path $out | Out-Null
foreach ($f in "BepInEx.dll", "0Harmony.dll", "MonoMod.RuntimeDetour.dll", "MonoMod.Utils.dll", "Mono.Cecil.dll", "Mono.Cecil.Mdb.dll", "Mono.Cecil.Pdb.dll", "Mono.Cecil.Rocks.dll") {
  if (Test-Path (Join-Path $Core $f)) { Copy-Item -Force (Join-Path $Core $f) $out }
}
Copy-Item -Force $Preloader $out
# Vigil.dll and Jotunn.dll for the check of the patch on Jotunn's config sync, when they are at hand.
$vigil = Join-Path $PSScriptRoot "..\..\bin\Release\Vigil.dll"
$jotunn = Get-ChildItem (Join-Path (Split-Path $Core) "plugins") -Recurse -Filter "Jotunn.dll" -ErrorAction SilentlyContinue | Select-Object -First 1
if ((Test-Path $vigil) -and $jotunn) { Copy-Item -Force $vigil $out; Copy-Item -Force $jotunn.FullName $out }
& "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /out:"$out\Probe.exe" /r:"$out\BepInEx.dll" /r:"$out\0Harmony.dll" (Join-Path $PSScriptRoot "Probe.cs")
if ($LASTEXITCODE -ne 0) { "The probe did not compile."; exit 1 }
$env:PROBE_DIR = $out
& $HostExe "$Valheim\MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll" "$Valheim\valheim_Data\Managed" "$Valheim\MonoBleedingEdge\etc" "$out\Probe.exe"
"exit $LASTEXITCODE"
