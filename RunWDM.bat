@echo off
setlocal
cd /d "%~dp0src\WDM"
set EXE=bin\Debug\net8.0-windows\WDM.exe
if not exist "%EXE%" (
    echo Building WDM...
    dotnet build -c Debug -v q
    if errorlevel 1 ( echo Build failed. & pause & exit /b 1 )
)
start "" "%EXE%"
endlocal