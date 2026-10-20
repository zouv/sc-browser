@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

rem ============================================================
rem  sc-browser - 清理脚本
rem  移除 src/、tests/、installer/ 下的 bin/obj 与打包暂存目录；
rem  保留 dist/ 成品包（与 sc-code 的 clean 语义一致）。
rem  用法: clean.bat
rem  等价命令: sh manager.sh clean
rem ============================================================

pushd "%~dp0"

echo ============================================================
echo  Clean  ^|  移除 bin / obj（保留 dist）
echo ============================================================

rem 先结束在跑的实例：它占用 ScBrowser.exe，会让删除失败（只是静默失败，不易发现）。
taskkill /IM ScBrowser.exe /F >nul 2>&1

call :CleanProject "src\ScBrowser"
call :CleanProject "tests\ScBrowser.Tests"
call :CleanProject "installer"

rem 历史产物目录：旧版本发布到 packages/，更早到 artifacts，迁移后顺手清掉残留。
if exist "packages" ( echo  - packages & rmdir /S /Q "packages" )
if exist "artifacts" ( echo  - artifacts & rmdir /S /Q "artifacts" )

rem 打包暂存目录：成功时 package.bat 会自删，失败时会残留。
if exist ".runtime" ( echo  - .runtime & rmdir /S /Q ".runtime" )

echo.
echo [SUCCESS] 清理完成（dist 成品包已保留）。
popd
exit /b 0

:CleanProject
set "P=%~1"
if exist "%P%\bin" ( echo  - %P%\bin & rmdir /S /Q "%P%\bin" )
if exist "%P%\obj" ( echo  - %P%\obj & rmdir /S /Q "%P%\obj" )
exit /b 0
