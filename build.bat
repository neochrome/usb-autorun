@echo off
setlocal
set assemblyName=usb-autorun.exe
set compile=%SystemRoot%\Microsoft.net\Framework\v4.0.30319\csc.exe /target:exe /out:%assemblyName% *.cs

for /f %%a in ('git rev-parse HEAD') do set hash=%%a
echo [assembly: System.Reflection.AssemblyInformationalVersion("%hash%")] > revision.cs

%compile%
if %errorlevel% == 0 goto success
:fail
exit /b 1

:success
echo %assemblyName% was built w/o errors :)
exit /b 0
