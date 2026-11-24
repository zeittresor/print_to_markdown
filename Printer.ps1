param(
    [Parameter(Mandatory)][ValidateSet('Install', 'Remove')][string]$Action,
    [Parameter(Mandatory)][ValidatePattern('^S-1-[0-9-]+$')][string]$OwnerSid,
    [Parameter(Mandatory)][string]$LogPath
)

$ErrorActionPreference = 'Stop'
$printerName = 'Print to Markdown (.md)'
$legacyName = 'Print to Markdown (XPS)'
$knownDrivers = @('Microsoft XPS Document Writer v4', 'Microsoft XPS Document Writer')
$portName = 'PrintToMarkdownXPS_127.0.0.1_19101'
$createdPort = $false
$step = 'initialization'
$recording = $false
$portNumber = 19101

function Get-LocalPort {
    Get-CimInstance -ClassName Win32_TCPIPPrinterPort -Filter "Name='$portName'"
}

function Invoke-PortTool([string[]]$Options) {
    $scripts = Join-Path $env:WINDIR 'System32\Printing_Admin_Scripts'
    $portTool = Get-ChildItem -LiteralPath $scripts -Filter 'prnport.vbs' -Recurse | Select-Object -First 1
    if (!$portTool) { throw 'The Windows prnport.vbs utility is missing.' }
    & (Join-Path $env:WINDIR 'System32\cscript.exe') '//nologo' $portTool.FullName @Options |
        ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { throw "Windows prnport.vbs failed with exit code $LASTEXITCODE." }
}

function Test-ExpectedPort($Instance) {
    return ($Instance -and $Instance.HostAddress -eq '127.0.0.1' -and
        $Instance.PortNumber -eq $portNumber -and $Instance.Protocol -eq 1)
}

try {
    Start-Transcript -LiteralPath $LogPath | Out-Null
    $recording = $true
    $step = 'load Windows print management'
    Import-Module PrintManagement
    $step = 'check Windows Print Spooler'
    $spooler = Get-Service -Name 'Spooler'
    Write-Host "Print Spooler status: $($spooler.Status)"
    if ($spooler.Status -ne 'Running') {
        Start-Service -Name 'Spooler'
    }

    $step = 'inspect existing printer and port'
    $printers = Get-Printer -Full
    $printer = $printers | Where-Object Name -eq $printerName | Select-Object -First 1
    $legacy = $printers | Where-Object { $_.Name -eq $legacyName -and $_.PortName -eq $portName } | Select-Object -First 1
    if ($printer -and $legacy) {
        throw "Both '$printerName' and '$legacyName' exist on the same port. Resolve the duplicate queue in Windows printer settings."
    }
    if (!$printer) { $printer = $legacy }
    $port = Get-PrinterPort | Where-Object Name -eq $portName | Select-Object -First 1
    $xpsQueue = $printers | Where-Object {
        $_.Name -eq 'Microsoft XPS Document Writer' -or $_.DriverName -in $knownDrivers
    } | Select-Object -First 1
    $drivers = $knownDrivers
    if ($xpsQueue -and $xpsQueue.DriverName) {
        $drivers = @($xpsQueue.DriverName) + $knownDrivers
    }

    if ($printer -and ($printer.DriverName -notin $drivers -or $printer.PortName -ne $portName)) {
        throw "The queue '$($printer.Name)' has an unexpected driver or port."
    }
    $ownerPattern = [regex]::Escape($OwnerSid) + '(?=\))'
    if ($printer -and $printer.PermissionSDDL -and $printer.PermissionSDDL -notmatch $ownerPattern) {
        throw "The '$($printer.Name)' queue belongs to a different Windows account."
    }
    if ($port) {
        $tcpPort = Get-LocalPort
        if (!(Test-ExpectedPort $tcpPort)) {
            throw "An incompatible port already uses the name '$portName'."
        }
    }

    if ($Action -eq 'Remove') {
        $step = 'remove printer and port'
        if ($printer) { Remove-Printer -Name $printer.Name }
        if ($port -and !(Get-Printer | Where-Object PortName -eq $portName)) {
            Remove-PrinterPort -Name $portName
        }
        Write-Host "Removal complete: $printerName"
        exit 0
    }

    $step = 'resolve installed XPS driver'
    $installed = @(Get-PrinterDriver | Select-Object -ExpandProperty Name)
    $driverName = $drivers | Where-Object { $_ -in $installed } | Select-Object -First 1
    if (!$driverName) {
        $step = 'enable optional Windows XPS feature'
        $featureName = 'Printing-XPSServices-Features'
        $feature = Get-WindowsOptionalFeature -Online -FeatureName $featureName
        Write-Host "Windows XPS feature: $($feature.State)"
        if ($feature.State -eq 'Disabled') {
            $change = Enable-WindowsOptionalFeature -Online -FeatureName $featureName -NoRestart -LimitAccess
            if ($change.RestartNeeded) {
                throw 'Windows requires a restart. Run Setup again after restarting.'
            }
        }
        foreach ($candidate in $knownDrivers) {
            try {
                if (!(Get-PrinterDriver -Name $candidate -ErrorAction SilentlyContinue)) {
                    Add-PrinterDriver -Name $candidate
                }
                $driverName = (Get-PrinterDriver -Name $candidate).Name
                break
            } catch {
                Write-Host "Unable to load driver '$candidate': $($_.Exception.Message)"
            }
        }
    }
    if (!$driverName) {
        throw "Enable 'Microsoft XPS Document Writer' in optionalfeatures.exe and run Setup again."
    }
    Write-Host "Selected driver: $driverName"

    $step = 'create and validate local RAW TCP port'
    if (!$port) {
        Write-Host "Creating port $portName for 127.0.0.1:$portNumber with SNMP disabled."
        try {
            Invoke-PortTool -Options @('-a', '-r', $portName, '-h', '127.0.0.1', '-o', 'raw', '-n', "$portNumber", '-md')
        } catch {
            Write-Host "prnport.vbs reported an error: $($_.Exception.Message)"
        }
        $tcpPort = Get-LocalPort
        if (!$tcpPort) {
            Write-Host 'Port not found after prnport.vbs. Trying Add-PrinterPort with its default SNMP settings.'
            Add-PrinterPort -Name $portName -PrinterHostAddress '127.0.0.1' -PortNumber $portNumber
            $tcpPort = Get-LocalPort
        }
        $createdPort = Test-ExpectedPort $tcpPort
        if (!(Test-ExpectedPort $tcpPort)) {
            throw 'Windows did not create the expected local RAW TCP port.'
        }
    }
    if ($tcpPort.SNMPEnabled) {
        Write-Host "Disabling SNMP on port $portName."
        Invoke-PortTool -Options @('-t', '-r', $portName, '-md')
        $tcpPort = Get-LocalPort
        if (!(Test-ExpectedPort $tcpPort) -or $tcpPort.SNMPEnabled) {
            throw 'The RAW TCP port could not be verified with SNMP disabled.'
        }
    }

    $step = 'create printer queue'
    if (!$printer) {
        # PRINTER_ALL_ACCESS (SYSTEM and Administrators); PRINTER_READ/USE (installing account).
        $security = "D:(A;;0x000F000C;;;SY)(A;;0x000F000C;;;BA)(A;;0x00020008;;;$OwnerSid)"
        Write-Host "Creating queue: $printerName"
        Add-Printer -Name $printerName -DriverName $driverName -PortName $portName -PermissionSDDL $security
    } elseif ($printer.Name -eq $legacyName) {
        $step = 'rename existing printer queue'
        Write-Host "Renaming queue: $legacyName -> $printerName"
        Rename-Printer -Name $legacyName -NewName $printerName
    } else {
        Write-Host "Reusing existing queue: $printerName"
    }
    $step = 'verify printer queue'
    $verified = Get-Printer -Name $printerName
    if ($verified.PortName -ne $portName -or $verified.DriverName -notin $drivers) {
        throw 'The registered queue does not have the expected driver and port.'
    }
    Write-Host "Registered queue: $($verified.Name) | $($verified.DriverName) | $($verified.PortName)"
    exit 0
} catch {
    $failure = $_
    Write-Host "ERROR in elevated step: $step"
    Write-Host ($failure | Format-List * -Force | Out-String)
    if ($createdPort) {
        try {
            if (!(Get-Printer | Where-Object PortName -eq $portName)) {
                Remove-PrinterPort -Name $portName
                Write-Host 'Removed the unused port created by this attempt.'
            }
        } catch {
            Write-Host "Port cleanup failed: $($_.Exception.Message)"
        }
    }
    exit 1
} finally {
    if ($recording) { Stop-Transcript | Out-Null }
}
