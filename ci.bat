@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 一键 CI 流水线
rem  clean -> verify（构建 -warnaserror + 单测 + 文档体检）-> publish
rem  用法: ci.bat
rem ============================================================

rem 关闭常驻 MSBuild / Roslyn 构建服务器，避免其缓存影响 publish 的单文件打包（详见 publish.bat）。
set "DOTNET_CLI_USE_MSBUILD_SERVER=0"
set "DOTNET_CLI_DO_NOT_USE_MSBUILD_SERVER=1"

pushd "%~dp0"

call "%~dp0clean.bat"   || goto :Fail
call "%~dp0verify.bat"  || goto :Fail
call "%~dp0publish.bat" || goto :Fail

echo.
echo ============================================================
echo  [SUCCESS] CI pipeline completed.
echo  Artifact: %CD%\artifacts\publish\ScBrowser.exe
echo ============================================================
popd
exit /b 0

:Fail
echo.
echo [FAILED] CI pipeline aborted.
popd
exit /b 1
