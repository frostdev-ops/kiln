<#
SPDX-License-Identifier: MIT
The priority kit's setup for Windows (docs/kiln/priority-kit.md). Every command takes -Game <the Enshrouded folder>;
without it the Steam library is searched.

  kit.ps1 install      copy the test mod into <game>\kiln\native\gate-priority-test and create its empty run file
  kit.ps1 world        name the world to test: the newest GUID the mod logged (or -Guid <32 hex digits>)
  kit.ps1 raise off    the measurement switch: the next game start runs with the gate's priority raise off
  kit.ps1 raise on     delete the switch again (the normal state)
  kit.ps1 status       what is installed, the run file and the switch
  kit.ps1 uninstall    remove the mod, its run file and the switch (-Logs: its measurements in kiln\logs too)

Nothing here starts or stops the game. The kit changes only these files under <game>\kiln:
native\gate-priority-test\, data\gate-priority-test\run.txt and data\kiln.engine\gate-priority-raise.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)][ValidateSet('install', 'world', 'raise', 'status', 'uninstall')][string]$Command,
    [Parameter(Position = 1)][string]$Value = '',
    [string]$Game = '',
    [string]$Guid = '',
    [switch]$Logs
)
Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }

function Find-Game {
    if ($Game) {
        if (-not (Test-Path -LiteralPath (Join-Path $Game 'enshrouded.exe'))) { throw "No enshrouded.exe in $Game" }
        return (Resolve-Path -LiteralPath $Game).Path
    }
    $steam = $null
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $item = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($item) {
            foreach ($name in 'SteamPath', 'InstallPath') {
                if ($item.PSObject.Properties[$name]) { $steam = $item.$name; break }
            }
        }
        if ($steam) { break }
    }
    $libraries = @()
    if ($steam) {
        $libraries += $steam
        $vdf = [System.IO.Path]::Combine($steam, 'steamapps', 'libraryfolders.vdf')
        if (Test-Path -LiteralPath $vdf) {
            foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libraries += $m.Groups[1].Value -replace '\\\\', '\'
            }
        }
    }
    foreach ($lib in $libraries | Select-Object -Unique) {
        $dir = [System.IO.Path]::Combine($lib, 'steamapps', 'common', 'Enshrouded')
        if (Test-Path -LiteralPath (Join-Path $dir 'enshrouded.exe')) { return $dir }
    }
    throw 'The Enshrouded folder was not found in the Steam libraries; pass -Game "<the folder with enshrouded.exe>".'
}

$gameDir = Find-Game
$kiln = Join-Path $gameDir 'kiln'
$modDir = [System.IO.Path]::Combine($kiln, 'native', 'gate-priority-test')
$runFile = [System.IO.Path]::Combine($kiln, 'data', 'gate-priority-test', 'run.txt')
$switchFile = [System.IO.Path]::Combine($kiln, 'data', 'kiln.engine', 'gate-priority-raise')
$template = @'
# The priority kit's run file (docs/kiln/priority-kit.md). One key=value per line.
# world=      the world to test: kit.ps1 world fills it in (the mod does nothing on any other world)
# minutes=15  how long the writes run; warmup=30 the seconds of steps recorded first
# rate=50     change-and-restore pairs a second; label=  free text for the analysis
'@
function Write-Text([string]$path, [string]$text) {
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path) | Out-Null
    [System.IO.File]::WriteAllText($path, $text)  # UTF-8 without a byte-order mark
}
if (-not (Test-Path -LiteralPath (Join-Path $kiln 'kiln-core.dll'))) {
    throw "Kiln is not installed in $gameDir (no kiln\kiln-core.dll). Install the test build of Kiln first."
}

switch ($Command) {
    'install' {
        $source = [System.IO.Path]::Combine($here, 'mod', 'gate-priority-test')
        if (-not (Test-Path -LiteralPath (Join-Path $source 'gate-priority-test.dll'))) { throw "No mod\gate-priority-test\gate-priority-test.dll next to this script" }
        New-Item -ItemType Directory -Force -Path $modDir | Out-Null
        Copy-Item -Force -Path (Join-Path $source '*') -Destination $modDir
        if (-not (Test-Path -LiteralPath $runFile)) { Write-Text $runFile $template }
        Write-Host "Installed the test mod in $modDir"
        $profileFile = Join-Path $kiln 'profile.json'
        if (Test-Path -LiteralPath $profileFile) {
            $p = Get-Content -LiteralPath $profileFile -Raw | ConvertFrom-Json
            $drop = if ($p.PSObject.Properties['dropins']) { $p.dropins } else { $false }
            if (-not (($drop -eq $true) -or (@($drop) -contains 'native'))) {
                Write-Warning 'A Kiln launcher profile is active and does not load drop-in native mods. In the launcher, turn on drop-ins for native mods in the profile''s settings (or deactivate the profile), or the mod will not load.'
            }
        }
        Write-Host 'Next: start the game and load the world you test on (your own save), wait a minute, quit, then run: kit.ps1 world'
    }
    'world' {
        if (-not $Guid) {
            $log = [System.IO.Path]::Combine($kiln, 'logs', 'kiln.log')
            $found = @()
            foreach ($f in ([System.IO.Path]::Combine($kiln, 'logs', 'kiln.prev.log')), ([System.IO.Path]::Combine($kiln, 'logs', 'kiln.log'))) {  # oldest first
                if (Test-Path -LiteralPath $f) {
                    $found += [regex]::Matches((Get-Content -LiteralPath $f -Raw), 'gate-priority-test: this world is ([0-9a-f]{32})') |
                        ForEach-Object { $_.Groups[1].Value }
                }
            }
            if (-not $found) { throw "No 'gate-priority-test: this world is ...' line in $log. Start the game with the mod installed, load your world, wait a minute, quit, and try again." }
            $Guid = $found[-1]
            if (@($found | Select-Object -Unique).Count -gt 1) { Write-Warning "The logs name more than one world; the newest is used: $Guid. Pass -Guid to choose." }
        }
        $Guid = ($Guid -replace '-', '').ToLowerInvariant()
        if ($Guid -notmatch '^[0-9a-f]{32}$') { throw 'A world GUID is 32 hex digits.' }
        $lines = @()
        if (Test-Path -LiteralPath $runFile) { $lines = @(Get-Content -LiteralPath $runFile | Where-Object { $_ -notmatch '^\s*world\s*=' }) }
        if (-not $lines) { $lines = $template -split "`r?`n" }
        Write-Text $runFile ((@("world=$Guid") + $lines) -join "`r`n")
        Write-Host "The test runs on world $Guid (and on no other). Run file: $runFile"
    }
    'raise' {
        if ($Value -eq 'off') {
            Write-Text $switchFile 'off'
            Write-Host "The measurement switch is set: the next game start runs with the gate's priority raise OFF. Delete it after that run (kit.ps1 raise on; collect.ps1 also deletes it)."
        }
        elseif ($Value -eq 'on') {
            if (Test-Path -LiteralPath $switchFile) { Remove-Item -LiteralPath $switchFile -Force; Write-Host 'The measurement switch is deleted: the raise is on again (the normal state).' }
            else { Write-Host 'No measurement switch: the raise is on (the normal state).' }
        }
        else { throw 'Use: kit.ps1 raise off  or  kit.ps1 raise on' }
    }
    'status' {
        Write-Host "Game:      $gameDir"
        Write-Host ("Mod:       " + $(if (Test-Path -LiteralPath (Join-Path $modDir 'gate-priority-test.dll')) { "installed ($modDir)" } else { 'not installed' }))
        if (Test-Path -LiteralPath $runFile) {
            $w = @(Get-Content -LiteralPath $runFile | Where-Object { $_ -match '^\s*world\s*=' })
            Write-Host ("Run file:  " + $(if ($w) { $w[0].Trim() } else { 'no world named yet (kit.ps1 world)' }))
        }
        else { Write-Host 'Run file:  none' }
        Write-Host ("Switch:    " + $(if (Test-Path -LiteralPath $switchFile) { 'present: the raise is OFF at the next start' } else { 'none: the raise is on' }))
        $caps = [System.IO.Path]::Combine($kiln, 'state', 'capabilities.json')
        if (Test-Path -LiteralPath $caps) {
            $rows = @((Get-Content -LiteralPath $caps -Raw | ConvertFrom-Json).capabilities)
            foreach ($name in 'world.ecs_write', 'engine.gate_priority_raise') {
                $row = $rows | Where-Object { $_.name -eq $name } | Select-Object -First 1
                Write-Host ("{0,-28}{1} at the last start {2}" -f ($name + ':'), $(if ($row) { $row.state } else { 'not reported' }),
                    $(if ($row -and $row.reasonText) { '(' + $row.reasonText + ')' } else { '' }))
            }
        }
    }
    'uninstall' {
        foreach ($p in $modDir, (Split-Path -Parent $runFile), $switchFile) {
            if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; Write-Host "Removed $p" }
        }
        $out = [System.IO.Path]::Combine($kiln, 'logs', 'gate-priority-test')
        if ($Logs -and (Test-Path -LiteralPath $out)) { Remove-Item -LiteralPath $out -Recurse -Force; Write-Host "Removed $out" }
        elseif (Test-Path -LiteralPath $out) { Write-Host "Kept the measurements in $out (-Logs removes them too)" }
        Write-Host 'The kit is removed. Kiln itself stays; reinstall your usual Kiln build if you installed the test build for this.'
    }
}
