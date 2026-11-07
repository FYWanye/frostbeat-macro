@echo off
setlocal enabledelayedexpansion
REM ============================================================
REM  FrostBeat 冰与火之宏 - 构建脚本
REM  依赖：Visual Studio 2022 Build Tools（含 Roslyn csc.exe）
REM        .NET Framework 4.8.1 Targeting Pack
REM ============================================================

set "ROOT=%~dp0"
set "SRC=%ROOT%src"
set "OUT=%ROOT%build"

set "REF=C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8.1"
set "CSC=C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe"

if not exist "%CSC%" (
    echo [ERROR] 未找到 csc.exe，请安装 Visual Studio 2022 Build Tools。
    echo         路径：%CSC%
    exit /b 1
)
if not exist "%REF%\mscorlib.dll" (
    echo [ERROR] 未找到 .NET Framework 4.8.1 Targeting Pack。
    echo         路径：%REF%
    exit /b 1
)

if not exist "%OUT%" mkdir "%OUT%"

set "REFS="
for %%d in (
    mscorlib.dll System.dll System.Core.dll System.Xml.dll System.Xml.Linq.dll
    WindowsBase.dll PresentationCore.dll PresentationFramework.dll System.Xaml.dll
    System.Windows.Forms.dll
) do (
    set "REFS=!REFS!"%REF%\%%d","
)

echo [INFO] 编译 FrostBeat.exe ...
"%CSC%" ^
    /target:winexe /platform:anycpu /langversion:latest /optimize+ ^
    /out:"%OUT%\FrostBeat.exe" ^
    /reference:%REFS% ^
    /reference:"%ROOT%Mono.Cecil.dll" ^
    /resource:"%SRC%\MainWindow.xaml",MainWindow.xaml ^
    "%SRC%\FrostBeat.decompiled.cs"

if errorlevel 1 (
    echo [ERROR] 编译失败。
    exit /b 1
)

REM 复制运行时依赖
copy /Y "%ROOT%FrostBeat.Bridge.dll" "%OUT%\" >nul
copy /Y "%ROOT%Mono.Cecil.dll" "%OUT%\" >nul
copy /Y "%ROOT%FrostBeat.exe.config" "%OUT%\" >nul

echo [OK] 构建完成：%OUT%\FrostBeat.exe
exit /b 0
