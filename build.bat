@echo off
setlocal
set assemblyName=usb-autorun.exe
set compile=%SystemRoot%\Microsoft.net\Framework\v4.0.30319\csc.exe /target:exe /out:%assemblyName% *.cs

set version=%1
if not '%version%'=='' goto set_version
echo usage: build version
echo version = major.minor.revision
echo.
echo example:
echo build 0.0.1
goto :fail
:set_version
echo [assembly: System.Reflection.AssemblyVersion("%version%")] > version.cs

%compile%
if %errorlevel% == 0 goto success
:fail
exit /b 1

:success
echo %assemblyName%@%version% was built w/o errors :)
ecit /b 0

