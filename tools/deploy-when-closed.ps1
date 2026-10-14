# Tests and builds Vigil right away, then copies the plugin into the Gale profile once Valheim has
# been closed for a while: the running game holds the profile's copy loaded. Stops at the first
# step that fails.
#   powershell -ExecutionPolicy Bypass -File tools/deploy-when-closed.ps1 [-ClosedSeconds 30]
param([int]$ClosedSeconds = 30)

$root = Split-Path -Parent $PSScriptRoot

$t = & dotnet test "$root\tests\Vigil.Tests" 2>&1 | Out-String
$code = $LASTEXITCODE
($t -split "`n" | Where-Object { $_ -match "\[FAIL\]| Failed |Passed!|Failed!|error CS|error MSB" } | Select-Object -Unique) -join "`n"
if ($code -ne 0) { "tests failed; not building"; exit 1 }

$b = & dotnet build "$root\Vigil.csproj" -c Release -p:Deploy=false -v q 2>&1 | Out-String
$code = $LASTEXITCODE
($b -split "`n" | Where-Object { $_ -match " error |warning |Build succeeded" } | Select-Object -Unique) -join "`n"
if ($code -ne 0) { "build failed"; exit 1 }

$closedSince = $null
while ($true) {
    if (Get-Process valheim -ErrorAction SilentlyContinue) { $closedSince = $null }
    elseif ($null -eq $closedSince) { $closedSince = Get-Date }
    elseif (((Get-Date) - $closedSince).TotalSeconds -ge $ClosedSeconds) { break }
    Start-Sleep -Seconds 5
}

$target = "$env:APPDATA\com.kesomannen.gale\valheim\profiles\Default\BepInEx\plugins\Vigil"
New-Item -ItemType Directory -Force $target | Out-Null
Copy-Item "$root\bin\Release\Vigil.dll" $target -Force
$dll = Join-Path $target "Vigil.dll"
"deployed: $((Get-Item $dll).VersionInfo.FileVersion), written $((Get-Item $dll).LastWriteTime.ToString('HH:mm:ss'))"
