# 接力进度 / Handoff

> **这是什么**：AI 断点续作的「交接包」。一次对话没干完，**下一个 AI 会话先读本文件**，
> 就知道「做到哪、还剩什么、卡在哪、下一步从哪开始」，无需从零重新理解。
>
> **给接手的 AI（冷启动按这 4 步走）**：
> 1. 先读 `AGENTS.md`（工作铁律）；据「当前任务」范围，按 `ARCHITECTURE.md` §0.5 路由表**只读相关文件**。
> 2. 读本文件「当前任务 / 进行中 / 下一步 / 完成定义」，从「下一步」第一条动作开始。
> 3. 每完成一个子步骤就**即时勾选并更新本文件**（不要攒到最后）。
> 4. 任务真正完结：计划条目在 `ROADMAP.md` 打 ✅，发布细节记入 `CHANGELOG.md`，
>    并把本文件里**开工时写下的整套计划痕迹一并清掉**，状态改回「无进行中任务」。
>
> **维护约定**：本文件只留「**进行中**的临时状态」。已完结内容当场整段删除，
> 禁止只标「已并入」不删。**超过 150 行即视为堆积超标**。

---

## 状态快照

- **最近更新**：2026-10-25
- **整体状态**：✅ 无进行中的开发任务（体检加固 S1/S2 与 S3 标签惰性恢复已完成；后续见 `ROADMAP` §🟡 S3b / F-* / E-*）
- **当前分支（必填）**：`main`
- **最后验证状态**：
  · 2026-10-25：`sh manager.sh verify` ✅（**141/141** + 文档体检）。
  · 隔离实例实跑（`SCBROWSER_DATA_DIR`）✅：
    - 内部页面在 `browser://` 下渲染与取数正常；本地 http 恶意页调宿主能力被拒且收不到回执，跳 `browser://` 被拦。
    - 带网址二次启动 1s 内转交主实例开新标签；20s 定期存会话。
    - 结束渲染进程出现崩溃条，「重新加载」恢复。
    - **惰性恢复**：5 标签会话只建 1 个渲染进程，标签条显示地址/「历史记录」「扩展程序」；
      点开惰性标签后引擎创建（渲染进程 1→2）、地址栏正确；会话文件仍含全部 5 个标签。
  · 未端到端验证：`Ctrl+W` 关最后一个标签后清会话、下载页按 id 打开（需真实下载一次）。
- **已发布**：`v0.2.2`（2026-10-22，https://github.com/zouv/sc-browser/releases/tag/v0.2.2 ）。

---

## 当前任务（In Progress）

- **目标**：无（S1/S2 已完结，成果见 `CHANGELOG [未发布]` 与 `ROADMAP` M5）
- **范围 / 涉及文件**：无
- **完成定义（Definition of Done）**：无

---

## 待归档暂存区（仅本轮未归档的完成项）

- （无。）

## 进行中（半成品 / 未提交）

- S1/S2 全部改动未提交（`git status` 可见）。提交信息按 AGENTS §5.3 用 `安全：` / `修复：` 等前缀。

## 阻塞 / 待决策（Blockers）

- S3「标签状态与引擎拆分」是架构级重构，开工前需用户确认（会改变标签生命周期与会话恢复行为）。

---

## 下一步（Next Steps）

1. 推进顺序见 `ROADMAP.md` §🟡：**S3b**（后台标签休眠 / 拖出成窗口 / 内核主进程退出后重建引擎）→
   **S4** 数据层加固 → **F-*** 日常功能 → **E-*** 扩展兼容。
   S3b 的起点：`BrowserTab.EnsureInitializedAsync`（引擎按需创建）与 `TabManager.Activate`（激活入口）。
2. `ARCHITECTURE.md` 约 384 行，接近 400 上限；下次再涨先把 §2 任务反查表拆到 `arch/`。
3. 发布流程（下次发版照做）：`sh manager.sh package`（或 `package.bat`）→
   从 `dist/release/` 取 `ScBrowser_<版本>_x64-setup.exe` 与 `ScBrowser_<版本>_x64-unpack.zip`
   → `gh release create <tag> <文件>`。**两者必须同时上传**；`dist/dev` 的 Debug zip 不上传。

## 如何运行 / 测试（Quick Verify）

- 构建：`sh manager.sh build`（无 bash 环境用 `build.bat`）
- 运行：`sh manager.sh run`（或 `sh manager.sh start` 跑已构建的 exe）；`run.bat` 为等价物
- 测试：`sh manager.sh test`（`test.bat`）
- 提交前闸门：`sh manager.sh verify`（构建 -warnaserror + 单测 + 文档体检）；`verify.bat` 等价
  （文档体检需 bash，无 `sh`/`bash` 时只告警跳过）
- 一键 CI：`ci.bat`（clean → verify → package 到 `dist\`）；打包另可 `sh manager.sh package`
- 打包产物：`dist/dev/ScBrowser_<版本>_x64-unpack-debug.zip`、
  `dist/release/unpack/` +
  `dist/release/ScBrowser_<版本>_x64-unpack.zip` +
  `ScBrowser_<版本>_x64-setup.exe`；`clean` 保留 `dist`。
- 看日志：`sh manager.sh logs`
- 端到端自查技巧：设 `SCBROWSER_DATA_DIR=<临时目录>` 起隔离实例（不打扰日常实例），往该目录的 `session.json` 写
  `{"Urls":["browser://extensions"],"ActiveIndex":0,"SavedAt":"2026-10-16T00:00:00+00:00"}` 再启动，
  可直接落在指定内部页面；后台窗口截图用 `PrintWindow`（外壳区截不到，外壳状态用 UI Automation 读）；`ScBrowser.exe --install-extension <目录|.crx>` 可验证扩展安装。

---

## 关键决策记录（Key Decisions）

> 记录「为什么这么做」，避免后续 AI 推翻或重复踩坑。完整版见 `ARCHITECTURE.md` §7。

- **外壳用 WPF 原生控件，不用 WebView**：参考项目 WebView2Browser 用第二个 WebView 环境承载外壳 UI，
  本项目改为原生控件——省一整套 UI 环境，且系统集成（焦点/输入法/无障碍/DPI）更省心。
  Chrome / Edge 的外壳同样是原生自绘（Views + Skia），此为业界主流做法。
- **每个标签页一个 WebView2，不做多标签复用**：换来切换零延迟与页面状态保留，代价是内存开销，
  与 Chrome 的进程模型一致。**不要为省内存改回单 WebView2 反复导航。**
- **只用一个 `CoreWebView2Environment`**：所有标签共享 Cookie/缓存/扩展安装态；扩展按 profile 管理正需要这个前提。
- **快捷键放在窗口级 `PreviewKeyDown`**：经反射核实，WPF 版 WebView2 内部会把
  `CoreWebView2Controller.AcceleratorKeyPressed` 转发成 WPF 的 `PreviewKeyDown` 重新抛出，
  因此无需反射取 controller。**前提**是关掉 `AreBrowserAcceleratorKeysEnabled`。已实测生效。
- **存储选 SQLite 而非 JSON/IndexedDB**：需要按时间倒序分页、按关键字搜索、按访问次数取热门站点，
  这些用 SQL 是几行的事。
- **扩展只能装「解包目录」**：WebView2 平台限制。为让用户能装网上下载的 `.crx`，
  自实现 CRX 解包（v2/v3 头解析 + 目录穿越防护）。
- **内部页面统一走 WebUI（HTML/JS）**：与 Chrome 的 `chrome://` 页面同构，
  复用项目「C#+JS」栈里的 JS 半边，改版快；代价是要额外维护一套消息协议。
- **无痕不做第二套环境**：WebView2 的 InPrivate 是**控制器/profile 级**能力，
  所以无痕窗口仍复用全应用那唯一一个 `CoreWebView2Environment`，只换
  `IsInPrivateModeEnabled` 的控制器选项——「单环境」铁律不破。
- **per-window 对象不进 DI 容器**：`TabManager` / `MainViewModel` / `MainWindow` 由
  `BrowserWindowManager` 手工 new。容器表达不了「按会话类型构造」，且单例服务一旦持有
  某个窗口实例，多窗口下必错。
- **窗口缩放靠「内缩一圈」而不是加大命中带**：缩放命中带必须落在 WPF 自绘的区域上，
  因为 WebView2 是独立子 HWND、会吃掉落在它身上的鼠标事件（见 ARCHITECTURE §7）。
- **下拉/菜单/浮层一律走独立顶层窗口**：airspace 决定的，不是风格选择——WPF 元素盖不到 WebView2 上。
- **内部页面走 `browser://` 自定义 scheme + 消息桥按来源鉴权**：WebView2 给所有网页都注入消息桥，
  `file://` 无法区分来源、https 虚拟主机会被扩展 `<all_urls>` 注入，所以选自定义 scheme（详见 ARCHITECTURE §7）。
- **单实例按数据根目录区分**：`SCBROWSER_DATA_DIR` 起的测试实例可与日常实例并存，方便实跑验证。
- **标签状态与引擎分离**：`BrowserTab` 不再在构造函数里 `new WebView2()`，引擎由 `EnsureInitializedAsync`
  按需创建；`View`/`Core` 可能为 `null`，导航与缩放会记为待处理、初始化完成后补上。
  会话恢复只立即加载激活标签——这就是惰性恢复（实测 5 标签只起 1 个渲染进程）。
- **页内查找优先原生、旧运行时走 JS 兜底**：`CoreWebView2.Find` 需要 `ICoreWebView2_28`，
  本机 WebView2 131 没有该接口（访问即抛 `NotImplementedException`）。`BrowserTab` 在原生不可用
  或启动失败时退回 `ExecuteScriptAsync` + `TreeWalker` + `CSS.highlights`；升级运行时后自动走原生。
  **不要为「只留一条实现」删掉兜底**，否则旧运行时会直接失去查找功能。
- **打包统一到 `dist/`，脚本名为 `package`**：脚本与产物目录对齐，避免 `packages/`
  在 .NET 里被误解为 NuGet 依赖目录。`dist/dev/ScBrowser_<版本>_x64-unpack-debug.zip`
  （Debug + PDB + 日志 + build-info，zip 内带单一根目录）与
  `dist/release/unpack/`（可直接运行）+
  `ScBrowser_<版本>_x64-unpack.zip`（zip 根下直接是程序文件）+ `ScBrowser_<版本>_x64-setup.exe`。
  zip 都由 Windows 自带的 `tar.exe` 生成（正斜杠条目）；
  **不要换回 PowerShell 的 ZipFile**（Windows PowerShell 5.1 会写出反斜杠条目名）。
  安装器工具链通过 NuGet `Tools.InnoSetup` 还原到本地缓存，`installer/ScBrowser.Installer.csproj`
  只负责取 ISCC.exe 路径，**不要让打包机依赖系统预装的 Inno Setup**。
  安装按用户落到 `%LOCALAPPDATA%\Programs\ScBrowser`（无 UAC），卸载器只删程序文件，
  **绝不能**删除 `%LOCALAPPDATA%\sc-browser` 里的用户数据。

---

## 待解问题 / 给用户的提问（Open Questions）

- **无痕的扩展支持未验证**：WebView2 的 InPrivate profile 下扩展行为未实测（普通窗口的扩展链路已验证）。
  如果以后要在无痕里用扩展，先读 `Browser/ExtensionService.cs` 的注释再动手。
- **行尾属性后加的坑（已修，供备忘）**：行尾规则只管「以后怎么检出」，**不会自动改写已存在的文件**——
  `.gitattributes` 加上 `*.sh = LF` 后，工作区里那份 CRLF 的 `check-docs.sh` 依旧带 `\r` 干活，
  且 `git status` 显示干净（提交时会被规范化，看不出差异）。要真修必须 `rm` 后 `git checkout -- <file>` 重新落盘。
