@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 开发构建并启动（Windows 便捷入口，纯 cmd + dotnet）
rem  用法: dev.bat [启动参数...]
rem        构建（含单元测试）后启动 Debug 版：
rem        src\ScBrowser\bin\Debug\net10.0-windows\ScBrowser.exe
rem        启动参数会原样传给浏览器（例如 --install-extension <目录>）。
rem
rem  构成: build.bat + test.bat + 启动（语义与 sh manager.sh build / test / start 一致）
rem
rem  安全: 检测到 ScBrowser 正在运行时先列出进程并询问；只有输入 Y（或 YES）
rem        才关闭（先尝试正常关闭，12 秒超时后强制结束）并继续构建 / 启动，
rem        其它输入一律取消。不会静默结束任何进程。
rem        关闭运行实例是单实例语义所需：不关闭直接启动只会把参数转交给旧进程，
rem        等于没有跑新代码。
rem
rem  实现注意: tasklist / taskkill / findstr / ping / where 一律用 System32 全路径——
rem        git-bash 派生的 cmd 里 PATH 可能先命中 GNU 同名工具，裸名会失灵
rem        （检测漏判 = 静默跳过询问；延迟失效 = 等待变成瞬时转完）。
rem        tasklist /FI 的退出码恒为 0（无匹配也 0），只能解析输出判「有没有在跑」。
rem ============================================================

set "SYS=%SystemRoot%\System32"

pushd "%~dp0"

"%SYS%\where.exe" dotnet >nul 2>&1
if errorlevel 1 goto :NoDotnet

rem ---- 运行中的实例检测 ----
rem tasklist /FI 的退出码在本机恒为 0（无匹配也 0），不能拿来判；改为用
rem System32 全路径的 findstr 解析输出（全路径 = 不受 git-bash PATH 的 GNU 工具干扰）。
"%SYS%\tasklist.exe" /FI "IMAGENAME eq ScBrowser.exe" 2>nul | "%SYS%\findstr.exe" /I /C:"ScBrowser.exe" >nul
if errorlevel 1 goto :Build

echo.
echo [WARN] 检测到 ScBrowser 正在运行：
"%SYS%\tasklist.exe" /FI "IMAGENAME eq ScBrowser.exe"
echo        正常关闭会保存会话；超时强杀可能丢失最后的状态。
echo.
set "ANSWER="
set /p ANSWER="是否关闭并继续构建？输入 Y 确认，其它任意输入取消: "
if /I "%ANSWER%"=="Y"  goto :DoClose
if /I "%ANSWER%"=="YES" goto :DoClose
goto :Cancelled

:DoClose
echo [INFO] 尝试正常关闭...
"%SYS%\taskkill.exe" /IM ScBrowser.exe >nul 2>&1

rem 等待用 ping 延时（约 1 秒/次）：timeout 在 stdin 被重定向时会报错立即返回，
rem 等待会瞬间转完；ping 不读 stdin，两种环境都按真实 12 秒走。
set /a WAIT=0
:WaitClose
"%SYS%\tasklist.exe" /FI "IMAGENAME eq ScBrowser.exe" 2>nul | "%SYS%\findstr.exe" /I /C:"ScBrowser.exe" >nul
if errorlevel 1 goto :Closed
set /a WAIT+=1
if %WAIT% GEQ 12 goto :ForceClose
"%SYS%\ping.exe" -n 2 127.0.0.1 >nul
goto :WaitClose

:ForceClose
echo [WARN] 12 秒未退出，强制结束...
"%SYS%\taskkill.exe" /F /IM ScBrowser.exe >nul 2>&1
rem 强杀是异步生效的，给 1 秒再复查，避免把「即将消失的进程」误判成关不掉。
"%SYS%\ping.exe" -n 2 127.0.0.1 >nul
"%SYS%\tasklist.exe" /FI "IMAGENAME eq ScBrowser.exe" 2>nul | "%SYS%\findstr.exe" /I /C:"ScBrowser.exe" >nul
if not errorlevel 1 (
    echo [ERROR] 无法关闭运行中的 ScBrowser，请手动退出后重试。
    popd
    exit /b 1
)

:Closed
echo [INFO] 已关闭运行中的实例，继续构建。

:Build
echo ============================================================
echo  Dev    ^|  build.bat + test.bat + 启动 Debug 版
echo ============================================================

call "%~dp0build.bat"
if errorlevel 1 goto :BuildFailed
call "%~dp0test.bat"
if errorlevel 1 goto :BuildFailed

set "EXE=%~dp0src\ScBrowser\bin\Debug\net10.0-windows\ScBrowser.exe"
if not exist "%EXE%" (
    echo [ERROR] 未找到 Debug 产物：%EXE%
    popd
    exit /b 1
)

echo.
echo [INFO] 启动 %EXE%
start "" "%EXE%" %*
popd
exit /b 0

:Cancelled
echo.
echo [INFO] 已取消启动：未关闭任何进程，也未启动应用。
popd
exit /b 2

:BuildFailed
echo.
echo [FAILED] 构建或测试失败，未启动应用。
popd
exit /b 1

:NoDotnet
echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
popd
exit /b 1
