@echo off
rem ClearForge dev loop — run from repo root.
rem usage: scripts\dev.bat [build|test|engine|ui]
setlocal
set CMD=%1
if "%CMD%"=="" set CMD=test

if "%CMD%"=="build" (
  dotnet build native\ClearForge.sln && flutter --version >nul && cd apps\desktop_flutter && flutter analyze && cd ..\..
  exit /b %ERRORLEVEL%
)
if "%CMD%"=="test" (
  dotnet test native\ClearForge.sln && cd apps\desktop_flutter && flutter test && cd ..\..
  exit /b %ERRORLEVEL%
)
if "%CMD%"=="engine" (
  dotnet run --project native\ClearForge.Host
  exit /b %ERRORLEVEL%
)
if "%CMD%"=="ui" (
  cd apps\desktop_flutter && flutter run -d windows
  exit /b %ERRORLEVEL%
)
echo unknown command: %CMD% & exit /b 2
