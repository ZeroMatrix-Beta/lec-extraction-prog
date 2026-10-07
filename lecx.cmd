@echo off
REM Short name for the headless CLI, so the assembly (and every path that already
REM points at it - launch.json, the multi-instance recipe in the docs) keeps its name.
if not defined DOTNET_ROOT if exist "%USERPROFILE%\.dotnet\dotnet.exe" set "DOTNET_ROOT=%USERPROFILE%\.dotnet"
"%~dp0bin\Debug\net10.0\lec-extraction-prog.exe" %*
