[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath,

    [switch]$DumpMetadata
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "Verify-VelopackMsi.ps1 requires Windows."
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if ([System.IO.Path]::GetExtension($resolvedMsiPath) -ne ".msi") {
    throw "Expected an MSI package: $resolvedMsiPath"
}

function Read-MsiRows {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Query,

        [Parameter(Mandatory = $true)]
        [string[]]$Columns
    )

    $invokeMethod = [System.Reflection.BindingFlags]::InvokeMethod
    $getProperty = [System.Reflection.BindingFlags]::GetProperty
    $view = $Database.GetType().InvokeMember(
        "OpenView",
        $invokeMethod,
        $null,
        $Database,
        @($Query))

    try {
        $null = $view.GetType().InvokeMember(
            "Execute",
            $invokeMethod,
            $null,
            $view,
            $null)

        $rows = [System.Collections.Generic.List[object]]::new()
        while ($true) {
            $record = $view.GetType().InvokeMember(
                "Fetch",
                $invokeMethod,
                $null,
                $view,
                $null)
            if ($null -eq $record) {
                break
            }

            try {
                $values = [ordered]@{}
                for ($index = 0; $index -lt $Columns.Count; $index++) {
                    $values[$Columns[$index]] = $record.GetType().InvokeMember(
                        "StringData",
                        $getProperty,
                        $null,
                        $record,
                        @($index + 1))
                }

                $rows.Add([pscustomobject]$values)
            }
            finally {
                $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
            }
        }

        return $rows.ToArray()
    }
    finally {
        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null

try {
    $database = $installer.GetType().InvokeMember(
        "OpenDatabase",
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $installer,
        @($resolvedMsiPath, 0))

    $properties = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Property`, `Value` FROM `Property`' `
        -Columns @("Property", "Value")
    $dialogs = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Dialog`, `Width`, `Height`, `Title`, `Control_First`, `Control_Default`, `Control_Cancel` FROM `Dialog`' `
        -Columns @("Dialog", "Width", "Height", "Title", "FirstControl", "DefaultControl", "CancelControl")
    $controls = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help` FROM `Control`' `
        -Columns @("Dialog", "Control", "Type", "X", "Y", "Width", "Height", "Attributes", "Property", "Text", "NextControl", "Help")
    $radioButtons = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Property`, `Order`, `Value`, `X`, `Y`, `Width`, `Height`, `Text`, `Help` FROM `RadioButton`' `
        -Columns @("Property", "Order", "Value", "X", "Y", "Width", "Height", "Text", "Help")
    $directories = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Directory`, `Directory_Parent`, `DefaultDir` FROM `Directory`' `
        -Columns @("Directory", "Parent", "DefaultDirectory")
    $controlEvents = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering` FROM `ControlEvent`' `
        -Columns @("Dialog", "Control", "Event", "Argument", "Condition", "Ordering")
    $controlConditions = Read-MsiRows `
        -Database $database `
        -Query 'SELECT `Dialog_`, `Control_`, `Action`, `Condition` FROM `ControlCondition`' `
        -Columns @("Dialog", "Control", "Action", "Condition")

    $metadata = [pscustomobject]@{
        Properties = @($properties | Where-Object {
            $_.Property -match 'ALLUSERS|MSIINSTALLPERUSER|INSTALLDIR|INSTALLFOLDER|WIXUI|VELOPACK|WixAppFolder|ApplicationFolderName'
        })
        Dialogs = @($dialogs)
        DirectoryControls = @($controls | Where-Object {
            $_.Type -in @("DirectoryCombo", "DirectoryList", "PathEdit")
        })
        ScopeControls = @($controls | Where-Object {
            $_.Dialog -eq "InstallScopeDlg" -or
            $_.Property -match 'ALLUSERS|MSIINSTALLPERUSER'
        })
        ScopeRadioButtons = @($radioButtons | Where-Object {
            $_.Property -match 'ALLUSERS|MSIINSTALLPERUSER|WixAppFolder'
        })
        InstallDirectories = @($directories | Where-Object {
            $_.Directory -match 'INSTALLDIR|INSTALLFOLDER' -or
            $_.Parent -match 'INSTALLDIR|INSTALLFOLDER'
        })
        InstallScopeEvents = @($controlEvents | Where-Object {
            $_.Dialog -eq "InstallScopeDlg"
        })
        InstallScopeConditions = @($controlConditions | Where-Object {
            $_.Dialog -eq "InstallScopeDlg"
        })
        BrowseEvents = @($controlEvents | Where-Object {
            $_.Dialog -eq "BrowseDlg"
        })
        NavigationEvents = @($controlEvents | Where-Object {
            $_.Event -eq "NewDialog"
        })
    }

    if ($DumpMetadata) {
        $metadata | ConvertTo-Json -Depth 6
        return
    }

    $installPathControls = @($controls | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "SnapBoardInstallPath"
    })
    if ($installPathControls.Count -ne 1 -or
        $installPathControls[0].Type -ne "PathEdit" -or
        $installPathControls[0].Property -ne "INSTALLFOLDER") {
        throw "The MSI does not contain SnapBoard's installation-directory editor."
    }

    $installPathAttributes = [int]$installPathControls[0].Attributes
    if (($installPathAttributes -band 8) -ne 0) {
        throw "The installation-directory editor must bind directly to INSTALLFOLDER."
    }
    if (($installPathAttributes -band 2) -ne 0) {
        throw "The final installation directory must be read-only so a shared parent cannot become the app root."
    }

    $installParents = @($directories | Where-Object {
        $_.Directory -eq "SNAPBOARD_INSTALL_PARENT" -and
        $_.Parent -eq "TARGETDIR"
    })
    if ($installParents.Count -ne 1) {
        throw "The MSI does not contain SnapBoard's selectable parent directory."
    }

    $browseButtons = @($controls | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "SnapBoardBrowsePath" -and
        $_.Type -eq "PushButton"
    })
    $browsePropertyEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "SnapBoardBrowsePath" -and
        $_.Event -eq "[_BrowseProperty]" -and
        $_.Argument -eq "SNAPBOARD_INSTALL_PARENT"
    })
    $browseDialogEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "SnapBoardBrowsePath" -and
        $_.Event -eq "SpawnDialog" -and
        $_.Argument -eq "BrowseDlg"
    })
    if ($browseButtons.Count -ne 1 -or
        $browsePropertyEvents.Count -ne 1 -or
        $browseDialogEvents.Count -ne 1) {
        throw "The MSI installation-directory browser is not fully connected."
    }

    $scopeRadioControls = @($controls | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "BothScopes"
    })
    $browseDisableConditions = @($controlConditions | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "SnapBoardBrowsePath" -and
        $_.Action -eq "Disable" -and
        $_.Condition -eq "VELOPACK_INSTALLDIR"
    })
    if ($scopeRadioControls.Count -ne 1 -or
        $scopeRadioControls[0].NextControl -ne "SnapBoardBrowsePath" -or
        $installPathControls[0].NextControl -ne "" -or
        $browseButtons[0].NextControl -ne "Back" -or
        $browseDisableConditions.Count -ne 1) {
        throw "The MSI scope dialog has an invalid tab order or command-line path lock."
    }

    $browseInstallPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "[INSTALLFOLDER]" -and
        $_.Argument -eq "[SNAPBOARD_INSTALL_PARENT]SnapBoard"
    })
    $browseValidationPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "[WIXUI_INSTALLDIR]" -and
        $_.Argument -eq "[INSTALLFOLDER]" -and
        $_.Ordering -eq "5"
    })
    $browseValidationActionEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "DoAction" -and
        $_.Argument -eq "RustValidatePath" -and
        $_.Ordering -eq "6"
    })
    $browseInvalidPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "SpawnDialog" -and
        $_.Argument -eq "InvalidDirDlg"
    })
    $browseCloseEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "EndDialog" -and
        $_.Argument -eq "Return" -and
        $_.Condition -eq "1" -and
        $_.Ordering -eq "8"
    })
    $browseCustomParentEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "BrowseDlg" -and
        $_.Control -eq "OK" -and
        $_.Event -eq "[SNAPBOARD_CUSTOM_INSTALL_PARENT]" -and
        $_.Argument -eq "1" -and
        $_.Ordering -eq "7"
    })
    if ($browseInstallPathEvents.Count -ne 1 -or
        $browseValidationPathEvents.Count -ne 1 -or
        $browseValidationActionEvents.Count -ne 1 -or
        $browseInvalidPathEvents.Count -ne 0 -or
        $browseCloseEvents.Count -ne 1 -or
        $browseCustomParentEvents.Count -ne 1) {
        throw "The MSI browser does not create and validate an isolated SnapBoard app directory."
    }

    if ($metadata.ScopeControls.Count -eq 0 -or $metadata.ScopeRadioButtons.Count -lt 2) {
        throw "The MSI does not allow the user to choose between per-user and per-machine installation."
    }

    $overwritingNextEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "[INSTALLFOLDER]"
    })
    if ($overwritingNextEvents.Count -ne 0) {
        throw "The MSI overwrites the user-selected installation directory when Next is pressed."
    }

    $scopeDefaultParentEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "BothScopes" -and
        $_.Event -eq "[SNAPBOARD_INSTALL_PARENT]" -and
        $_.Argument -in @("[LocalAppDataFolder]Programs", "[ProgramFiles64Folder]") -and
        $_.Condition -match "NOT SNAPBOARD_CUSTOM_INSTALL_PARENT"
    })
    if ($scopeDefaultParentEvents.Count -ne 2) {
        throw "The MSI does not preserve a user-selected directory when installation scope changes."
    }

    $welcomeValidationPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "WelcomeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "[WIXUI_INSTALLDIR]" -and
        $_.Argument -eq "[INSTALLFOLDER]" -and
        $_.Ordering -eq "8"
    })
    $welcomeValidationActionEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "WelcomeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "DoAction" -and
        $_.Argument -eq "RustValidatePath" -and
        $_.Ordering -eq "9"
    })
    $scopeSelectionValidationPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "BothScopes" -and
        $_.Event -eq "[WIXUI_INSTALLDIR]" -and
        $_.Argument -eq "[INSTALLFOLDER]" -and
        $_.Ordering -eq "6"
    })
    $scopeSelectionValidationActionEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "BothScopes" -and
        $_.Event -eq "DoAction" -and
        $_.Argument -eq "RustValidatePath" -and
        $_.Ordering -eq "7"
    })
    $scopeNextValidationActionEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "DoAction" -and
        $_.Argument -eq "RustValidatePath"
    })
    $scopeInvalidPathEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "SpawnDialog" -and
        $_.Argument -eq "InvalidDirDlg" -and
        $_.Condition -match 'WIXUI_INSTALLDIR_VALID<>"1"' -and
        $_.Ordering -eq "7"
    })
    $scopeReadyEvents = @($controlEvents | Where-Object {
        $_.Dialog -eq "InstallScopeDlg" -and
        $_.Control -eq "Next" -and
        $_.Event -eq "NewDialog" -and
        $_.Argument -eq "VerifyReadyDlg" -and
        $_.Condition -match 'WIXUI_INSTALLDIR_VALID="1"' -and
        $_.Ordering -eq "8"
    })
    if ($welcomeValidationPathEvents.Count -ne 1 -or
        $welcomeValidationActionEvents.Count -ne 1 -or
        $scopeSelectionValidationPathEvents.Count -ne 1 -or
        $scopeSelectionValidationActionEvents.Count -ne 1 -or
        $scopeNextValidationActionEvents.Count -ne 0 -or
        $scopeInvalidPathEvents.Count -ne 1 -or
        $scopeReadyEvents.Count -ne 1) {
        throw "The MSI does not cache path validation before the scope dialog reads its result."
    }

    $allUsers = @($properties | Where-Object { $_.Property -eq "ALLUSERS" })
    if ($allUsers.Count -ne 1 -or $allUsers[0].Value -ne "2") {
        throw "The MSI must use ALLUSERS=2 so installation scope is selected at runtime."
    }

    Write-Output "Verified selectable Windows MSI: $resolvedMsiPath"
}
finally {
    if ($null -ne $database) {
        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }

    $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
