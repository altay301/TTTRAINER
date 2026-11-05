@echo off
rem Builds TTTRAINER.exe with the .NET Framework compiler that ships with Windows.
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize /codepage:65001 ^
  /win32manifest:src\app.manifest ^
  /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.ServiceProcess.dll ^
  /resource:src\fonts\3270-Regular.ttf,font3270.ttf ^
  /out:TTTRAINER.exe src\FocusLock.cs src\Ui.cs src\ArtView.cs
