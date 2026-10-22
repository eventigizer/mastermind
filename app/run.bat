@echo off
REM Quick dev launch (builds + runs from source). For distribution use publish.bat.
cd /d "%~dp0"
start "" dotnet run -c Release --project Mastermind.csproj
