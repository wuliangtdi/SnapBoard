[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExecutablePath,

    [ValidateRange(1, 20)]
    [int]$Runs = 3,

    [ValidateRange(5, 600)]
    [int]$SampleSeconds = 30,

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
$snapOutputDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path -Path (Get-Location) -ChildPath $OutputDirectory))
[System.IO.Directory]::CreateDirectory($snapOutputDirectory) | Out-Null

$snapSamples = [System.Collections.Generic.List[object]]::new()
$snapSummaries = [System.Collections.Generic.List[object]]::new()

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
                    SampleSecond = $snapSecond
                    ProcessId = $snapProcess.Id
                    StartupMilliseconds = $snapStartupMilliseconds
                    PrivateWorkingSetBytes = [uint64]$snapMetric.WorkingSetPrivate
                    PrivateBytes = [uint64]$snapMetric.PrivateBytes
                    HandleCount = [uint64]$snapMetric.HandleCount
                    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
                })
            }

            Start-Sleep -Seconds 1
        }

        $snapRunSamples = @($snapSamples | Where-Object Run -eq $snapRun)
        if ($snapRunSamples.Count -eq 0) {
            throw "No Win32 performance samples were available for process $($snapProcess.Id)."
        }

        $snapSummaries.Add([pscustomobject]@{
            Run = $snapRun
            StartupMilliseconds = $snapStartupMilliseconds
            PeakPrivateWorkingSetBytes = ($snapRunSamples.PrivateWorkingSetBytes | Measure-Object -Maximum).Maximum
            PeakPrivateBytes = ($snapRunSamples.PrivateBytes | Measure-Object -Maximum).Maximum
            PeakHandleCount = ($snapRunSamples.HandleCount | Measure-Object -Maximum).Maximum
        })
    }
    finally {
        if ($null -ne $snapProcess -and -not $snapProcess.HasExited) {
            $null = $snapProcess.CloseMainWindow()
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
    MeasurementKind = "process-cold-visible-window"
    Executable = $snapExecutable
    Runs = $Runs
    SampleSeconds = $SampleSeconds
    OperatingSystem = $snapOperatingSystem.Caption
    OperatingSystemVersion = $snapOperatingSystem.Version
    Architecture = $snapOperatingSystem.OSArchitecture
    Processor = $snapProcessor.Name
    TotalPhysicalMemoryBytes = [uint64]$snapComputer.TotalPhysicalMemory
    CapturedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    Note = "This is a process-cold visible-window measurement. It is not an OS-cold boot and not a tray-resident measurement."
}
$snapMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $snapMetadataPath -Encoding utf8

$snapSummaries | Format-Table -AutoSize
Write-Output "Samples: $snapSamplesPath"
Write-Output "Summary: $snapSummaryPath"
Write-Output "Environment: $snapMetadataPath"
