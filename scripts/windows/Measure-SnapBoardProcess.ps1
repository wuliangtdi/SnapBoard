[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [ValidateRange(1, 20)]
    [int]$Runs = 3,

    [ValidateRange(5, 600)]
    [int]$SampleSeconds = 30,

    [ValidateRange(5, 600)]
    [int]$ClosedSampleSeconds = 30,

    [ValidateRange(1, 60)]
    [int]$StartupTimeoutSeconds = 15,

    [string]$OutputDirectory = "artifacts/performance/windows"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "Measure-SnapBoardProcess.ps1 requires Windows."
}

$snapExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$snapOutputDirectory = if ([System.IO.Path]::IsPathFullyQualified($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath(
        (Join-Path -Path (Get-Location) -ChildPath $OutputDirectory))
}
[System.IO.Directory]::CreateDirectory($snapOutputDirectory) | Out-Null

$snapSamples = [System.Collections.Generic.List[object]]::new()
$snapSummaries = [System.Collections.Generic.List[object]]::new()
$snapLogicalProcessorCount = [System.Environment]::ProcessorCount

for ($snapRun = 1; $snapRun -le $Runs; $snapRun++) {
    $snapProcess = $null

    try {
        $snapStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        $snapProcess = Start-Process `
            -FilePath $snapExecutable `
            -PassThru `
            -WindowStyle Normal

        while ($true) {
            if ($snapProcess.HasExited) {
                throw "SnapBoard exited before creating a main window. ExitCode=$($snapProcess.ExitCode)"
            }

            $snapProcess.Refresh()
            if ($snapProcess.MainWindowHandle -ne [IntPtr]::Zero) {
                break
            }

            if ($snapStopwatch.Elapsed.TotalSeconds -ge $StartupTimeoutSeconds) {
                throw "SnapBoard did not create a main window within $StartupTimeoutSeconds seconds."
            }

            Start-Sleep -Milliseconds 25
        }

        $snapStopwatch.Stop()
        $snapStartupMilliseconds = [Math]::Round($snapStopwatch.Elapsed.TotalMilliseconds, 2)

        for ($snapSecond = 1; $snapSecond -le $SampleSeconds; $snapSecond++) {
            if ($snapProcess.HasExited) {
                throw "SnapBoard exited during sampling. ExitCode=$($snapProcess.ExitCode)"
            }

            $snapMetric = Get-CimInstance `
                -ClassName Win32_PerfFormattedData_PerfProc_Process `
                -Filter "IDProcess = $($snapProcess.Id)" `
                -ErrorAction SilentlyContinue

            if ($null -ne $snapMetric) {
                $snapSamples.Add([pscustomobject]@{
                    Run = $snapRun
                    Phase = "Visible"
                    SampleSecond = $snapSecond
                    ProcessId = $snapProcess.Id
                    StartupMilliseconds = $snapStartupMilliseconds
                    PrivateWorkingSetBytes = [uint64]$snapMetric.WorkingSetPrivate
                    PrivateBytes = [uint64]$snapMetric.PrivateBytes
                    HandleCount = [uint64]$snapMetric.HandleCount
                    CpuPercent = [Math]::Round(
                        [double]$snapMetric.PercentProcessorTime / $snapLogicalProcessorCount,
                        3)
                    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                })
            }

            Start-Sleep -Seconds 1
        }

        $snapVisibleSamples = @(
            $snapSamples | Where-Object { $_.Run -eq $snapRun -and $_.Phase -eq "Visible" })
        if ($snapVisibleSamples.Count -eq 0) {
            throw "No Win32 performance samples were available for process $($snapProcess.Id)."
        }

        $null = $snapProcess.CloseMainWindow()
        $snapCloseStopwatch = [System.Diagnostics.Stopwatch]::StartNew()
        while ($true) {
            if ($snapProcess.HasExited) {
                throw "SnapBoard exited after closing its main window instead of remaining resident."
            }

            $snapProcess.Refresh()
            if ($snapProcess.MainWindowHandle -eq [IntPtr]::Zero) {
                break
            }

            if ($snapCloseStopwatch.Elapsed.TotalSeconds -ge 10) {
                throw "SnapBoard main window did not unload within 10 seconds."
            }

            Start-Sleep -Milliseconds 25
        }

        $snapCloseStopwatch.Stop()
        $snapWindowUnloadMilliseconds = [Math]::Round(
            $snapCloseStopwatch.Elapsed.TotalMilliseconds,
            2)

        for ($snapSecond = 1; $snapSecond -le $ClosedSampleSeconds; $snapSecond++) {
            if ($snapProcess.HasExited) {
                throw "SnapBoard exited during closed-window sampling. ExitCode=$($snapProcess.ExitCode)"
            }

            $snapMetric = Get-CimInstance `
                -ClassName Win32_PerfFormattedData_PerfProc_Process `
                -Filter "IDProcess = $($snapProcess.Id)" `
                -ErrorAction SilentlyContinue

            if ($null -ne $snapMetric) {
                $snapSamples.Add([pscustomobject]@{
                    Run = $snapRun
                    Phase = "Closed"
                    SampleSecond = $snapSecond
                    ProcessId = $snapProcess.Id
                    StartupMilliseconds = $snapStartupMilliseconds
                    PrivateWorkingSetBytes = [uint64]$snapMetric.WorkingSetPrivate
                    PrivateBytes = [uint64]$snapMetric.PrivateBytes
                    HandleCount = [uint64]$snapMetric.HandleCount
                    CpuPercent = [Math]::Round(
                        [double]$snapMetric.PercentProcessorTime / $snapLogicalProcessorCount,
                        3)
                    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                })
            }

            Start-Sleep -Seconds 1
        }

        $snapClosedSamples = @(
            $snapSamples | Where-Object { $_.Run -eq $snapRun -and $_.Phase -eq "Closed" })
        if ($snapClosedSamples.Count -eq 0) {
            throw "No closed-window performance samples were available for process $($snapProcess.Id)."
        }

        $snapSummaries.Add([pscustomobject]@{
            Run = $snapRun
            StartupMilliseconds = $snapStartupMilliseconds
            WindowUnloadMilliseconds = $snapWindowUnloadMilliseconds
            VisiblePeakPrivateWorkingSetBytes =
                ($snapVisibleSamples.PrivateWorkingSetBytes | Measure-Object -Maximum).Maximum
            VisiblePeakPrivateBytes =
                ($snapVisibleSamples.PrivateBytes | Measure-Object -Maximum).Maximum
            VisiblePeakHandleCount =
                ($snapVisibleSamples.HandleCount | Measure-Object -Maximum).Maximum
            VisibleAverageCpuPercent = [Math]::Round(
                ($snapVisibleSamples.CpuPercent | Measure-Object -Average).Average,
                3)
            ClosedPeakPrivateWorkingSetBytes =
                ($snapClosedSamples.PrivateWorkingSetBytes | Measure-Object -Maximum).Maximum
            ClosedFinalPrivateWorkingSetBytes = $snapClosedSamples[-1].PrivateWorkingSetBytes
            ClosedPeakPrivateBytes =
                ($snapClosedSamples.PrivateBytes | Measure-Object -Maximum).Maximum
            ClosedFinalPrivateBytes = $snapClosedSamples[-1].PrivateBytes
            ClosedPeakHandleCount =
                ($snapClosedSamples.HandleCount | Measure-Object -Maximum).Maximum
            ClosedFinalHandleCount = $snapClosedSamples[-1].HandleCount
            ClosedAverageCpuPercent = [Math]::Round(
                ($snapClosedSamples.CpuPercent | Measure-Object -Average).Average,
                3)
            PrivateWorkingSetDropBytes =
                ($snapVisibleSamples.PrivateWorkingSetBytes | Measure-Object -Maximum).Maximum -
                $snapClosedSamples[-1].PrivateWorkingSetBytes
        })
    }
    finally {
        if ($null -ne $snapProcess -and -not $snapProcess.HasExited) {
            $snapExitSignal = $null
            try {
                $snapExitSignal = Start-Process `
                    -FilePath $snapExecutable `
                    -ArgumentList "--exit" `
                    -PassThru `
                    -WindowStyle Hidden
                $null = $snapExitSignal.WaitForExit(5000)
            }
            finally {
                if ($null -ne $snapExitSignal) {
                    $snapExitSignal.Dispose()
                }
            }

            if (-not $snapProcess.WaitForExit(5000)) {
                Stop-Process -Id $snapProcess.Id -Force
                $snapProcess.WaitForExit()
            }
        }

        if ($null -ne $snapProcess) {
            $snapProcess.Dispose()
        }
    }
}

$snapTimestamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$snapSamplesPath = Join-Path $snapOutputDirectory "snapboard-$snapTimestamp-samples.csv"
$snapSummaryPath = Join-Path $snapOutputDirectory "snapboard-$snapTimestamp-summary.json"
$snapMetadataPath = Join-Path $snapOutputDirectory "snapboard-$snapTimestamp-environment.json"

$snapSamples | Export-Csv -LiteralPath $snapSamplesPath -NoTypeInformation -Encoding utf8
$snapSummaries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $snapSummaryPath -Encoding utf8

$snapOperatingSystem = Get-CimInstance Win32_OperatingSystem
$snapProcessor = Get-CimInstance Win32_Processor | Select-Object -First 1
$snapComputer = Get-CimInstance Win32_ComputerSystem
$snapMetadata = [pscustomobject]@{
    MeasurementKind = "process-cold-visible-and-closed-window"
    Executable = $snapExecutable
    Runs = $Runs
    SampleSeconds = $SampleSeconds
    ClosedSampleSeconds = $ClosedSampleSeconds
    OperatingSystem = $snapOperatingSystem.Caption
    OperatingSystemVersion = $snapOperatingSystem.Version
    Architecture = $snapOperatingSystem.OSArchitecture
    Processor = $snapProcessor.Name
    TotalPhysicalMemoryBytes = [uint64]$snapComputer.TotalPhysicalMemory
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Note = "This is a process-cold visible-window measurement followed by a closed-window resident phase. It is not an OS-cold boot or a 10-minute endurance sample."
}
$snapMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $snapMetadataPath -Encoding utf8

$snapSummaries | Format-Table -AutoSize
Write-Output "Samples: $snapSamplesPath"
Write-Output "Summary: $snapSummaryPath"
Write-Output "Environment: $snapMetadataPath"
