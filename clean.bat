@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

rem ============================================================
rem  sc-browser - 清理脚本
rem  移除 src/ 与 tests/ 下的 bin/obj，以及发布产物 artifacts/
rem  用法: clean.bat
rem  等价命令: sh manager.sh clean
rem ============================================================

pushd "%~dp0"

echo ============================================================
echo  Clean  ^|  移除 bin / obj / artifacts
echo ============================================================

rem 先结束在跑的实例：它占用 ScBrowser.exe，会让删除失败（只是静默失败，不易发现）。
taskkill /IM ScBrowser.exe /F >nul 2>&1

call :CleanProject "src\ScBrowser"
call :CleanProject "tests\ScBrowser.Tests"

if exist "artifacts" ( echo  - artifacts & rmdir /S /Q "artifacts" )

echo.
echo [SUCCESS] 清理完成。
popd
exit /b 0

:CleanProject
set "P=%~1"
if exist "%P%\bin" ( echo  - %P%\bin & rmdir /S /Q "%P%\bin" )
if exist "%P%\obj" ( echo  - %P%\obj & rmdir /S /Q "%P%\obj" )
exit /b 0
