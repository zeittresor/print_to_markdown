@echo off
setlocal DisableDelayedExpansion
set "PTM_LOG=%~dp0setup.log"
if /i "%~1"=="-Remove" set "PTM_LOG=%~dp0uninstall.log"
>>"%PTM_LOG%" echo ==== Print to Markdown 0.2.4 ^| %DATE% %TIME% ==== || (
    echo [ERROR] Cannot write "%PTM_LOG%".
    pause
    exit /b 1
)
set "PTM_PS=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
if exist "%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe" set "PTM_PS=%SystemRoot%\Sysnative\WindowsPowerShell\v1.0\powershell.exe"
echo Print to Markdown 0.2.4
echo Log: "%PTM_LOG%"
echo Starting Windows PowerShell. Follow the setup prompts.
"%PTM_PS%" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%~dp0Setup.ps1" %* >>"%PTM_LOG%" 2>&1
set "PTM_EXIT=%ERRORLEVEL%"
>>"%PTM_LOG%" echo Exit code: %PTM_EXIT%
if not "%PTM_EXIT%"=="0" (
    echo [ERROR] Setup exited with code %PTM_EXIT%. See "%PTM_LOG%".
    pause
) else (
    echo [OK] Finished. Details: "%PTM_LOG%".
)
exit /b %PTM_EXIT%
