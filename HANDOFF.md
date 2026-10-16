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
- **整体状态**：✅ 无进行中的开发任务（v0.1.0 已交付并提交）
- **当前分支（必填）**：`main`（v0.1.0 已作为根提交入库；后续开发建议开 feature 分支）
- **最后验证状态**：`sh manager.sh verify` ✅（构建 -warnaserror 0 错误 + 36/36 单测 + 文档体检）；
  实跑验证 ✅（真实站点导航、Ctrl+T 在网页聚焦下生效、扩展内容脚本注入、历史跨重启持久化）

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

- （无。工作区全部为新增文件，尚未 `git commit`。）

## 阻塞 / 待决策（Blockers）

- （无。）

---

## 下一步（Next Steps）

1. （暂无既定下一步。新需求到来时：按 `ARCHITECTURE.md` §0.5 定范围 → 定位函数 → 实施 → 文档同步。）
2. 若要继续推进产品功能，`ROADMAP.md` §🟡 近期规划 的 P1-1 ~ P1-5 是已评估过的候选。

---

## 如何运行 / 测试（Quick Verify）

- 构建：`sh manager.sh build`
- 运行：`sh manager.sh run`（或 `sh manager.sh start` 跑已构建的 exe）
- 测试：`sh manager.sh test`
- 提交前闸门：`sh manager.sh verify`（构建 -warnaserror + 单测 + 文档体检）
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

---

## 待解问题 / 给用户的提问（Open Questions）

- **尚未推送到远程**：仓库已配置 `origin = https://github.com/zouv/sc-browser`，
  但本地两个提交都还没 push。需要推送时请明确指示。
