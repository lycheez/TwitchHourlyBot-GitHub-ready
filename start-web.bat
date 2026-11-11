@echo off
setlocal
cd /d "%~dp0"

where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] dotnet not found. Install the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
dotnet --list-sdks | findstr /b "8." >nul
if errorlevel 1 (
  echo [ERROR] .NET 8 SDK not installed. Installed SDKs:
  dotnet --list-sdks
  echo Install: https://dotnet.microsoft.com/download/dotnet/8.0
  pause
  exit /b 1
)
where npm >nul 2>nul
if errorlevel 1 (
  echo [ERROR] npm not found. Install Node.js LTS: https://nodejs.org
  pause
  exit /b 1
)

if not exist web\node_modules (
  echo Installing web dependencies...
  pushd web
  call npm install
  popd
)

start "TwitchBot API" cmd /k dotnet run --project src\TwitchHourlyBot.Api --urls http://localhost:5000
start "TwitchBot Web" cmd /k "cd /d %~dp0web && npm start"
echo Waiting for servers, then opening http://localhost:4200 ...
timeout /t 25 >nul
start http://localhost:4200
