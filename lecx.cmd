@echo off
REM Short name for the headless CLI, so the assembly (and every path that already
REM points at it - launch.json, the multi-instance recipe in the docs) keeps its name.
REM Forwards every argument to the built executable; no arguments starts the menu.
"%~dp0bin\Debug\net10.0\lec-extraction-prog.exe" %*
