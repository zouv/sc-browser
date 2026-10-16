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

- **最近更新**：2026-10-16
- **整体状态**：✅ 无进行中的开发任务（连续三批需求全部完结，未 commit）
- **当前分支（必填）**：`main`（v0.1.0 已作为根提交入库；后续开发建议开 feature 分支）
- **最后验证状态**：`sh manager.sh verify` ✅（构建 -warnaserror + **90/90** 单测 + 文档体检）。
  实跑逐项验过（`PrintWindow` 抓窗 / `WM_NCHITTEST` 探测 / UI Automation 驱动 + 前台窗口守卫的键盘模拟）：
  · `WM_NCHITTEST`：标签条空白 `HTCAPTION`、标签与窗口按钮 `HTCLIENT`、四边/四角为缩放边；
  · 最大化几何 = 工作区 `2560×1410 @(0,0)`；`WindowChrome` 裁边问题已修；
  · 多窗口：菜单触发后进程内出现 3 个顶层窗口（普通 / 无痕 / 又一个普通），无痕窗口标题带「无痕：」且标签条有无痕徽标；
  · 地址栏下拉：输入 `goo` 列出「使用 Bing 搜索」+ 两条历史；`↓↓` 选中历史项后回车 → 标题变为 `Google — sc-browser`；
  · 窗口几何：`SetWindowPos` 改成 1100×700@(700,220) 后正常关闭 → `settings.json` 写入该几何，重启后精确恢复（随后已还原为默认值）。
  · **未能实跑的部分**（鼠标驱动，无合规的自动化手段）：中键关闭标签、拖拽重排的**实际拖拽动作**、
  标签右键菜单的点击、鼠标点地址栏触发下拉——其中拖拽的下标换算已用 `TabReorder` 单测覆盖，
  其余为「XAML 接线 + 代码审读」级别，接手时如需确认请人工点一遍。
- **改动尚未 commit**（工作区：本轮交互改造 + 前两轮的外观/工程化改动）。

---

## 当前任务（In Progress）

- **目标**：无
- **范围 / 涉及文件**：无
- **完成定义（Definition of Done）**：无

> 当前：无进行中的任务，等待下一个开发需求。

---

## 待归档暂存区（仅本轮未归档的完成项）

- （无。）

## 进行中（半成品 / 未提交）

- （无。）

## 阻塞 / 待决策（Blockers）

- （无。）

---

## 下一步（Next Steps）

1. **提交本批改动**（建议按主题拆 3~4 个 commit）：外观（图标/自绘外壳/深色同步）、工程化（行尾 + bat），
   交互（标签页/多窗口+无痕/菜单/地址栏下拉）。
2. `ARCHITECTURE.md` 已按 AGENTS §1.1 拆分：§3 链路详解移到 [`arch/flows.md`](arch/flows.md)
   （主文件 332 行）；§2 任务反查表还留在主文件里，下次再涨就先拆它。
3. 仍需明确指示才推送远程（见下）。

## 如何运行 / 测试（Quick Verify）

- 构建：`sh manager.sh build`（无 bash 环境用 `build.bat`）
- 运行：`sh manager.sh run`（或 `sh manager.sh start` 跑已构建的 exe）；`run.bat` 为等价物
- 测试：`sh manager.sh test`（`test.bat`）
- 提交前闸门：`sh manager.sh verify`（构建 -warnaserror + 单测 + 文档体检）；`verify.bat` 等价
  （文档体检需 bash，无 `sh`/`bash` 时只告警跳过）
- 一键 CI：`ci.bat`（clean → verify → publish 自包含单文件到 `artifacts\publish`）；发布另可 `sh manager.sh publish`
- 看日志：`sh manager.sh logs`
- 端到端自查技巧：往 `%LOCALAPPDATA%\sc-browser\session.json` 写
  `{"Urls":["browser://extensions"],"ActiveIndex":0,"SavedAt":"2026-10-16T00:00:00+00:00"}` 再启动，
  可直接落在指定内部页面；`ScBrowser.exe --install-extension <目录|.crx>` 可验证扩展安装。

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

---

## 待解问题 / 给用户的提问（Open Questions）

- **尚未推送到远程**：仓库已配置 `origin = https://github.com/zouv/sc-browser`，
  本地提交都还没 push；本轮改动也还没 commit。需要时请明确指示。
- **无痕的扩展支持未验证**：WebView2 的 InPrivate profile 下扩展行为未实测（普通窗口的扩展链路已验证）。
  如果以后要在无痕里用扩展，先读 `Browser/ExtensionService.cs` 的注释再动手。
- **行尾属性后加的坑（已修，供备忘）**：行尾规则只管「以后怎么检出」，**不会自动改写已存在的文件**——
  `.gitattributes` 加上 `*.sh = LF` 后，工作区里那份 CRLF 的 `check-docs.sh` 依旧带 `\r` 干活，
  且 `git status` 显示干净（提交时会被规范化，看不出差异）。要真修必须 `rm` 后 `git checkout -- <file>` 重新落盘。
