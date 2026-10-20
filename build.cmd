@echo off
rem Build OsuMapTools (single-file exe via Costura).
rem Requires the .NET SDK with .NET Framework 4.8 targeting pack.

setlocal
cd /d "%~dp0src\OsuMapTools"
dotnet build -c Release || goto :error

echo.
echo Output: %~dp0src\OsuMapTools\bin\Release\net48\OsuMapTools.exe
exit /b 0

:error
echo BUILD FAILED
exit /b 1
