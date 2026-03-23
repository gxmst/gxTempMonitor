@echo off
setlocal
cd /d "%~dp0"

echo Publishing light build...
dotnet publish "TempMonitor\TempMonitor.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:EnableCompressionInSingleFile=false ^
  -p:DebugType=none ^
  -p:DebugSymbols=false ^
  -o "TempMonitor\bin\Release\net10.0-windows\win-x64\publish"

if errorlevel 1 (
  echo.
  echo Light build failed.
  exit /b 1
)

echo.
echo Light build output:
echo F:\监控\gxTempMonitor\TempMonitor\bin\Release\net10.0-windows\win-x64\publish
endlocal
