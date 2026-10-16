@echo off
cd /d "%~dp0"
rem Builds KinetikFeeds.exe with the .NET Framework compiler that ships with Windows. No SDK needed.
rem "build.bat package" also makes KinetikFeeds.zip (the exe, README and licence) for a release.
if not exist icon.ico powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 || exit /b 1
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /utf8output /codepage:65001 /platform:x64 /target:winexe /out:KinetikFeeds.new.exe /win32icon:icon.ico /resource:icon.ico,icon.ico ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Xml.dll ^
  KinetikFeeds.cs || exit /b 1
rem Built under a new name first, so this works while the app is running. The exe it replaces goes to ..\Old Versions
rem (when that folder is there), stamped with the date and time, to go back to if a build misbehaves.
if exist KinetikFeeds.exe if exist "..\Old Versions\" (
  for /f %%t in ('powershell -NoProfile -Command "Get-Date -Format yyyy-MM-dd-HHmmss"') do move /y KinetikFeeds.exe "..\Old Versions\KinetikFeeds-%%t.exe" >nul
)
if exist KinetikFeeds.exe move /y KinetikFeeds.exe KinetikFeeds.old.exe >nul
move /y KinetikFeeds.new.exe KinetikFeeds.exe >nul || exit /b 1
if exist KinetikFeeds.old.exe del KinetikFeeds.old.exe 2>nul
echo Built KinetikFeeds.exe
if /i not "%~1"=="package" exit /b 0
if exist dist rmdir /s /q dist
mkdir dist\KinetikFeeds
copy /y KinetikFeeds.exe dist\KinetikFeeds\ >nul
copy /y README.md dist\KinetikFeeds\ >nul
copy /y LICENSE dist\KinetikFeeds\ >nul
if exist KinetikFeeds.zip del KinetikFeeds.zip
powershell -NoProfile -Command "Compress-Archive -Path dist\KinetikFeeds\* -DestinationPath KinetikFeeds.zip"
rmdir /s /q dist
echo Made KinetikFeeds.zip
