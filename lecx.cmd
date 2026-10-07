@echo off
REM Short name for the headless CLI, so the assembly (and every path that already
REM points at it - launch.json, the multi-instance recipe in the docs) keeps its name.
REM Forwards every argument to the built executable; no arguments starts the menu.
REM A per-user .NET install (~/.dotnet) is used when DOTNET_ROOT is not set already.
if not defined DOTNET_ROOT if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
"%~dp0bin\Debug\net10.0\lec-extraction-prog.exe" %*
