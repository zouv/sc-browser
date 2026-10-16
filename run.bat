@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 启动脚本（前台运行，关闭浏览器窗口即退出）
rem  用法: run.bat [启动参数...]
rem        例如 run.bat --install-extension C:\path\to\extension
rem  等价命令: sh manager.sh run
rem ============================================================

pushd "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
    popd
    exit /b 1
)

echo [INFO] 启动 sc-browser（首次运行会先编译，请稍候）...
dotnet run --project "src\ScBrowser\ScBrowser.csproj" %*

set "CODE=%ERRORLEVEL%"
popd
exit /b %CODE%
