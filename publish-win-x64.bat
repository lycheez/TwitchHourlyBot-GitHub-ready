@echo off
setlocal
dotnet publish "%~dp0src\TwitchHourlyBot\TwitchHourlyBot.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0publish"
echo.
echo Published to:
echo %~dp0publish
pause
