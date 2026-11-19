@echo off
rem Teacher AI: starts the server (if it isn't running yet) and opens Teacher in Microsoft Edge as an app window.
rem Edge has the natural Uzbek voice (Sardor); Chrome would read Uzbek with a Russian voice.
title Teacher AI
set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
set "PATH=%DOTNET_ROOT%;%PATH%"
set "ASPNETCORE_ENVIRONMENT=Development"
set "URL=http://localhost:5080"
set "EDGE=%ProgramFiles(x86)%\Microsoft\Edge\Application\msedge.exe"
if not exist "%EDGE%" set "EDGE=%ProgramFiles%\Microsoft\Edge\Application\msedge.exe"

call :ping
if %errorlevel%==0 goto open

echo Teacher AI server ishga tushmoqda...
start "Teacher AI server" /min /d "%~dp0src\UstozAI.Api" cmd /c "dotnet run --urls %URL%"
for /l %%i in (1,1,90) do (
  call :ping
  if not errorlevel 1 goto open
  timeout /t 1 /nobreak >nul
)
echo Server 90 soniyada ishga tushmadi. "Teacher AI server" oynasidagi xatoni ko'ring.
pause
exit /b 1

:open
start "" "%EDGE%" --app=%URL% --window-size=1440,900
exit /b 0

:ping
powershell -NoProfile -Command "try { Invoke-WebRequest '%URL%/api/health' -UseBasicParsing -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
exit /b %errorlevel%
