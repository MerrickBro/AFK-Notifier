@echo off
cd /d "%~dp0"
set DOTNET_NOLOGO=1
set DOTNET_CLI_TELEMETRY_OPTOUT=1
set DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish Release.ps1"
set EXIT_CODE=%ERRORLEVEL%

echo.
if not "%EXIT_CODE%"=="0" (
    echo Release publishing failed with exit code %EXIT_CODE%.
) else (
    echo Release publishing completed successfully.
)
echo.
pause
exit /b %EXIT_CODE%
