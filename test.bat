@echo off
chcp 65001 >nul
setlocal

rem ============================================================
rem  sc-browser - 测试脚本（xUnit 单元测试）
rem  用法: test.bat [Debug^|Release]    默认 Debug
rem  等价命令: sh manager.sh test
rem ============================================================

set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Debug"

pushd "%~dp0"
set "TEST_PROJ=tests\ScBrowser.Tests\ScBrowser.Tests.csproj"

echo ============================================================
echo  Test   ^|  Config = %CONFIG%
echo ============================================================

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [ERROR] 未找到 dotnet CLI，请先安装 .NET 10 SDK。
    popd
    exit /b 1
)

dotnet test "%TEST_PROJ%" -c %CONFIG% --nologo --logger "console;verbosity=normal"
if errorlevel 1 (
    echo.
    echo [FAILED] 部分测试失败。
    popd
    exit /b 1
)

echo.
echo [SUCCESS] 全部测试通过。
popd
exit /b 0
