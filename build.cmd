@echo off
rem Build OsuMapTools (single-file exe via Costura).
rem Prefers the machine-wide build environment on C: when present.

setlocal
set "DOTNET=dotnet"
if exist "C:\vibe-build\dotnetsdk\sdk8\dotnet.exe" set "DOTNET=C:\vibe-build\dotnetsdk\sdk8\dotnet.exe"
if exist "C:\vibe-build\caches\dotnet-home" set "DOTNET_CLI_HOME=C:\vibe-build\caches\dotnet-home"
if exist "C:\vibe-build\caches\nuget" set "NUGET_PACKAGES=C:\vibe-build\caches\nuget"
if exist "C:\vibe-build\caches\nuget-http" set "NUGET_HTTP_CACHE_PATH=C:\vibe-build\caches\nuget-http"
if exist "C:\vibe-build\caches\tmp" set "TMP=C:\vibe-build\caches\tmp"
if exist "%TMP%" set "TEMP=%TMP%"

cd /d "%~dp0src\OsuMapTools"
"%DOTNET%" build -c Release || goto :error

echo.
echo Output: %~dp0src\OsuMapTools\bin\Release\net48\OsuMapTools.exe
exit /b 0

:error
echo BUILD FAILED
exit /b 1
