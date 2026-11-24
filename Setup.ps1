param([switch]$Remove)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$stage = 'initialization'

function Write-Step([string]$Message) {
    Write-Host ('[{0:yyyy-MM-dd HH:mm:ss}] {1}' -f (Get-Date), $Message)
}

function Invoke-PrinterSetup {
    $printerScript = Join-Path $PSScriptRoot 'Printer.ps1'
    if (!(Test-Path -LiteralPath $printerScript -PathType Leaf)) {
        throw 'Printer.ps1 is missing. Extract the complete ZIP first.'
    }

    $name = 'printer-{0:yyyyMMdd-HHmmss}-{1}.log' -f (Get-Date), $PID
    $printerLog = Join-Path $PSScriptRoot $name
    try {
        $arguments = @(
            '-NoLogo'
            '-NoProfile'
            '-NonInteractive'
            '-ExecutionPolicy'
            'Bypass'
            '-File'
            ('"' + $printerScript + '"')
            '-Action'
            $action
            '-OwnerSid'
            $owner
            '-LogPath'
            ('"' + $printerLog + '"')
        )
        if ($isAdmin) {
            $result = Start-Process -FilePath $powershell -Wait -PassThru -ArgumentList $arguments
        } else {
            $result = Start-Process -FilePath $powershell -Verb RunAs -Wait -PassThru -ArgumentList $arguments
        }
    } finally {
        if (Test-Path -LiteralPath $printerLog -PathType Leaf) {
            Write-Step "Elevated printer log: $name"
            Get-Content -LiteralPath $printerLog | ForEach-Object { Write-Host $_ }
        } else {
            Write-Step "Elevated printer log was not created: $name"
        }
    }
    if ($result.ExitCode -ne 0) {
        throw "Printer setup failed with exit code $($result.ExitCode). See the elevated printer details above."
    }
}

function Build-Receiver {
    $framework = [Runtime.InteropServices.RuntimeEnvironment]::GetRuntimeDirectory()
    $compiler = Join-Path $framework 'csc.exe'
    if (!(Test-Path -LiteralPath $compiler -PathType Leaf)) {
        throw "The Windows .NET Framework C# compiler is missing: $compiler"
    }

    Add-Type -AssemblyName System.IO.Compression
    $compression = [IO.Compression.ZipArchive].Assembly.Location
    $temporaryExe = Join-Path $PSScriptRoot "PrintToMarkdown-build-$PID.exe"
    try {
        $options = @(
            '/nologo'
            '/target:winexe'
            '/platform:anycpu'
            '/optimize+'
            '/codepage:65001'
            "/out:$temporaryExe"
            "/reference:$compression"
            '/reference:System.Drawing.dll'
            '/reference:System.Windows.Forms.dll'
            '/reference:System.Xml.Linq.dll'
            (Join-Path $PSScriptRoot 'Program.cs')
            (Join-Path $PSScriptRoot 'XpsMarkdown.cs')
            (Join-Path $PSScriptRoot 'SelfTest.cs')
        )
        Write-Step "Compiler: $compiler"
        & $compiler @options
        if ($LASTEXITCODE -ne 0) {
            throw "C# compilation failed with exit code $LASTEXITCODE."
        }

        $testLog = Join-Path $PSScriptRoot "self-test-$PID.log"
        try {
            $check = Start-Process -FilePath $temporaryExe -ArgumentList ('--self-test "' + $testLog + '"') -Wait -PassThru
            if ($check.ExitCode -ne 0) {
                throw "XPS conversion self-test failed with exit code $($check.ExitCode)."
            }
        } finally {
            if (Test-Path -LiteralPath $testLog -PathType Leaf) {
                Get-Content -LiteralPath $testLog -Encoding UTF8 | ForEach-Object { Write-Host $_ }
                Remove-Item -LiteralPath $testLog
            }
        }
        Move-Item -LiteralPath $temporaryExe -Destination $exe
    } finally {
        if (Test-Path -LiteralPath $temporaryExe) {
            Remove-Item -LiteralPath $temporaryExe
        }
    }
}

function Remove-LegacyShortcuts($Shell, [string[]]$Directories) {
    foreach ($directory in $Directories) {
        $path = Join-Path $directory 'Print to Markdown (XPS).lnk'
        if (Test-Path -LiteralPath $path -PathType Leaf) {
            $target = $Shell.CreateShortcut($path).TargetPath
            if ([IO.Path]::GetFileName($target) -match '^PrintToMarkdown(?:-0\.2\.[12])?\.exe$') {
                Remove-Item -LiteralPath $path
            }
        }
    }
}

function Get-ReceiverConnection {
    Get-NetTCPConnection -LocalPort 19101 -State Listen -ErrorAction SilentlyContinue |
        Where-Object { $_.LocalAddress -in @('127.0.0.1', '0.0.0.0') } | Select-Object -First 1
}

function Start-Receiver {
    $connection = Get-ReceiverConnection
    if ($connection) {
        $running = Get-Process -Id $connection.OwningProcess -ErrorAction SilentlyContinue
        if ($running -and $running.Path -eq $exe) {
            Write-Step 'The print receiver is already running.'
            return
        }
        if ($running -and $running.Path -in $previousTargets -and $running.ProcessName -match '^PrintToMarkdown(?:-0\.2\.[123])?$') {
            Write-Step 'Closing the receiver from the previous installation.'
            if (!$running.CloseMainWindow() -or !$running.WaitForExit(5000)) {
                throw 'The previous receiver is still open. Finish any pending save dialog, close the receiver and rerun Setup.bat.'
            }
        } elseif ($running) {
            throw "TCP port 19101 is already in use (PID $($running.Id)). Close the previous receiver or free the port, then rerun Setup.bat."
        }
    }

    $receiver = Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -PassThru
    $deadline = [DateTime]::UtcNow.AddSeconds(8)
    do {
        if ($receiver.HasExited) { throw "The print receiver exited with code $($receiver.ExitCode)." }
        $connection = Get-ReceiverConnection
        if ($connection -and $connection.OwningProcess -eq $receiver.Id) {
            Write-Step "Verified the background print receiver (PID $($receiver.Id))."
            return
        }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The print receiver did not start listening on TCP port 19101.'
}

try {
    $powershell = Join-Path $PSHOME 'powershell.exe'
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $owner = $identity.User.Value
    $principal = New-Object Security.Principal.WindowsPrincipal -ArgumentList $identity
    $isAdmin = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    $action = if ($Remove) { 'Remove' } else { 'Install' }
    $exe = Join-Path $PSScriptRoot 'PrintToMarkdown-0.2.4.exe'
    $startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Print to Markdown (.md).lnk'
    $programs = Join-Path ([Environment]::GetFolderPath('Programs')) 'Print to Markdown (.md).lnk'
    Write-Step "Print to Markdown 0.2.4 | $action | Windows $([Environment]::OSVersion.Version) | PowerShell $($PSVersionTable.PSVersion)"

    if ($Remove) {
        $stage = 'remove printer'
        Invoke-PrinterSetup
        $stage = 'remove shortcuts'
        foreach ($link in @($startup, $programs)) {
            if (Test-Path -LiteralPath $link) { Remove-Item -LiteralPath $link }
        }
        $shell = New-Object -ComObject WScript.Shell
        Remove-LegacyShortcuts -Shell $shell -Directories @((Split-Path -Parent $startup), (Split-Path -Parent $programs))
        Write-Step 'Removed the printer and shortcuts. Close the receiver if it is running.'
        exit 0
    }

    $stage = 'show administrator guidance'
    Add-Type -AssemblyName System.Windows.Forms
    $adminHelp = 'To start it manually, right-click Setup.bat and choose "Run as administrator" ("Als Administrator ausführen"). Use the Windows account that will print.'
    if (!$isAdmin) {
        Write-Step 'Setup was started without administrator rights. Windows will prompt before printer registration.'
        $message = "The printer needs administrator rights. Windows will ask for approval before installing it.`r`n`r`n$adminHelp"
        [void][Windows.Forms.MessageBox]::Show($message, 'Print to Markdown Setup', 'OK', 'Information')
    }

    $stage = 'build and self-test'
    if (!(Test-Path -LiteralPath $exe -PathType Leaf)) {
        Write-Step 'Building and testing the receiver with the Windows .NET Framework compiler.'
        Build-Receiver
    } else {
        Write-Step 'Using the existing 0.2.4 executable.'
    }

    $stage = 'register printer'
    Write-Step 'Registering the local printer. Windows may request administrator approval.'
    try {
        Invoke-PrinterSetup
    } catch {
        if (!$isAdmin) {
            $errorDetail = $_.Exception.Message
            [void][Windows.Forms.MessageBox]::Show("Printer setup did not finish.`r`n`r`n$errorDetail`r`n`r`n$adminHelp", 'Print to Markdown Setup', 'OK', 'Warning')
        }
        throw
    }

    $stage = 'verify printer from installing account'
    Import-Module PrintManagement
    $queue = Get-Printer -Name 'Print to Markdown (.md)'
    Add-Type -AssemblyName System.Drawing
    $visiblePrinters = [System.Drawing.Printing.PrinterSettings]::InstalledPrinters
    if ($queue.PortName -ne 'PrintToMarkdownXPS_127.0.0.1_19101' -or $queue.Name -notin $visiblePrinters) {
        throw 'The expected printer queue is not visible to this Windows account.'
    }
    Write-Step "Verified printer: $($queue.Name) | $($queue.DriverName) | $($queue.PortName)"

    $stage = 'start receiver'
    $shell = New-Object -ComObject WScript.Shell
    $legacyStartup = Join-Path (Split-Path -Parent $startup) 'Print to Markdown (XPS).lnk'
    $legacyPrograms = Join-Path (Split-Path -Parent $programs) 'Print to Markdown (XPS).lnk'
    $previousTargets = @(foreach ($link in @($startup, $programs, $legacyStartup, $legacyPrograms)) {
        if (Test-Path -LiteralPath $link -PathType Leaf) { $shell.CreateShortcut($link).TargetPath }
    })
    Start-Receiver

    $stage = 'create shortcuts'
    foreach ($link in @($startup, $programs)) {
        $shortcut = $shell.CreateShortcut($link)
        $shortcut.TargetPath = $exe
        $shortcut.WorkingDirectory = $PSScriptRoot
        $shortcut.Arguments = ''
        $shortcut.Save()
    }
    Remove-LegacyShortcuts -Shell $shell -Directories @((Split-Path -Parent $startup), (Split-Path -Parent $programs))

    Write-Step 'Printer installed. Reopen any print dialog that was open during setup.'
    [void][Windows.Forms.MessageBox]::Show(
        "Installation complete.`r`n`r`nSelect Print to Markdown (.md) in the print dialog.",
        'Print to Markdown Setup', 'OK', 'Information')
    exit 0
} catch {
    Write-Host "ERROR in step: $stage"
    Write-Host ($_ | Format-List * -Force | Out-String)
    exit 1
}
