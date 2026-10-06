@echo off
setlocal
pushd "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
set REF=/r:System.dll /r:System.Core.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll

"%CSC%" /nologo /optimize+ /platform:anycpu /target:winexe /out:frpc-tray.exe %REF% ^
    /win32icon:assets\app.ico ^
    /resource:assets\frpc.exe,frpc.exe ^
    /resource:assets\app.ico,app.ico ^
    /resource:src\default.frpc.toml,default.frpc.toml ^
    src\FrpcTray.cs
if errorlevel 1 goto fail

"%CSC%" /nologo /optimize+ /platform:anycpu /target:exe /out:fakeproc.exe %REF% test\FakeProc.cs
if errorlevel 1 goto fail

echo build ok
popd
exit /b 0

:fail
echo build failed
popd
exit /b 1
