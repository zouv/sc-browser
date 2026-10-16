@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 提交前质量闸门
rem  构建(-warnaserror) -> 单元测试 -> 文档体检(check-docs.sh，需 bash)
rem  用法: verify.bat
rem  等价命令: sh manager.sh verify
rem ============================================================

pushd "%~dp0"
set "SLN=sc-browser.slnx"
set "TEST_PROJ=tests\ScBrowser.Tests\ScBrowser.Tests.csproj"

echo ============================================================
echo  Verify ^|  build -warnaserror + test + check-docs
echo ============================================================

where dotnet >nul 2>&1
if errorlevel 1 goto :NoDotnet

dotnet build "%SLN%" -warnaserror --nologo
if errorlevel 1 goto :BuildFailed

dotnet test "%TEST_PROJ%" --nologo
if errorlevel 1 goto :TestFailed

rem 文档体检是 bash 脚本：有 Git for Windows / WSL 才跑得起来，没有就只提示不失败。
rem 先找 sh，再退而求其次找 bash（Git for Windows 默认只把 Git\cmd 加进 PATH，
rem 那里有 git.exe 但未必有 sh.exe）。
where sh >nul 2>&1
if not errorlevel 1 goto :RunDocCheck
where bash >nul 2>&1
if not errorlevel 1 goto :UseBash
goto :NoSh

:RunDocCheck
sh check-docs.sh
if errorlevel 1 goto :DocsFailed
goto :Done

:UseBash
bash check-docs.sh
if errorlevel 1 goto :DocsFailed
goto :Done

:NoSh
echo.
echo [WARN] 未找到 sh / bash，跳过文档体检（check-docs.sh）。
echo        装了 Git for Windows 或 WSL 后重跑本脚本即可，命令为：sh check-docs.sh

:Done
echo.
echo [SUCCESS] 全部通过。
popd
exit /b 0

:NoDotnet
echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
popd
exit /b 1

:BuildFailed
echo.
echo [FAILED] 构建失败（-warnaserror：警告即错误）。
popd
exit /b 1

:TestFailed
echo.
echo [FAILED] 测试失败。
popd
exit /b 1

:DocsFailed
echo.
echo [FAILED] 文档体检未通过。
popd
exit /b 1
