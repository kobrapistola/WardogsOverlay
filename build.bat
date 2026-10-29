@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET 10 SDK isn't installed.
  echo Install it with:  winget install Microsoft.DotNet.SDK.10
  echo Then run this file again.
  pause
  exit /b 1
)

echo Building WarDogsOverlay...
dotnet publish -c Release -o publish
if errorlevel 1 (
  echo.
  echo Build failed - copy the errors above and send them over.
  pause
  exit /b 1
)

echo.
echo Done: %~dp0publish\WarDogsOverlay.exe
pause
