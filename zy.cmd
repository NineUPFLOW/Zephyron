@echo off
rem Short wrapper for the troubleshooting tool: "zy status", "zy where twitch.tv".
rem
rem Runs zy from build\, where build.cmd deploys it. zy needs no administrator
rem rights and finds the installation root on its own.
rem
rem ASCII only on purpose: cmd.exe reads batch files in the OEM code page,
rem and UTF-8 Cyrillic here breaks apart into bogus commands.
setlocal
set "ZY_EXE=%~dp0build\zy.exe"

if not exist "%ZY_EXE%" (
    echo Not deployed yet: %ZY_EXE%
    echo Run: "%~dp0build.cmd"
    exit /b 1
)

"%ZY_EXE%" %*
