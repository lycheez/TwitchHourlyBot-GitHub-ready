@echo off
setlocal
where dotnet >nul 2>nul
if errorlevel 1 (
  echo .NET 8 SDK not found.
  echo Install .NET 8 SDK first.
  pause
  exit /b 1
)
dotnet run --project "%~dp0src\TwitchHourlyBot\TwitchHourlyBot.csproj"
pause
