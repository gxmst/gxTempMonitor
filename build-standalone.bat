@echo off
setlocal EnableExtensions
cd /d "%~dp0"

set "ARCH=%~1"
if not defined ARCH set "ARCH=x64"

if /I "%ARCH%"=="x64" goto arch_ok
if /I "%ARCH%"=="arm64" goto arch_ok

echo Unsupported architecture: %ARCH%
echo Usage: %~nx0 [x64^|arm64]
exit /b 2

:arch_ok
set "PROFILE=SelfContained-%ARCH%"
set "OUTPUT=%CD%\publish\self-contained\win-%ARCH%"

if exist "%OUTPUT%\" (
  echo Removing previous output: %OUTPUT%
  rmdir /s /q "%OUTPUT%"
  if exist "%OUTPUT%\" (
    echo Failed to remove previous output.
    exit /b 1
  )
)

echo Publishing self-contained win-%ARCH% build...
dotnet publish "TempMonitor\TempMonitor.csproj" ^
  -c Release ^
  -p:PublishProfile="%PROFILE%" ^
  --nologo

if errorlevel 1 (
  echo.
  echo Self-contained build failed.
  exit /b 1
)

echo.
echo Self-contained build output:
echo %OUTPUT%
endlocal
