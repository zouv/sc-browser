#!/usr/bin/env bash
# =============================================================================
# manager.sh — sc-browser 统一管理脚本
# 用法：./manager.sh <command> [args]    (Windows: sh manager.sh <command>)
#
# 本项目管理 .NET 解决方案（dotnet CLI），主工程为 WPF 桌面应用 ScBrowser。
# 本脚本封装：构建 / 运行 / 测试 / 发布 / 清理 / 运行时数据查看。
# =============================================================================
set -uo pipefail

PROJECT_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_PROJ="${PROJECT_ROOT}/src/ScBrowser/ScBrowser.csproj"
TEST_PROJ="${PROJECT_ROOT}/tests/ScBrowser.Tests/ScBrowser.Tests.csproj"
INSTALLER_PROJ="${PROJECT_ROOT}/installer/ScBrowser.Installer.csproj"
INSTALLER_SCRIPT="${PROJECT_ROOT}/installer/ScBrowser.iss"
PACKAGES_DIR="${PROJECT_ROOT}/packages"
DEV_DIR="${PACKAGES_DIR}/dev"
RELEASE_DIR="${PACKAGES_DIR}/release"
STAGING_DIR="${PROJECT_ROOT}/.runtime/package"
RUNTIME_DIR="${PROJECT_ROOT}/.runtime"

# 解决方案文件：.NET 10 默认生成新版 XML 格式 .slnx，这里两种都认，避免换 SDK 后脚本失效。
SLN="$(ls "${PROJECT_ROOT}"/sc-browser.slnx "${PROJECT_ROOT}"/sc-browser.sln 2>/dev/null | head -n1)"

# 应用运行期数据目录（与 Infrastructure/AppPaths.cs 保持一致：%LOCALAPPDATA%\sc-browser）
if [ -n "${LOCALAPPDATA:-}" ]; then
  APPDATA_DIR="${LOCALAPPDATA}/sc-browser"
else
  APPDATA_DIR="${HOME}/.local/share/sc-browser"
fi

log()  { printf '\033[1;34m[manager]\033[0m %s\n' "$*"; }
ok()   { printf '\033[1;32m[ ok  ]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[warn ]\033[0m %s\n' "$*"; }
err()  { printf '\033[1;31m[err  ]\033[0m %s\n' "$*" >&2; }

require_cmd() { command -v "$1" >/dev/null 2>&1 || { err "缺少命令：$1"; exit 1; }; }
ensure_dirs() { mkdir -p "${RUNTIME_DIR}"; }

# --------------------------------------------------------------------------- #
# 构建
# --------------------------------------------------------------------------- #

cmd_build() {
  require_cmd dotnet
  log "构建解决方案（Debug）"
  dotnet build "${SLN}" "$@"
}

cmd_release() {
  require_cmd dotnet
  log "构建解决方案（Release）"
  dotnet build "${SLN}" -c Release "$@"
}

cmd_run() {
  require_cmd dotnet
  log "启动 sc-browser（前台，Ctrl-C 退出）"
  log "提示：可传启动参数，如 --install-extension <扩展目录或 .crx>"
  dotnet run --project "${APP_PROJ}" "$@"
}

# 直接运行已构建的可执行文件（跳过 dotnet run 的额外开销，便于反复试跑）
cmd_start() {
  local exe
  exe="$(find "${PROJECT_ROOT}/src/ScBrowser/bin/Debug" -maxdepth 3 -name 'ScBrowser.exe' 2>/dev/null | head -n1)"
  if [ -z "${exe}" ]; then
    err "未找到已构建的可执行文件，先执行：sh manager.sh build"
    exit 1
  fi
  log "启动 ${exe} $*"
  "${exe}" "$@" &
  ok "已启动（PID=$!）"
}

cmd_stop() {
  if command -v taskkill >/dev/null 2>&1; then
    taskkill //F //IM ScBrowser.exe >/dev/null 2>&1 && ok "已停止" || warn "未在运行"
  else
    pkill -f ScBrowser 2>/dev/null && ok "已停止" || warn "未在运行"
  fi
}

# --------------------------------------------------------------------------- #
# 测试 / 质量
# --------------------------------------------------------------------------- #

cmd_test() {
  require_cmd dotnet
  log "运行单元测试"
  dotnet test "${TEST_PROJ}" "$@"
}

cmd_fmt() {
  require_cmd dotnet
  log "格式化代码（dotnet format）"
  dotnet format "${SLN}" "$@"
}

cmd_verify() {
  require_cmd dotnet
  log "质量闸门：构建 + 测试 + 文档体检"
  dotnet build "${SLN}" -warnaserror --nologo || { err "构建失败"; exit 1; }
  dotnet test "${TEST_PROJ}" --nologo || { err "测试失败"; exit 1; }
  if [ -f "${PROJECT_ROOT}/check-docs.sh" ]; then
    sh "${PROJECT_ROOT}/check-docs.sh" || { err "文档体检未通过"; exit 1; }
  fi
  ok "全部通过"
}

# --------------------------------------------------------------------------- #
# 发布 / 清理 / 诊断
# --------------------------------------------------------------------------- #

# 版本号单一来源：ScBrowser.csproj 的 <Version>。
app_version() {
  dotnet msbuild "${APP_PROJ}" -getProperty:Version -nologo 2>/dev/null | tail -n1
}

# 通过 NuGet 还原 Inno Setup（Tools.InnoSetup），返回 ISCC.exe 的绝对路径。
# 这样打包机不需要预先安装 Inno Setup；首次需要联网，之后走本地 NuGet 缓存。
resolve_iscc() {
  dotnet restore "${INSTALLER_PROJ}" --nologo >/dev/null 2>&1 || return 1
  local iscc
  iscc="$(dotnet msbuild "${INSTALLER_PROJ}" -getProperty:InnoSetupCompiler -nologo 2>/dev/null | tail -n1)"
  [ -n "${iscc}" ] && [ -f "${iscc}" ] || return 1
  printf '%s' "${iscc}"
}

# dev 包的排障信息：版本 / 构建时间 / 提交 / 工具链。
write_build_info() {
  local file="$1"
  local version="$2"
  local channel="$3"
  {
    printf 'ScBrowser %s (%s)\n' "${version}" "${channel}"
    printf 'Built: %s\n' "$(date -Iseconds 2>/dev/null || date)"
    printf 'Git: %s\n' "$(git -C "${PROJECT_ROOT}" rev-parse --short HEAD 2>/dev/null || echo unknown)"
    printf 'Dotnet: %s\n' "$(dotnet --version 2>/dev/null || echo unknown)"
    printf 'OS: %s\n' "$(uname -srm 2>/dev/null || echo unknown)"
  } > "${file}"
}

# 用 Windows 自带的 bsdtar 生成 zip，并把传入目录本身作为 zip 内的根目录。
# 不用 PowerShell 的 ZipFile：Windows PowerShell 5.1 会写出反斜杠条目名，
# 与非 Windows 解压工具不兼容。tar.exe 自 Windows 10 1803 起随系统提供。
zip_directory() {
  local source="$1"
  local dest="$2"
  local tar_win tar_posix dest_win

  tar_win="${SYSTEMROOT:-C:\\Windows}\\System32\\tar.exe"
  tar_posix="$(cygpath -u "${tar_win}" 2>/dev/null || printf '%s' "${tar_win}")"
  if [ ! -f "${tar_posix}" ]; then
    err "生成 zip 需要 Windows 自带的 tar.exe（${tar_win}）。"
    return 1
  fi

  dest_win="$(cygpath -w "${dest}" 2>/dev/null || printf '%s' "${dest}")"
  rm -f "${dest}"
  mkdir -p "$(dirname "${dest}")"

  ( cd "$(dirname "${source}")" && "${tar_posix}" -a -c -f "${dest_win}" "$(basename "${source}")" )
}

cmd_publish() {
  require_cmd dotnet
  if [ "$#" -gt 0 ]; then
    warn "publish 不接受参数（固定产出 dev + release），已忽略：$*"
  fi

  local version iscc
  version="$(app_version)"
  if [ -z "$version" ]; then
    err "无法从 ScBrowser.csproj 读取版本号"
    exit 1
  fi

  if ! iscc="$(resolve_iscc)"; then
    err "无法还原 Inno Setup 工具（NuGet 包 Tools.InnoSetup）。"
    err "请检查网络 / NuGet 配置后重试：dotnet restore \"${INSTALLER_PROJ}\""
    exit 1
  fi

  local dev_unpack="${STAGING_DIR}/dev/ScBrowser_${version}_x64-unpack-debug"
  local rel_unpack="${STAGING_DIR}/release/ScBrowser_${version}_x64-unpack"
  local dev_logs="${dev_unpack}/logs"
  local dev_zip="${DEV_DIR}/ScBrowser_${version}_x64-unpack-debug.zip"
  local rel_zip="${RELEASE_DIR}/ScBrowser_${version}_x64-unpack.zip"
  local setup_exe="${RELEASE_DIR}/ScBrowser_${version}_x64-setup.exe"

  # 结束可能驻留的实例，避免 exe 被占用导致 publish 覆盖失败。
  if command -v taskkill >/dev/null 2>&1; then
    taskkill //F //IM ScBrowser.exe >/dev/null 2>&1 || true
  fi

  log "清理旧打包产物与暂存目录"
  rm -rf "$DEV_DIR" "$RELEASE_DIR" "$STAGING_DIR"
  mkdir -p "$DEV_DIR" "$RELEASE_DIR" "$dev_logs" "$rel_unpack"

  # 1) dev：Debug 发布（框架依赖，含 PDB）+ 构建日志，供开发 / 排障。
  log "打包 dev（Debug + PDB + 构建日志）"
  if ! dotnet publish "$APP_PROJ" -c Debug -r win-x64 --self-contained false \
      -p:PublishSingleFile=false -p:PublishReadyToRun=false \
      -o "$dev_unpack" --nologo > "$dev_logs/dev-publish.log" 2>&1; then
    err "dev 发布失败，日志：${dev_logs}/dev-publish.log"
    tail -n 40 "$dev_logs/dev-publish.log" >&2
    exit 1
  fi

  # 2) release：自包含的 unpack 目录（目标机无需安装 .NET 运行时）。
  #    Release 不保留 PDB；调试符号只进 dev 包。
  log "打包 release（自包含 unpack 目录）"
  if ! dotnet publish "$APP_PROJ" -c Release -r win-x64 --self-contained true \
      -p:PublishSingleFile=false -p:PublishReadyToRun=false \
      -p:DebugType=None -p:DebugSymbols=false \
      -o "$rel_unpack" --nologo > "$dev_logs/release-publish.log" 2>&1; then
    err "release 发布失败，日志：${dev_logs}/release-publish.log"
    tail -n 40 "$dev_logs/release-publish.log" >&2
    exit 1
  fi

  # 3) 包内说明 / 许可证 / 排障信息。
  cp "$PROJECT_ROOT/LICENSE" "$dev_unpack/LICENSE"
  cp "$PROJECT_ROOT/LICENSE" "$rel_unpack/LICENSE"

  cat > "$dev_unpack/README.txt" <<EOF
ScBrowser ${version} (Debug, x64)
=================================

这是 Debug 构建（框架依赖），运行本目录的 ScBrowser.exe 需要：
  - Windows 10 1809+ / Windows 11
  - .NET 10 桌面运行时
  - WebView2 运行时（Win10/11 通常已随 Edge 预装）

调试信息：
  - ScBrowser.pdb
  - build-info.txt（版本 / 构建时间 / Git 提交 / SDK）
  - logs/（本次 dev / release 发布与安装程序编译日志）

用户数据位于 %LOCALAPPDATA%\sc-browser。
EOF

  cat > "$rel_unpack/README.txt" <<EOF
ScBrowser ${version} (x64, self-contained)
==========================================

这是免安装的 Release 版本：双击 ScBrowser.exe 即可运行，
目标机无需安装 .NET 运行时；仍需 WebView2 运行时
（Windows 10/11 通常已随 Edge 预装）。

用户数据位于 %LOCALAPPDATA%\sc-browser。
EOF

  write_build_info "$dev_unpack/build-info.txt" "$version" "dev"

  # 4) Setup.exe：按用户安装到 %LOCALAPPDATA%\Programs\ScBrowser。
  log "编译安装程序 → ${setup_exe}"
  if ! "$iscc" "--define=MyAppVersion=${version}" "$INSTALLER_SCRIPT" \
      > "$dev_logs/setup-compile.log" 2>&1; then
    err "安装程序编译失败，日志：${dev_logs}/setup-compile.log"
    tail -n 40 "$dev_logs/setup-compile.log" >&2
    exit 1
  fi

  # 5) 生成 dev / release 的 unpack zip（zip 内带单一根目录）。
  log "生成 unpack zip"
  if ! zip_directory "$dev_unpack" "$dev_zip"; then
    err "生成 dev zip 失败（暂存目录保留：${STAGING_DIR}）"
    exit 1
  fi
  if ! zip_directory "$rel_unpack" "$rel_zip"; then
    err "生成 release zip 失败（暂存目录保留：${STAGING_DIR}）"
    exit 1
  fi

  # 6) 暂存目录只在成功时删除；失败时保留供排查。
  rm -rf "$STAGING_DIR"

  ok "打包完成（版本 ${version}）"
  printf '  dev     : %s\n' "$dev_zip"
  printf '  unpack  : %s\n' "$rel_zip"
  printf '  setup   : %s\n' "$setup_exe"
}

cmd_clean() {
  require_cmd dotnet
  log "清理构建产物（保留 packages 成品包）"
  dotnet clean "${SLN}" >/dev/null 2>&1 || true

  # 历史产物目录：旧版本发布到 artifacts，迁移后顺手清掉残留。
  rm -rf "${PROJECT_ROOT}/artifacts"

  find "${PROJECT_ROOT}/src" "${PROJECT_ROOT}/tests" "${PROJECT_ROOT}/installer" \
    -type d \( -name bin -o -name obj \) \
    -prune -exec rm -rf {} + 2>/dev/null || true
  ok "已清理"
}

# 查看应用运行期数据（用户数据目录 / 日志 / 数据库）
cmd_data() {
  log "应用数据目录：${APPDATA_DIR}"
  if [ ! -d "${APPDATA_DIR}" ]; then
    warn "目录不存在（应用还没运行过？）"
    return 0
  fi
  ls -la "${APPDATA_DIR}"
}

cmd_logs() {
  local dir="${APPDATA_DIR}/logs"
  if [ ! -d "${dir}" ]; then
    warn "暂无日志目录：${dir}"
    return 0
  fi
  local latest
  latest="$(ls -t "${dir}"/*.log 2>/dev/null | head -n1)"
  if [ -z "${latest}" ]; then
    warn "暂无日志文件"
    return 0
  fi
  log "跟踪 ${latest}"
  tail -n "${1:-80}" -f "${latest}"
}

# 一键重置：清空应用数据（书签/历史/扩展安装态/Cookie）。危险操作，需二次确认。
cmd_reset() {
  warn "这将删除应用的全部本地数据：书签、历史、下载记录、已安装扩展、Cookie 与缓存。"
  printf '输入 yes 确认：'
  read -r answer
  if [ "${answer}" != "yes" ]; then
    warn "已取消"
    return 0
  fi
  rm -rf "${APPDATA_DIR}"
  ok "已重置 ${APPDATA_DIR}"
}

cmd_help() {
  cat <<EOF
sc-browser — manager.sh

用法：
  ./manager.sh <command> [args]    (Windows: sh manager.sh <command>)

构建 / 运行：
  build [args]        构建（Debug）
  release [args]      构建（Release）
  run [args]          前台启动（dotnet run，可传启动参数）
  start [args]        运行已构建的 exe（后台）
  stop                结束 ScBrowser 进程

测试 / 质量：
  test [args]         运行单元测试
  verify              构建(-warnaserror) + 测试 + 文档体检（提交前跑这个）
  fmt [args]          格式化代码

发布 / 维护：
  publish             打包到 packages/（dev zip + release setup/unpack zip）
  clean               清理构建产物（保留 packages 成品包）
  data                查看应用数据目录
  logs [n]            跟踪应用日志末尾 n 行（默认 80）
  reset               清空全部本地数据（危险，需确认）
  help                显示本帮助

打包产物：
  packages/dev/       ScBrowser_<版本>_x64-unpack-debug.zip
  packages/release/   ScBrowser_<版本>_x64-setup.exe + ScBrowser_<版本>_x64-unpack.zip

应用数据目录：${APPDATA_DIR}
EOF
}

main() {
  local cmd="${1:-help}"
  shift || true
  case "${cmd}" in
    build)   cmd_build   "$@" ;;
    release) cmd_release "$@" ;;
    run)     cmd_run     "$@" ;;
    start)   cmd_start   "$@" ;;
    stop)    cmd_stop    "$@" ;;
    test)    cmd_test    "$@" ;;
    verify)  cmd_verify  "$@" ;;
    fmt)     cmd_fmt     "$@" ;;
    publish) cmd_publish "$@" ;;
    clean)   cmd_clean   "$@" ;;
    data)    cmd_data    "$@" ;;
    logs)    cmd_logs    "$@" ;;
    reset)   cmd_reset   "$@" ;;
    help|-h|--help) cmd_help ;;
    *) err "未知命令：${cmd}"; cmd_help; exit 2 ;;
  esac
}

main "$@"
