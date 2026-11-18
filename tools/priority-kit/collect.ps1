<#
SPDX-License-Identifier: MIT
The priority kit's collection script for Windows (docs/kiln/priority-kit.md). Run it after each game run, before the
next start (Kiln starts tick-stats.jsonl afresh every time the game starts):

  powershell -ExecutionPolicy Bypass -File collect.ps1 [-Game <the Enshrouded folder>] [-Out <zip>]

It puts into one zip, next to this script unless -Out says otherwise:
  - the lines of kiln\logs\kiln.log and kiln.prev.log that the analysis reads (Kiln's version, kiln.engine, the test
    mod, kiln.world's writes, kiln.tasks, the tick, faults), not the whole log;
  - kiln\logs\tick-stats.jsonl and kiln\state\capabilities.json;
  - every run of the test mod (kiln\logs\gate-priority-test\run-*), the CPU-load helper's phase files
    (cpu-load-*.csv next to this script) and the output of the check without the game (fault-test*.txt, ditto);
  - system.json: Windows version, CPU, cores, memory, power plan, the time zone offset, and whether the measurement
    switch was set.
Your user name, the computer's name, folders under C:\Users, Steam ids and world GUIDs are replaced before anything is
written; open the zip and look before you post it. At the end it deletes the measurement switch
(kiln\data\kiln.engine\gate-priority-raise) if it is still there, so the next start runs with the raise on again;
-KeepSwitch keeps it.
#>
[CmdletBinding()]
param(
    [string]$Game = '',
    [string]$Out = '',
    [switch]$KeepSwitch
)
Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'
$here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
$kitVersion = '1'

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

$utf8 = New-Object System.Text.UTF8Encoding($false)
# ---- redaction: the same rules as collect.py ----
$literal = New-Object System.Collections.Generic.List[object]
foreach ($pair in @(
        @($env:USERPROFILE, 'C:\Users\<user>'),
        @($env:USERNAME, '<user>'),
        @($env:COMPUTERNAME, '<computer>'))) {
    if ($pair[0] -and $pair[0].Length -ge 3) { $literal.Add($pair) }
}
function Redact([string]$text) {
    foreach ($pair in $literal) {
        $text = [regex]::Replace($text, '(?i)(?<![A-Za-z0-9])' + [regex]::Escape($pair[0]) + '(?![A-Za-z0-9])', $pair[1].Replace('$', '$$'))
    }
    $text = [regex]::Replace($text, '(?i)([a-z]:[\\/]+users[\\/]+)[^\\/"\s]+', '$1<user>')
    $text = [regex]::Replace($text, '([\\/]home[\\/]+|/Users/)[^\\/"\s]+', '$1<user>')  # also Wine's Z:\home\<user>
    $text = [regex]::Replace($text, '(?i)(userdata[\\/]+)\d+', '$1<steamid>')  # Steam's per-account folder
    $text = [regex]::Replace($text, '7656119\d{10}', '<steamid>')
    $text = [regex]::Replace($text, '(?<![0-9A-Fa-f-])[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}(?![0-9A-Fa-f-])', '<guid>')
    $text = [regex]::Replace($text, '(?<![0-9A-Fa-f])[0-9A-Fa-f]{32}(?![0-9A-Fa-f])', '<guid>')
    return $text
}
$keep = 'Kiln |build |config:|kiln\.engine|engine\.|gate-priority-test|world\.ecs|ecs-write|kiln\.tasks|tasks[:.]|watchdog|tick:|crash|fault|quarantin|safe idle'
# lines that name people or their worlds stay out even when $keep matches them ("net: <name> joined with Kiln ...")
$drop = '\bnet: |\bplayers: |\bsaves: |\bchat'
# Kiln writes UTF-8; Windows PowerShell 5.1 would read a file without a byte-order mark in the ANSI code page
function Read-Text([string]$path) { return [System.IO.File]::ReadAllText($path, $utf8) }
function Log-Lines([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    return @((Read-Text $path) -split "`r?`n" | Where-Object { ($_ -cmatch $keep) -and -not ($_ -cmatch $drop) } | ForEach-Object { Redact $_ })
}

$gameDir = Find-Game
$kiln = Join-Path $gameDir 'kiln'
$logs = Join-Path $kiln 'logs'
$switchFile = [System.IO.Path]::Combine($kiln, 'data', 'kiln.engine', 'gate-priority-raise')
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$entries = [ordered]@{}  # zip entry name ('/' separated) -> text
$listed = New-Object System.Collections.Generic.List[string]
function Put([string]$name, [string]$text) {
    $entries[$name] = $text
    $listed.Add($name)
}
try {
    foreach ($name in 'kiln.log', 'kiln.prev.log') {
        $lines = @(Log-Lines (Join-Path $logs $name))
        if ($lines.Count) { Put "logs/$name" (($lines -join "`n") + "`n") }
    }
    foreach ($name in 'tick-stats.jsonl') {
        $p = Join-Path $logs $name
        if (Test-Path -LiteralPath $p) { Put "logs/$name" (Redact (Read-Text $p)) }
    }
    $caps = [System.IO.Path]::Combine($kiln, 'state', 'capabilities.json')
    if (Test-Path -LiteralPath $caps) { Put 'state/capabilities.json' (Redact (Read-Text $caps)) }
    $runs = Join-Path $logs 'gate-priority-test'
    if (Test-Path -LiteralPath $runs) {
        foreach ($run in Get-ChildItem -LiteralPath $runs -Directory -Filter 'run-*') {
            foreach ($f in Get-ChildItem -LiteralPath $run.FullName -File) {
                Put ("runs/{0}/{1}" -f $run.Name, $f.Name) (Redact (Read-Text $f.FullName))
            }
        }
    }
    foreach ($f in Get-ChildItem -LiteralPath $here -File -Filter 'cpu-load-*.csv' -ErrorAction SilentlyContinue) {
        Put ("load/{0}" -f $f.Name) (Read-Text $f.FullName)
    }
    foreach ($f in Get-ChildItem -LiteralPath $here -File -Filter 'fault-test*.txt' -ErrorAction SilentlyContinue) {
        Put ("checks/{0}" -f $f.Name) (Redact (Read-Text $f.FullName))  # UTF-16 from 5.1's `>`: the byte-order mark tells
    }

    # system information (nothing that names you or the machine); each part on its own, so one that fails costs only itself
    $osText = $null; $memory = $null; $cpuName = $null; $cpuCount = $null; $cores = $null; $logical = [Environment]::ProcessorCount
    try {
        $os = Get-CimInstance Win32_OperatingSystem
        $osText = "$($os.Caption) $($os.Version) (build $($os.BuildNumber))"
        $memory = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
    } catch { $osText = [Environment]::OSVersion.VersionString }
    try {
        $cpu = @(Get-CimInstance Win32_Processor)
        $cpuName = ($cpu | Select-Object -First 1).Name.Trim()
        $cpuCount = $cpu.Count
        $cores = ($cpu | Measure-Object -Property NumberOfCores -Sum).Sum
        $logical = ($cpu | Measure-Object -Property NumberOfLogicalProcessors -Sum).Sum
    } catch { }
    $plan = ''
    try { $plan = ((powercfg /getactivescheme) -join ' ') -replace '^.*\(([^)]*)\).*$', '$1' } catch { }
    $wine = $false
    try { $wine = $null -ne (Get-ItemProperty -Path 'HKLM:\Software\Wine' -ErrorAction Stop) } catch { }
    $switchText = if (Test-Path -LiteralPath $switchFile -PathType Leaf) { [System.IO.File]::ReadAllText($switchFile).Trim() } else { $null }
    $gpu = @()
    try { $gpu = @(Get-CimInstance Win32_VideoController -ErrorAction Stop | ForEach-Object { $_.Name }) } catch { }
    $system = [ordered]@{
        kit                = $kitVersion
        collector          = 'collect.ps1'
        platform           = 'windows'
        wine               = [bool]$wine
        os                 = $osText
        cpu                = $cpuName
        cpu_count          = $cpuCount
        cores              = $cores
        logical_processors = $logical
        memory_gb          = $memory
        power_plan         = $plan
        gpu                = $gpu
        utc_offset_minutes = [int]([TimeZoneInfo]::Local.GetUtcOffset([DateTime]::Now).TotalMinutes)
        collected_utc_ms   = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
        raise_switch       = $switchText
        files              = $listed
    }
    Put 'system.json' (Redact ($system | ConvertTo-Json -Depth 4))

    $mode = if ($switchText -ceq 'off') { 'raise-off' } else { 'raise-on' }  # as kiln.engine: exactly "off"
    if (-not $Out) { $Out = Join-Path $here "kiln-priority-kit-windows-$mode-$stamp.zip" }
    if (Test-Path -LiteralPath $Out) { Remove-Item -LiteralPath $Out -Force }
    # the zip by hand: entry names with '/' (Compress-Archive in Windows PowerShell 5.1 writes '\')
    Add-Type -AssemblyName System.IO.Compression -ErrorAction SilentlyContinue
    Add-Type -AssemblyName System.IO.Compression.FileSystem -ErrorAction SilentlyContinue
    $zip = [System.IO.Compression.ZipFile]::Open($Out, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $entries.Keys) {
            $writer = New-Object System.IO.StreamWriter($zip.CreateEntry($name).Open(), $utf8)
            try { $writer.Write($entries[$name]) } finally { $writer.Dispose() }
        }
    }
    finally { $zip.Dispose() }
    Write-Host "Collected $($listed.Count) file(s) into $Out :"
    $listed | ForEach-Object { Write-Host "  $_" }
    if (-not ($listed | Where-Object { $_ -like 'runs/*' })) {
        Write-Warning 'No run of the test mod was found (kiln\logs\gate-priority-test\run-*). Did the run file name the world you played?'
    }
}
catch {
    Write-Warning "Nothing was collected: $_"
    throw
}
finally {
    # also when the collection failed: the next start must not run with the raise off by accident
    if (Test-Path -LiteralPath $switchFile -PathType Leaf) {
        if ($KeepSwitch) {
            Write-Warning "The measurement switch is still there ($switchFile): the next start runs with the priority raise OFF. Delete it when you are done (kit.ps1 raise on)."
        }
        else {
            Remove-Item -LiteralPath $switchFile -Force
            Write-Warning 'The measurement switch was still there; it is deleted now, so the next start runs with the priority raise on (the normal state).'
        }
    }
}
