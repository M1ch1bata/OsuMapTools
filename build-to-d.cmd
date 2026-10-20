@echo off
rem Build OsuTrainerXSlim and deploy the single-file exe to D:\APPs\OsuTrainerXSlim
rem (the C:\Users\...\Documents\vibe\osu workspace carries sandbox ACLs,
rem  so the app must run from a normal folder such as D:\APPs\OsuTrainerXSlim)

setlocal
set DOTNET=C:\Users\96549\Documents\vibe\osu\_analysis\dotnetsdk\sdk8\dotnet.exe
set DOTNET_CLI_HOME=C:\Users\96549\Documents\vibe\osu\OsuTrainerX\.dotnet-home
set NUGET_PACKAGES=C:\Users\96549\Documents\vibe\osu\OsuTrainerX\.nuget
set NUGET_HTTP_CACHE_PATH=C:\Users\96549\Documents\vibe\osu\OsuTrainerX\.nuget-http
set TMP=C:\Users\96549\Documents\vibe\osu\OsuTrainerX\.tmp
set TEMP=%TMP%

cd /d "%~dp0src\Slim.App"
"%DOTNET%" build -c Release || goto :error

if not exist "D:\APPs\OsuTrainerXSlim" mkdir "D:\APPs\OsuTrainerXSlim"
copy /y "bin\Release\net48\OsuTrainerXSlim.exe" "D:\APPs\OsuTrainerXSlim\OsuTrainerXSlim.exe" >nul || goto :error

echo.
echo Deployed single-file exe: D:\APPs\OsuTrainerXSlim\OsuTrainerXSlim.exe
exit /b 0

:error
echo BUILD OR DEPLOY FAILED
exit /b 1
