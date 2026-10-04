@echo off
rem Builds CopyPaste.exe using the C# compiler that comes with Windows.
rem No Visual Studio or .NET SDK required.
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo Could not find csc.exe. .NET Framework 4.x is required ^(it ships with Windows 10/11^).
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /out:"%~dp0CopyPaste.exe" ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll ^
  "%~dp0src\CopyPaste.cs"
if errorlevel 1 exit /b 1
echo.
echo Built CopyPaste.exe - double-click it to start.
