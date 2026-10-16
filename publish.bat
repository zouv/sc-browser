@echo off
chcp 65001 >nul

rem ============================================================
rem  sc-browser - 发布自包含单文件（win-x64）
rem  用法: publish.bat [Release^|Debug]    默认 Release
rem  等价命令: sh manager.sh publish
rem  产物: artifacts\publish\ScBrowser.exe（目标机无需安装 .NET 运行时）
rem ============================================================

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem 关闭常驻 MSBuild / Roslyn 构建服务器：否则同一会话里 build/test 启动的常驻服务器
rem 会缓存「非单文件」的构建求值，紧接着的 publish 复用该缓存、静默跳过单文件打包目标
rem GenerateBundle，产物从单个 EXE 退化成上百个散落 DLL（无报错，只是悄悄变了）。
set "DOTNET_CLI_USE_MSBUILD_SERVER=0"
set "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1"

rem 注意：不要用 OUTDIR 作变量名——它是 MSBuild 保留属性 OutDir，
rem 环境变量会覆盖编译输出目录，导致单文件打包回退成散落 DLL。
set "ROOT=%~dp0"
set "PROJ=%ROOT%src\ScBrowser\ScBrowser.csproj"
set "PROJDIR=%ROOT%src\ScBrowser"
set "TESTDIR=%ROOT%tests\ScBrowser.Tests"
set "OUT_DIR=%ROOT%artifacts\publish"

if exist "%ROOT%src\ScBrowser\ScBrowser.csproj" goto :Ready
echo [ERROR] 找不到工程文件：%PROJ%
exit /b 1

:Ready
echo ============================================================
echo  Publish ^|  Config = %CONFIG%
echo  Project : %PROJ%
echo  Output  : %OUT_DIR%
echo ============================================================

where dotnet >nul 2>&1
if errorlevel 1 goto :NoDotnet

rem 结束可能驻留的实例，否则它占用的 exe 会让打包覆盖失败（MSB4018）。
echo Stopping any running ScBrowser instances...
taskkill /IM ScBrowser.exe /F >nul 2>&1

rem 发布前清空 bin/obj：基于 build/test 的增量产物直接 publish 会静默跳过单文件
rem 打包目标（无报错），输出散落上百个 DLL。
echo Cleaning bin/obj to force a clean single-file publish...
if exist "%PROJDIR%\bin"  rmdir /S /Q "%PROJDIR%\bin"
if exist "%PROJDIR%\obj"  rmdir /S /Q "%PROJDIR%\obj"
if exist "%TESTDIR%\bin"  rmdir /S /Q "%TESTDIR%\bin"
if exist "%TESTDIR%\obj"  rmdir /S /Q "%TESTDIR%\obj"
if exist "%OUT_DIR%"       rmdir /S /Q "%OUT_DIR%"

cd /d "%ROOT%"

rem 注意：
rem - 显式关闭 PublishReadyToRun：本机 SDK 上 R2R 与 WPF 单文件发布的组合不稳定，
rem   会间歇性触发 NETSDK1096 crossgen 失败。
rem - IncludeAllContentForSelfExtract 必须开：把内部页面（Assets\ui）也打进 exe，
rem   启动时释放到临时目录。不开的话 exe 旁边会留一个 Assets 目录，
rem   用户只拷走 exe 时内部页面会加载不出来。
rem - -o 用相对路径 artifacts\publish：本机 dotnet CLI 对「cmd 变量展开出来的绝对
rem   路径」解析有缺陷，会静默跳过单文件打包而输出散落 DLL。
dotnet publish "%PROJ%" -c %CONFIG% -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -p:PublishReadyToRun=false -o artifacts\publish --nologo

if errorlevel 1 goto :Failed

echo.
echo ============================================================
echo  Output files:
echo ============================================================
for %%F in ("%OUT_DIR%\*") do echo   %%~nxF   %%~zF bytes

echo.
echo [SUCCESS] 发布完成：%OUT_DIR%\ScBrowser.exe
exit /b 0

:NoDotnet
echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
exit /b 1

:Failed
echo.
echo [FAILED] 发布失败。
exit /b 1
