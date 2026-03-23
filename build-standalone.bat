@echo off
setlocal
cd /d "%~dp0"

echo Publishing standalone build...
dotnet publish "TempMonitor\TempMonitor.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -p:DebugSymbols=false ^
  -o "TempMonitor\bin\Release\net10.0-windows\win-x64\publish-standalone"

if errorlevel 1 (
  echo.
  echo Standalone build failed.
  exit /b 1
)

echo.
echo Standalone build output:
echo F:\监控\gxTempMonitor\TempMonitor\bin\Release\net10.0-windows\win-x64\publish-standalone
endlocal
