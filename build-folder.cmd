@echo off
rem Build the multi-file (folder) variant: loose web assets, no Costura/Fody.
rem Output: src\OsuMapTools\bin\Release\net48\ (exe + DLLs + web\)
setlocal
set "DOTNET=dotnet"
if exist "C:\vibe-build\dotnetsdk\sdk8\dotnet.exe" set "DOTNET=C:\vibe-build\dotnetsdk\sdk8\dotnet.exe"
if exist "C:\vibe-build\caches\dotnet-home" set "DOTNET_CLI_HOME=C:\vibe-build\caches\dotnet-home"
if exist "C:\vibe-build\caches\nuget" set "NUGET_PACKAGES=C:\vibe-build\caches\nuget"
if exist "C:\vibe-build\caches\nuget-http" set "NUGET_HTTP_CACHE_PATH=C:\vibe-build\caches\nuget-http"
if exist "C:\vibe-build\caches\tmp" set "TMP=C:\vibe-build\caches\tmp"
if exist "%TMP%" set "TEMP=%TMP%"

cd /d "%~dp0src\OsuMapTools"
rem Switching from a single-file build leaves Costura-only outputs; build folder from clean.
"%DOTNET%" clean -c Release >nul
"%DOTNET%" build -c Release -p:FolderBuild=true -p:DisableFody=true || goto :error

echo.
echo Folder output: %~dp0src\OsuMapTools\bin\Release\net48\
exit /b 0

:error
echo BUILD FAILED
exit /b 1
