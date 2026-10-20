@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 打包脚本（dev + release）
rem  用法: package.bat
rem  等价命令: sh manager.sh package
rem
rem  产物（dist/ 已 gitignore）:
rem    dist\dev\ScBrowser_<版本>_x64-unpack-debug.zip
rem    dist\release\unpack\                        （可直接运行）
rem    dist\release\ScBrowser_<版本>_x64-setup.exe
rem    dist\release\ScBrowser_<版本>_x64-unpack.zip  （zip 内无顶层目录）
rem ============================================================

rem 关闭常驻 MSBuild / Roslyn 构建服务器：否则同一会话里 build/test 启动的常驻服务器
rem 会缓存「非单文件」的构建求值，导致 package 复用该缓存、输出不符合预期。
set "DOTNET_CLI_USE_MSBUILD_SERVER=0"
set "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1"

rem 注意：不要用 OUTDIR 作变量名——它是 MSBuild 保留属性 OutDir。
set "ROOT=%~dp0"
set "PROJ=%ROOT%src\ScBrowser\ScBrowser.csproj"
set "PROJDIR=%ROOT%src\ScBrowser"
set "TESTDIR=%ROOT%tests\ScBrowser.Tests"
set "INSTALLER_PROJ=%ROOT%installer\ScBrowser.Installer.csproj"
set "INSTALLER_SCRIPT=%ROOT%installer\ScBrowser.iss"
set "STAGING_DIR=%ROOT%.runtime\staging"
set "DIST_DIR=%ROOT%dist"
set "DEV_DIR=%DIST_DIR%\dev"
set "RELEASE_DIR=%DIST_DIR%\release"

if exist "%PROJ%" goto :Ready
echo [ERROR] 找不到工程文件：%PROJ%
exit /b 1

:Ready
where dotnet >nul 2>&1
if errorlevel 1 goto :NoDotnet

rem 版本号单一来源：ScBrowser.csproj 的 <Version>
set "APP_VERSION="
for /f "usebackq delims=" %%V in (`dotnet msbuild "%PROJ%" -getProperty:Version -nologo`) do set "APP_VERSION=%%V"
if not defined APP_VERSION goto :NoVersion

set "DEV_UNPACK=%STAGING_DIR%\dev\ScBrowser_%APP_VERSION%_x64-unpack-debug"
set "REL_UNPACK=%RELEASE_DIR%\unpack"
set "DEV_LOGS=%DEV_UNPACK%\logs"
set "DEV_ZIP=%DEV_DIR%\ScBrowser_%APP_VERSION%_x64-unpack-debug.zip"
set "REL_ZIP=%RELEASE_DIR%\ScBrowser_%APP_VERSION%_x64-unpack.zip"
set "SETUP_EXE=%RELEASE_DIR%\ScBrowser_%APP_VERSION%_x64-setup.exe"

rem Inno Setup 通过 NuGet（Tools.InnoSetup）还原，不要求系统预装。
echo Restoring Inno Setup toolchain...
dotnet restore "%INSTALLER_PROJ%" --nologo >nul 2>&1
if errorlevel 1 goto :NoInnoSetup

set "ISCC="
for /f "usebackq delims=" %%I in (`dotnet msbuild "%INSTALLER_PROJ%" -getProperty:InnoSetupCompiler -nologo`) do set "ISCC=%%I"
if not defined ISCC goto :NoInnoSetup
if not exist "%ISCC%" goto :NoInnoSetup
if not exist "%SystemRoot%\System32\tar.exe" goto :NoTar

echo ============================================================
echo  Package ^|  Version = %APP_VERSION%
echo  Project : %PROJ%
echo  Dev zip : %DEV_ZIP%
echo  Unpack  : %REL_UNPACK%
echo  Zip     : %REL_ZIP%
echo  Setup   : %SETUP_EXE%
echo ============================================================

rem 结束可能驻留的实例，否则它占用的 exe 会让打包覆盖失败。
echo Stopping any running ScBrowser instances...
taskkill /IM ScBrowser.exe /F >nul 2>&1

rem 清理 bin/obj 与旧产物，强制一次干净的打包。
echo Cleaning bin/obj and previous artifacts...
if exist "%PROJDIR%\bin"  rmdir /S /Q "%PROJDIR%\bin"
if exist "%PROJDIR%\obj"  rmdir /S /Q "%PROJDIR%\obj"
if exist "%TESTDIR%\bin"  rmdir /S /Q "%TESTDIR%\bin"
if exist "%TESTDIR%\obj"  rmdir /S /Q "%TESTDIR%\obj"
if exist "%ROOT%installer\obj" rmdir /S /Q "%ROOT%installer\obj"
if exist "%STAGING_DIR%"  rmdir /S /Q "%STAGING_DIR%"
if exist "%DIST_DIR%"      rmdir /S /Q "%DIST_DIR%"
mkdir "%DEV_LOGS%" 2>nul
mkdir "%REL_UNPACK%" 2>nul
mkdir "%DEV_DIR%" 2>nul
mkdir "%RELEASE_DIR%" 2>nul

cd /d "%ROOT%"

rem ------------------------------------------------------------------
rem  1) dev：Debug 发布（框架依赖，含 PDB）+ 构建日志
rem ------------------------------------------------------------------
echo.
echo [1/4] Publishing dev build (Debug + PDB)...
dotnet publish "%PROJ%" -c Debug -r win-x64 --self-contained false -p:PublishSingleFile=false -p:PublishReadyToRun=false -o ".runtime\staging\dev\ScBrowser_%APP_VERSION%_x64-unpack-debug" --nologo > "%DEV_LOGS%\dev-publish.log" 2>&1
if errorlevel 1 goto :DevFailed

rem ------------------------------------------------------------------
rem  2) release：自包含发布，直接落到 dist\release 作为可运行目录
rem     （目标机无需 .NET 运行时，不含 PDB）
rem ------------------------------------------------------------------
echo.
echo [2/4] Publishing release unpack directory...
dotnet publish "%PROJ%" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -p:DebugType=None -p:DebugSymbols=false -o "%REL_UNPACK%" --nologo > "%DEV_LOGS%\release-publish.log" 2>&1
if errorlevel 1 goto :ReleaseFailed

rem ------------------------------------------------------------------
rem  3) 包内说明 / 许可证 / 安装程序
rem ------------------------------------------------------------------
echo.
echo [3/4] Preparing package files and compiling installer...
copy /Y "%ROOT%LICENSE" "%DEV_UNPACK%\LICENSE" >nul
copy /Y "%ROOT%LICENSE" "%REL_UNPACK%\LICENSE" >nul

> "%DEV_UNPACK%\README.txt" echo ScBrowser %APP_VERSION% ^(Debug, x64^)
>> "%DEV_UNPACK%\README.txt" echo =================================
>> "%DEV_UNPACK%\README.txt" echo.
>> "%DEV_UNPACK%\README.txt" echo 这是 Debug 构建（框架依赖），运行 ScBrowser.exe 需要：
>> "%DEV_UNPACK%\README.txt" echo   - Windows 10 1809+ / Windows 11
>> "%DEV_UNPACK%\README.txt" echo   - .NET 10 桌面运行时
>> "%DEV_UNPACK%\README.txt" echo   - WebView2 运行时（通常已随 Edge 预装）
>> "%DEV_UNPACK%\README.txt" echo.
>> "%DEV_UNPACK%\README.txt" echo 调试信息：ScBrowser.pdb、build-info.txt、logs\
>> "%DEV_UNPACK%\README.txt" echo 用户数据位于 %%LOCALAPPDATA%%\sc-browser。

> "%REL_UNPACK%\README.txt" echo ScBrowser %APP_VERSION% ^(x64, self-contained^)
>> "%REL_UNPACK%\README.txt" echo ==========================================
>> "%REL_UNPACK%\README.txt" echo.
>> "%REL_UNPACK%\README.txt" echo 免安装 Release 版本：双击 ScBrowser.exe 即可运行。
>> "%REL_UNPACK%\README.txt" echo 目标机无需安装 .NET 运行时，但仍需 WebView2 运行时
>> "%REL_UNPACK%\README.txt" echo （Windows 10/11 通常已随 Edge 预装）。
>> "%REL_UNPACK%\README.txt" echo.
>> "%REL_UNPACK%\README.txt" echo 用户数据位于 %%LOCALAPPDATA%%\sc-browser。

call :WriteBuildInfo

"%ISCC%" --define=MyAppVersion=%APP_VERSION% "%INSTALLER_SCRIPT%" > "%DEV_LOGS%\setup-compile.log" 2>&1
if errorlevel 1 goto :SetupFailed

rem ------------------------------------------------------------------
rem  4) 生成 zip：
rem     dev  zip 内带 ScBrowser_<版本>_x64-unpack-debug 根目录；
rem     release zip 从 unpack 目录内部打包，条目直接在 zip 根下。
rem ------------------------------------------------------------------
echo.
echo [4/4] Creating zip files...
"%SystemRoot%\System32\tar.exe" -a -c -f "%DEV_ZIP%" -C "%STAGING_DIR%\dev" "ScBrowser_%APP_VERSION%_x64-unpack-debug"
if errorlevel 1 goto :ZipFailed
"%SystemRoot%\System32\tar.exe" -a -c -f "%REL_ZIP%" -C "%REL_UNPACK%" *
if errorlevel 1 goto :ZipFailed

rem 暂存目录只在成功时删除；dist\release\unpack 是正式产物，保留。
rmdir /S /Q "%STAGING_DIR%"

echo.
echo ============================================================
echo  Output files:
echo ============================================================
for %%F in ("%DEV_DIR%\*") do echo   dev\%%~nxF   %%~zF bytes
for %%F in ("%RELEASE_DIR%\*") do echo   release\%%~nxF   %%~zF bytes

echo.
echo [SUCCESS] 打包完成
echo   dev     : %DEV_ZIP%
echo   unpack  : %REL_UNPACK%
echo   zip     : %REL_ZIP%
echo   setup   : %SETUP_EXE%
exit /b 0

:WriteBuildInfo
set "GIT_SHA=unknown"
for /f "delims=" %%G in ('git rev-parse --short HEAD 2^>nul') do set "GIT_SHA=%%G"
set "DOTNET_VER=unknown"
for /f "delims=" %%D in ('dotnet --version 2^>nul') do set "DOTNET_VER=%%D"
> "%DEV_UNPACK%\build-info.txt" (
    echo ScBrowser %APP_VERSION% ^(dev^)
    echo Built: %DATE% %TIME%
    echo Git: %GIT_SHA%
    echo Dotnet: %DOTNET_VER%
    echo OS: %OS%
)
exit /b 0

:NoDotnet
echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
exit /b 1

:NoVersion
echo [ERROR] 无法从 ScBrowser.csproj 读取版本号。
exit /b 1

:NoInnoSetup
echo [ERROR] 无法还原 Inno Setup 工具（NuGet 包 Tools.InnoSetup）。
echo         请检查网络 / NuGet 配置后重试：dotnet restore "%INSTALLER_PROJ%"
exit /b 1

:NoTar
echo [ERROR] 未找到 Windows 自带的 tar.exe（%SystemRoot%\System32\tar.exe）。
echo         需要 Windows 10 1803+ 才能生成 zip 包。
exit /b 1

:DevFailed
echo.
echo [FAILED] dev 发布失败。日志：%DEV_LOGS%\dev-publish.log
type "%DEV_LOGS%\dev-publish.log"
exit /b 1

:ReleaseFailed
echo.
echo [FAILED] release 发布失败。日志：%DEV_LOGS%\release-publish.log
type "%DEV_LOGS%\release-publish.log"
exit /b 1

:SetupFailed
echo.
echo [FAILED] 安装程序编译失败。日志：%DEV_LOGS%\setup-compile.log
type "%DEV_LOGS%\setup-compile.log"
exit /b 1

:ZipFailed
echo.
echo [FAILED] 生成 zip 失败。暂存目录保留：%STAGING_DIR%
exit /b 1
