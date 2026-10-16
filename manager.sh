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

cmd_publish() {
  require_cmd dotnet
  local config="${1:-Release}"
  local out="${PROJECT_ROOT}/artifacts/publish"
  log "发布自包含单文件（${config}）→ ${out}"
  dotnet publish "${APP_PROJ}" -c "${config}" -r win-x64 --self-contained true \
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -o "${out}"
  ok "产物：${out}"
}

cmd_clean() {
  require_cmd dotnet
  log "清理构建产物"
  dotnet clean "${SLN}" >/dev/null 2>&1 || true
  rm -rf "${PROJECT_ROOT}/artifacts"
  find "${PROJECT_ROOT}/src" "${PROJECT_ROOT}/tests" -type d \( -name bin -o -name obj \) \
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
  publish [Debug|Release]   发布自包含单文件到 artifacts/publish
  clean               清理构建产物
  data                查看应用数据目录
  logs [n]            跟踪应用日志末尾 n 行（默认 80）
  reset               清空全部本地数据（危险，需确认）
  help                显示本帮助

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
