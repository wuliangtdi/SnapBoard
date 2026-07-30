[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$MsiPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

if ([System.Environment]::OSVersion.Platform -ne [System.PlatformID]::Win32NT) {
    throw "Customize-VelopackMsi.ps1 requires Windows."
}

$resolvedMsiPath = (Resolve-Path -LiteralPath $MsiPath).Path
if ([System.IO.Path]::GetExtension($resolvedMsiPath) -ne ".msi") {
    throw "Expected an MSI package: $resolvedMsiPath"
}

function Invoke-MsiStatement {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Statement
    )

    $view = $Database.GetType().InvokeMember(
        "OpenView",
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $Database,
        @($Statement))

    try {
        $null = $view.GetType().InvokeMember(
            "Execute",
            [System.Reflection.BindingFlags]::InvokeMethod,
            $null,
            $view,
            $null)
    }
    finally {
        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

function Test-MsiRow {
    param(
        [Parameter(Mandatory = $true)]
        [object]$Database,

        [Parameter(Mandatory = $true)]
        [string]$Query
    )

    $invokeMethod = [System.Reflection.BindingFlags]::InvokeMethod
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
        $record = $view.GetType().InvokeMember(
            "Fetch",
            $invokeMethod,
            $null,
            $view,
            $null)
        if ($null -eq $record) {
            return $false
        }

        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        return $true
    }
    finally {
        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($view)
    }
}

$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null

try {
    # Direct MSI customization must happen before Authenticode signing.
    $database = $installer.GetType().InvokeMember(
        "OpenDatabase",
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $installer,
        @($resolvedMsiPath, 1))

    $requiredRows = @(
        @{
            Description = "Velopack InstallScopeDlg"
            Query = "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog`` = 'InstallScopeDlg'"
        },
        @{
            Description = "Velopack BrowseDlg"
            Query = "SELECT ``Dialog`` FROM ``Dialog`` WHERE ``Dialog`` = 'BrowseDlg'"
        },
        @{
            Description = "Velopack scope selector"
            Query = "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'InstallScopeDlg' AND ``Control`` = 'BothScopes'"
        },
        @{
            Description = "Velopack install directory"
            Query = "SELECT ``Directory`` FROM ``Directory`` WHERE ``Directory`` = 'INSTALLFOLDER'"
        }
    )

    foreach ($requiredRow in $requiredRows) {
        if (-not (Test-MsiRow -Database $database -Query $requiredRow.Query)) {
            throw "Missing $($requiredRow.Description); the pinned Velopack MSI schema changed."
        }
    }

    if (Test-MsiRow `
        -Database $database `
        -Query "SELECT ``Control`` FROM ``Control`` WHERE ``Dialog_`` = 'InstallScopeDlg' AND ``Control`` = 'SnapBoardInstallPath'") {
        throw "The MSI already contains SnapBoard's installation-directory controls."
    }

    if (Test-MsiRow `
        -Database $database `
        -Query "SELECT ``Name`` FROM ``_Tables`` WHERE ``Name`` = 'MsiDigitalSignature'") {
        throw "The MSI is already signed. Customize it before applying Authenticode signatures."
    }

    $statements = @(
        'INSERT INTO `Directory` (`Directory`, `Directory_Parent`, `DefaultDir`) VALUES (''SNAPBOARD_INSTALL_PARENT'', ''TARGETDIR'', ''.'')',
        'UPDATE `Control` SET `Control_Next` = ''SnapBoardBrowsePath'' WHERE `Dialog_` = ''InstallScopeDlg'' AND `Control` = ''BothScopes''',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallScopeDlg'', ''SnapBoardInstallPathLabel'', ''Text'', 25, 179, 327, 14, 3, '''', ''安装位置（将在所选位置下创建 SnapBoard 文件夹）：'', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallScopeDlg'', ''SnapBoardInstallPath'', ''PathEdit'', 25, 197, 265, 18, 5, ''INSTALLFOLDER'', '''', '''', '''')',
        'INSERT INTO `Control` (`Dialog_`, `Control`, `Type`, `X`, `Y`, `Width`, `Height`, `Attributes`, `Property`, `Text`, `Control_Next`, `Help`) VALUES (''InstallScopeDlg'', ''SnapBoardBrowsePath'', ''PushButton'', 296, 197, 56, 18, 3, '''', ''浏览(&R)...'', ''Back'', '''')',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''SnapBoardBrowsePath'', ''[_BrowseProperty]'', ''SNAPBOARD_INSTALL_PARENT'', ''1'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''SnapBoardBrowsePath'', ''SpawnDialog'', ''BrowseDlg'', ''1'', 2)',
        'INSERT INTO `ControlCondition` (`Dialog_`, `Control_`, `Action`, `Condition`) VALUES (''InstallScopeDlg'', ''SnapBoardBrowsePath'', ''Disable'', ''VELOPACK_INSTALLDIR'')',
        'UPDATE `ControlEvent` SET `Ordering` = 11 WHERE `Dialog_` = ''WelcomeDlg'' AND `Control_` = ''Next'' AND `Event` = ''NewDialog'' AND `Argument` = ''InstallScopeDlg'' AND `Condition` = ''NOT Installed''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[WixAppFolder]'', ''WixPerUserFolder'', ''NOT Privileged AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[SNAPBOARD_INSTALL_PARENT]'', ''[LocalAppDataFolder]Programs'', ''WixAppFolder = "WixPerUserFolder" AND NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 2)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[SNAPBOARD_INSTALL_PARENT]'', ''[ProgramFiles64Folder]'', ''WixAppFolder = "WixPerMachineFolder" AND NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 3)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[INSTALLFOLDER]'', ''[VELOPACK_INSTALLDIR]'', ''VELOPACK_INSTALLDIR AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 4)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[INSTALLFOLDER]'', ''[SNAPBOARD_INSTALL_PARENT]SnapBoard'', ''NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 5)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''SetTargetPath'', ''SNAPBOARD_INSTALL_PARENT'', ''NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 6)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''SetTargetPath'', ''INSTALLFOLDER'', ''NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 7)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[WIXUI_INSTALLDIR]'', ''[INSTALLFOLDER]'', ''NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 8)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''DoAction'', ''RustValidatePath'', ''NOT WIXUI_DONTVALIDATEPATH AND NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 9)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''WelcomeDlg'', ''Next'', ''[SNAPBOARD_INSTALL_SCOPE_INITIALIZED]'', ''1'', ''NOT SNAPBOARD_INSTALL_SCOPE_INITIALIZED'', 10)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''[SNAPBOARD_INSTALL_PARENT]'', ''[LocalAppDataFolder]Programs'', ''WixAppFolder = "WixPerUserFolder" AND NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_CUSTOM_INSTALL_PARENT'', 1)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''[SNAPBOARD_INSTALL_PARENT]'', ''[ProgramFiles64Folder]'', ''WixAppFolder = "WixPerMachineFolder" AND NOT VELOPACK_INSTALLDIR AND NOT SNAPBOARD_CUSTOM_INSTALL_PARENT'', 2)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''[INSTALLFOLDER]'', ''[SNAPBOARD_INSTALL_PARENT]SnapBoard'', ''NOT VELOPACK_INSTALLDIR'', 3)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''SetTargetPath'', ''SNAPBOARD_INSTALL_PARENT'', ''NOT VELOPACK_INSTALLDIR'', 4)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''SetTargetPath'', ''INSTALLFOLDER'', ''NOT VELOPACK_INSTALLDIR'', 5)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''[WIXUI_INSTALLDIR]'', ''[INSTALLFOLDER]'', ''NOT VELOPACK_INSTALLDIR'', 6)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''BothScopes'', ''DoAction'', ''RustValidatePath'', ''NOT VELOPACK_INSTALLDIR AND NOT WIXUI_DONTVALIDATEPATH'', 7)',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''InstallScopeDlg'' AND `Control_` = ''Next'' AND `Event` = ''[INSTALLFOLDER]''',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''InstallScopeDlg'' AND `Control_` = ''Next'' AND `Event` = ''DoAction'' AND `Argument` = ''RustValidatePath''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''Next'', ''SpawnDialog'', ''InvalidDirDlg'', ''NOT WIXUI_DONTVALIDATEPATH AND WIXUI_INSTALLDIR_VALID<>"1"'', 7)',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''InstallScopeDlg'' AND `Control_` = ''Next'' AND `Event` = ''NewDialog'' AND `Argument` = ''VerifyReadyDlg''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''Next'', ''NewDialog'', ''VerifyReadyDlg'', ''WIXUI_DONTVALIDATEPATH OR WIXUI_INSTALLDIR_VALID="1"'', 8)',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''InstallScopeDlg'' AND `Control_` = ''Next'' AND `Event` = ''DoAction'' AND `Argument` = ''FindRelatedProducts''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''InstallScopeDlg'', ''Next'', ''DoAction'', ''FindRelatedProducts'', ''WIXUI_DONTVALIDATEPATH OR WIXUI_INSTALLDIR_VALID="1"'', 9)',
        'UPDATE `ControlEvent` SET `Ordering` = 2 WHERE `Dialog_` = ''BrowseDlg'' AND `Control_` = ''OK'' AND `Event` = ''SetTargetPath'' AND `Argument` = ''[_BrowseProperty]''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''[INSTALLFOLDER]'', ''[SNAPBOARD_INSTALL_PARENT]SnapBoard'', ''1'', 3)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''SetTargetPath'', ''INSTALLFOLDER'', ''1'', 4)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''[WIXUI_INSTALLDIR]'', ''[INSTALLFOLDER]'', ''1'', 5)',
        'UPDATE `ControlEvent` SET `Ordering` = 6 WHERE `Dialog_` = ''BrowseDlg'' AND `Control_` = ''OK'' AND `Event` = ''DoAction'' AND `Argument` = ''RustValidatePath''',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''BrowseDlg'' AND `Control_` = ''OK'' AND `Event` = ''SpawnDialog'' AND `Argument` = ''InvalidDirDlg''',
        'DELETE FROM `ControlEvent` WHERE `Dialog_` = ''BrowseDlg'' AND `Control_` = ''OK'' AND `Event` = ''EndDialog'' AND `Argument` = ''Return''',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''[SNAPBOARD_CUSTOM_INSTALL_PARENT]'', ''1'', ''1'', 7)',
        'INSERT INTO `ControlEvent` (`Dialog_`, `Control_`, `Event`, `Argument`, `Condition`, `Ordering`) VALUES (''BrowseDlg'', ''OK'', ''EndDialog'', ''Return'', ''1'', 8)'
    )

    foreach ($statement in $statements) {
        try {
            Invoke-MsiStatement -Database $database -Statement $statement
        }
        catch {
            throw [System.InvalidOperationException]::new(
                "Failed to customize MSI with statement: $statement",
                $_.Exception)
        }
    }

    $null = $database.GetType().InvokeMember(
        "Commit",
        [System.Reflection.BindingFlags]::InvokeMethod,
        $null,
        $database,
        $null)

    Write-Output "Added selectable installation directory to: $resolvedMsiPath"
}
finally {
    if ($null -ne $database) {
        $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($database)
    }

    $null = [System.Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer)
}
