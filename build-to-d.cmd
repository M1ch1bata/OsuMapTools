@echo off
rem Build OsuMapTools and deploy the single-file exe to D:\APPs\OsuMapTools
rem (the C:\Users\...\Documents\vibe\osu workspace carries sandbox ACLs,
rem  so the app must run from a normal folder such as D:\APPs\OsuMapTools)

setlocal
set DOTNET=D:\vibe\osu\_analysis\dotnetsdk\sdk8\dotnet.exe
set DOTNET_CLI_HOME=D:\vibe\osu\OsuTrainerX\.dotnet-home
set NUGET_PACKAGES=D:\vibe\osu\OsuTrainerX\.nuget
set NUGET_HTTP_CACHE_PATH=D:\vibe\osu\OsuTrainerX\.nuget-http
set TMP=D:\vibe\osu\OsuTrainerX\.tmp
set TEMP=%TMP%

cd /d "%~dp0src\OsuMapTools"
"%DOTNET%" build -c Release || goto :error

if not exist "D:\APPs\OsuMapTools" mkdir "D:\APPs\OsuMapTools"
copy /y "bin\Release\net48\OsuMapTools.exe" "D:\APPs\OsuMapTools\OsuMapTools.exe" >nul || goto :error

echo.
echo Deployed single-file exe: D:\APPs\OsuMapTools\OsuMapTools.exe
exit /b 0

:error
echo BUILD OR DEPLOY FAILED
exit /b 1
