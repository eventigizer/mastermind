@echo off
REM Build a portable, single-file Mastermind.exe (no .NET install needed on target PCs).
setlocal
cd /d "%~dp0"

echo Building Mastermind  (self-contained, single file, win-x64)...
echo.
REM Publish inside this project's bin\, then copy the one exe up to the repository root. Never publish
REM straight into the root: the SDK excludes the publish dir from the source globs, so a publish
REM dir CONTAINING this project compiles no source at all and ships an empty exe, no error.
dotnet publish Mastermind.csproj -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=none ^
  -o "%~dp0bin\portable"

if errorlevel 1 ( echo. & echo BUILD FAILED. & pause & exit /b 1 )

set tries=0
:copy
copy /Y "%~dp0bin\portable\Mastermind.exe" "%~dp0..\Mastermind.exe" >nul
if not errorlevel 1 goto copied
set /a tries+=1
if %tries% geq 10 ( echo. & echo COPY FAILED - is Mastermind.exe still running? & pause & exit /b 1 )
REM A --hook run holds the exe for a moment. ping is the sleep: timeout refuses the redeploy's < nul.
ping -n 2 127.0.0.1 >nul
goto copy
:copied

echo.
echo ============================================================
echo  Done.  Portable build:  %~dp0..\Mastermind.exe
echo  Copy that one file to any Windows 10/11 PC and run it.
echo ============================================================
pause
