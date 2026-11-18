<#
SPDX-License-Identifier: MIT
The priority kit's CPU-load helper for Windows (docs/kiln/priority-kit.md): busy threads at one priority, in loaded and
idle phases on a schedule, with a hard time limit. Run it in its own PowerShell window while the game runs.

  powershell -ExecutionPolicy Bypass -File cpu-load.ps1                  # every logical CPU, Normal, the default schedule
  powershell -ExecutionPolicy Bypass -File cpu-load.ps1 -Threads 8 -Priority AboveNormal -LoadSeconds 120 -Cycles 3

The schedule: idle for -IdleSeconds, then -Cycles times (loaded for -LoadSeconds, idle for -IdleSeconds). The default is
2 + 3 x (2 + 2) = 14 minutes. It writes each phase's start and end (UTC) to a CSV the analysis reads
(cpu-load-<UTC time>.csv next to this script, or -Out).

Stopping: Ctrl+C or closing the window stops every busy thread at once (they belong to this PowerShell process). Each
thread also stops by itself at the end of its phase, and nothing runs past -MaxMinutes (default 60) in any case.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 256)][int]$Threads = [Environment]::ProcessorCount,
    [ValidateSet('BelowNormal', 'Normal', 'AboveNormal')][string]$Priority = 'Normal',
    [ValidateRange(1, 3600)][int]$LoadSeconds = 120,
    [ValidateRange(1, 3600)][int]$IdleSeconds = 120,
    [ValidateRange(1, 20)][int]$Cycles = 3,
    [ValidateRange(1, 240)][int]$MaxMinutes = 60,
    [string]$Out = ''
)
Set-StrictMode -Version 3
$ErrorActionPreference = 'Stop'

$total = $IdleSeconds + $Cycles * ($LoadSeconds + $IdleSeconds)
if ($total -gt $MaxMinutes * 60) {
    throw "The schedule takes $total s, more than -MaxMinutes $MaxMinutes. Shorten it or raise -MaxMinutes."
}
if (-not $Out) {
    $here = if ($PSScriptRoot) { $PSScriptRoot } else { (Get-Location).Path }
    $Out = Join-Path $here ('cpu-load-{0}.csv' -f [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
}

# The busy threads: background threads of this process (they end with it), each with its own deadline.
if (-not ('KilnPriorityKit.CpuLoad' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.Threading;
namespace KilnPriorityKit {
public static class CpuLoad {
    static volatile bool stop;
    static Thread[] threads = new Thread[0];
    public static double Sink;
    // Starts n busy threads at `priority` that stop by themselves after `seconds` (or at Stop()).
    public static void Start(int n, ThreadPriority priority, int seconds) {
        Stop();
        stop = false;
        long deadline = Stopwatch.GetTimestamp() + (long)seconds * Stopwatch.Frequency;
        threads = new Thread[n];
        for (int i = 0; i < n; i++) {
            var t = new Thread(() => {
                double x = 1.0;
                while (!stop && Stopwatch.GetTimestamp() < deadline) {
                    for (int k = 0; k < 100000; k++) x = x * 1.0000001 + 0.0000001;
                }
                Sink = x;
            });
            t.IsBackground = true;
            t.Priority = priority;
            t.Name = "kiln-cpu-load-" + i;
            threads[i] = t;
            t.Start();
        }
    }
    public static void Stop() {
        stop = true;
        foreach (var t in threads) if (t != null) t.Join(2000);
        threads = new Thread[0];
    }
    public static int Running() {
        int n = 0;
        foreach (var t in threads) if (t != null && t.IsAlive) n++;
        return n;
    }
}
}
'@
}

function UtcMs { [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
$prio = [System.Threading.ThreadPriority]::$Priority
$lines = New-Object System.Collections.Generic.List[string]
$lines.Add('phase,start_utc_ms,end_utc_ms,threads,priority')
$schedule = @(@{ Phase = 'idle'; Seconds = $IdleSeconds })
for ($c = 0; $c -lt $Cycles; $c++) {
    $schedule += @{ Phase = 'load'; Seconds = $LoadSeconds }
    $schedule += @{ Phase = 'idle'; Seconds = $IdleSeconds }
}
Write-Host ("CPU load: {0} thread(s) at {1}, {2} phase(s), {3:N1} minutes; Ctrl+C stops it. Phases go to {4}" -f `
        $Threads, $Priority, $schedule.Count, ($total / 60.0), $Out)
$hardStop = [DateTime]::UtcNow.AddMinutes($MaxMinutes)
$current = $null
$start = 0
try {
    foreach ($p in $schedule) {
        $start = UtcMs
        $current = $p
        $until = [DateTime]::UtcNow.AddSeconds($p.Seconds)
        if ($until -gt $hardStop) { $until = $hardStop }
        if ($p.Phase -eq 'load') {
            [KilnPriorityKit.CpuLoad]::Start($Threads, $prio, [int][Math]::Ceiling(($until - [DateTime]::UtcNow).TotalSeconds))
        }
        Write-Host ("{0:HH:mm:ss} {1} for {2} s" -f [DateTime]::Now, $p.Phase, $p.Seconds)
        while ([DateTime]::UtcNow -lt $until) { Start-Sleep -Milliseconds 200 }
        [KilnPriorityKit.CpuLoad]::Stop()
        $lines.Add(('{0},{1},{2},{3},{4}' -f $p.Phase, $start, (UtcMs), $(if ($p.Phase -eq 'load') { $Threads } else { 0 }), $Priority))
        $current = $null
        [System.IO.File]::WriteAllLines($Out, $lines)  # after every phase: a stop mid-way keeps what ran
        if ([DateTime]::UtcNow -ge $hardStop) { Write-Host 'The time limit is reached; stopping.'; break }
    }
}
finally {
    [KilnPriorityKit.CpuLoad]::Stop()
    if ($current) {  # stopped part-way: the phase as far as it ran
        $lines.Add(('{0},{1},{2},{3},{4}' -f $current.Phase, $start, (UtcMs), $(if ($current.Phase -eq 'load') { $Threads } else { 0 }), $Priority))
    }
    if ([KilnPriorityKit.CpuLoad]::Running() -gt 0) { Write-Warning 'Some busy threads did not stop; close this window.' }
    [System.IO.File]::WriteAllLines($Out, $lines)
    Write-Host "Done. Phases written to $Out"
}
