# sc-browser

基于 **WPF + Microsoft Edge WebView2** 的 Windows 桌面浏览器。
外壳用 WPF 原生控件实现，网页内容与内部页面由 WebView2 承载，支持 **Chrome 扩展加载**。

> 参考项目：[MicrosoftEdge/WebView2Browser](https://github.com/MicrosoftEdge/WebView2Browser)（C++ Win32 示例）。
> 本项目沿用其多 WebView 思路，但在技术栈与架构上做了三处关键调整——**外壳改 WPF 原生**、
> **引入分层架构与依赖注入**、**存储改 SQLite**。理由见 [`docs/01-技术栈说明.md`](docs/01-技术栈说明.md)。

## 技术栈

C# / .NET 10（`net10.0-windows`）· WPF（外壳 UI）· Microsoft Edge WebView2（网页与内部页面）
· SQLite（历史/书签/下载）· 原生 HTML/JS/CSS（内部页面 WebUI）。
> 选型理由与各组件职责分工见 [`docs/01-技术栈说明.md`](docs/01-技术栈说明.md)（单一信息源，勿在此重复维护）。

## 功能

- 多标签浏览（新建/关闭/切换）、前进/后退、刷新/停止、主页、网址与搜索词自动判定
- 页面安全状态图标、favicon、标题与加载状态同步
- 历史记录（去重 + 访问计数 + 关键字搜索）· 书签（Ctrl+D 一键收藏）· 下载（进度实时展示）
- 内部页面：新标签页 / 历史 / 书签 / 下载 / 扩展程序 / 设置
- **Chrome 扩展**：加载已解压扩展、安装 `.crx`（自动解包）、启用停用、卸载
- 深浅主题热切换、会话保存与恢复、完整的键盘快捷键

## 快速开始

```bash
# 1. 构建
sh manager.sh build

# 2. 运行（前台，Ctrl-C 退出）
sh manager.sh run

# 3. 跑测试
sh manager.sh test
```

> Windows 上也可在 Visual Studio 中直接打开 `sc-browser.slnx` 构建运行（需 VS 2022 17.13+）。
> 前置条件：**Windows 10 1809+**、**.NET 10 SDK**、**WebView2 运行时**（Win10/11 通常已随 Edge 预装）。

## 常用命令

```bash
sh manager.sh help        # 查看全部命令
sh manager.sh verify      # 提交前闸门：构建(-warnaserror) + 单测 + 文档体检
sh manager.sh start       # 后台运行已构建的 exe
sh manager.sh logs        # 跟踪应用日志
sh manager.sh data        # 查看应用数据目录
sh manager.sh publish     # 发布自包含单文件到 artifacts/publish
```

## 项目结构

```
src/ScBrowser/            # WPF 应用（Browser 引擎 / Storage 存储 / Services 服务 / Views 视图 / Assets WebUI）
tests/ScBrowser.Tests/    # xUnit 单元测试
samples/extensions/       # 示例 Chrome 扩展（验证扩展加载链路）
docs/                     # 使用/维护文档（面向人）
AGENTS.md                 # AI 工作铁律（AI 开工先读）
ARCHITECTURE.md           # 代码链路图谱（面向 AI）
ROADMAP.md                # 开发路线图（总体计划与完成度）
CHANGELOG.md              # 发布变更记录（按版本）
HANDOFF.md                # 会话接力（进行中状态）
manager.sh                # 统一管理脚本
```

## 已知约束

- **仅 Windows**：依赖 WPF 与 WebView2。
- **扩展只能加载「解压后的目录」**（WebView2 平台限制）；`.crx` 由本应用先解包再加载，
  没有扩展商店、不校验签名、不自动更新。
- 安装扩展后**不要改动其目录内的文件**，否则该扩展会被 WebView2 自动卸载。
- 内部页面无法使用 `fetch` 直连宿主，必须走消息桥（见 `ARCHITECTURE.md` §3.1）。

## 文档

- 怎么用 / 出问题怎么查 → [`docs/02-使用说明.md`](docs/02-使用说明.md)
- 代码在哪 / 怎么连 → [`ARCHITECTURE.md`](ARCHITECTURE.md)
- 要做什么 / 做到哪了 → [`ROADMAP.md`](ROADMAP.md)

> AI 协作说明：本项目采用一套「AI 协作文档体系」，AI 代理开工请先读 [`AGENTS.md`](./AGENTS.md)。
