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

- **最近更新**：2026-10-29
- **整体状态**：✅ 无进行中的开发任务（**v0.5.0 已打包并发布**）
- **当前分支（必填）**：`main`
- **最后验证状态**：
  · 2026-10-29：**v0.5.0 已提交推送并发布**（https://github.com/zouv/sc-browser/releases/tag/v0.5.0，
    上传 `ScBrowser_0.5.0_x64-setup.exe` 与 `ScBrowser_0.5.0_x64-unpack.zip`）。发布闸门：
    构建 -warnaserror 0 警告（重定向构建，日常实例在跑）+ **197/197** 单测 + 文档体检 0 警告 +
    隐私自查（release exe 元数据 CompanyName=ScBrowser、无个人信息；Debug 含 PDB 的 zip 未上传）。
    实跑验证过的项：标签拖拽贴手性/让位动画/边缘自动滚动/5 连交接、dev.bat 四条路径、
    书签页弹窗开关、自定义工具栏菜单点击与勾选态（修复后）。
  · 2026-10-27：`sh manager.sh verify` ✅（构建 + **188/188** 单测 + 文档体检）。
    `sh manager.sh package` ✅（0.4.2 dev/release 产物）；GitHub Release `v0.4.2` 两个资产已上传。
    实跑通过（隔离实例，全程不松手）：**拖出即分窗**（多标签 / 单标签源）→ **拖进另一窗口标签条即合并** →
    再拖出继续分窗，连续 5 次交接的窗口数都正确；单标签源合并后原窗口自动关闭；
    横向拖出窗口也能分窗；分出窗口跟随鼠标，指针始终停在标签抓取点；「关于」浅色 / 深色按钮样式已截图确认。
  · 2026-10-26：`sh manager.sh test` ✅（**183/183**）。
  · 实跑通过：标签拖拽重排；书签页文件夹树（新建 / 移动到 → 子项归入文件夹）；历史页按日期分组与范围筛选；
    **扩展安装全链路**：用 `--install-extension` 安装真实商店包 uBlock Origin Lite ——
    CRX 验签通过、WebView2 报出的 ID 与商店 ID 一致（`ddkjiahejlhfcafbddmgiahcphecmpfh`）、
    manifest 已写入 `key`、受管目录为 `<id>/<版本>`；
    工具栏显示扩展图标，点图标后弹窗加载 `chrome-extension://<id>/popup.html` **成功**（日志 NavigationCompleted=true）。
  · **未实跑（靠编译与单测兜底，清单见「下一步」）**：密码保存/自动填充、标签组 UI、uBlock 弹窗实际 UI、
    后台休眠、内核重建、打印/另存为、证书提示、默认浏览器注册。
- **已发布**：`v0.5.0`（2026-10-29，https://github.com/zouv/sc-browser/releases/tag/v0.5.0 ）——
  上传 `ScBrowser_0.5.0_x64-setup.exe` 与 `ScBrowser_0.5.0_x64-unpack.zip`。
- **上一版**：`v0.4.2`（2026-10-27）；更早 `v0.4.1` / `v0.4.0` / `v0.3.0`。

---

## 当前任务（In Progress）

- **无进行中任务**。

---

## 待归档暂存区（仅本轮未归档的完成项）

- （无。）

## 进行中（半成品 / 未提交）

- （无。）

## 阻塞 / 待决策（Blockers）

- （无。）

---

## 下一步（Next Steps）

1. 用户在场时补做未实跑项，优先顺序：
   - 书签导入导出（Chrome 导出的真实 HTML 文件）、拼图菜单行内图钉与固定扩展右键菜单；
   - 扩展弹窗的实际 UI（uBlock 点开后是否显示统计/开关）、`chrome.tabs` 兼容层是否被用上；
   - 标签组：右键建组 → 色点出现 → 退出重开看会话是否恢复分组；
   - 密码保存 / 自动填充开关与「清除已保存密码」；
   - 此前遗留：后台休眠、内核重建、打印/另存为、证书提示、默认浏览器注册（写 HKCU，需用户同意）。
3. 仍未做的规划（见 `ROADMAP`）：**P2-1**（CompositionController，重写承载层，需实机视觉验证）、
   **垂直标签栏**、扩展自动更新、`chrome.action.onClicked`。

## 如何运行 / 测试（Quick Verify）

- 构建：`sh manager.sh build`（无 bash 环境用 `build.bat`）
- 运行：`sh manager.sh run`（或 `sh manager.sh start` 跑已构建的 exe）；`run.bat` 为等价物
- 测试：`sh manager.sh test`（`test.bat`）
- 提交前闸门：`sh manager.sh verify`（构建 -warnaserror + 单测 + 文档体检）；`verify.bat` 等价
  （文档体检需 bash，无 `sh`/`bash` 时只告警跳过）
- 一键 CI：`ci.bat`（clean → verify → package 到 `dist\`）；打包另可 `sh manager.sh package`
- 打包产物：`dist/dev/`（Debug zip）与 `dist/release/`（自包含 unpack zip + setup exe）；`clean` 保留 `dist`。
- 看日志：`sh manager.sh logs`
- 端到端自查技巧：设 `SCBROWSER_DATA_DIR=<临时目录>` 起隔离实例（不打扰日常实例），往该目录的 `session.json` 写
  `{"Urls":["browser://extensions"],"ActiveIndex":0,"SavedAt":"…"}` 再启动可直接落在指定内部页面；`ScBrowser.exe --install-extension <目录|.crx>` 可验证扩展安装。

---

## 关键决策记录（Key Decisions）

> 记录「为什么这么做」，避免后续 AI 推翻或重复踩坑。完整版见 `ARCHITECTURE.md` §7。

- **外壳用 WPF 原生控件，不用 WebView**：系统集成（焦点/输入法/无障碍/DPI）更省心；
  Chrome / Edge 的外壳同样是原生自绘（Views + Skia），此为业界主流做法。
- **每个标签页一个 WebView2，不做多标签复用**：换来切换零延迟与页面状态保留（见 ARCHITECTURE §7）。
- **只用一个 `CoreWebView2Environment`**：所有标签共享 Cookie/缓存/扩展安装态（见 ARCHITECTURE §0/§1）。
- **快捷键放在窗口级 `PreviewKeyDown`**：经反射核实，WPF 版 WebView2 会把
  `CoreWebView2Controller.AcceleratorKeyPressed` 转发成 WPF 的 `PreviewKeyDown` 重新抛出，
  因此无需反射取 controller。**前提**是关掉 `AreBrowserAcceleratorKeysEnabled`。
- **存储选 SQLite 而非 JSON/IndexedDB**：按时间倒序分页、关键字搜索、热门站点统计，用 SQL 是几行的事。
- **内部页面统一走 WebUI（HTML/JS）**：与 Chrome 的 `chrome://` 页面同构，
  复用「C#+JS」栈里的 JS 半边；代价是要额外维护一套消息协议。
- **无痕不做第二套环境**：复用唯一的 `CoreWebView2Environment`，只换 InPrivate 控制器选项（见 ARCHITECTURE §7）。
- **per-window 对象不进 DI 容器**：`TabManager` / `MainViewModel` / `MainWindow` 由
  `BrowserWindowManager` 手工 new——容器表达不了「按会话类型构造」，且单例服务一旦持有
  某个窗口实例，多窗口下必错。
- **窗口缩放靠「内缩一圈」而不是加大命中带**（airspace 决定的，见 ARCHITECTURE §7）。
- **下拉/菜单/浮层一律走独立顶层窗口**：airspace 决定的，不是风格选择（见 ARCHITECTURE §7）。
- **内部页面走 `browser://` 自定义 scheme + 消息桥按来源鉴权**：`file://` 无法区分来源、
  https 虚拟主机会被扩展 `<all_urls>` 注入（详见 ARCHITECTURE §7）。
- **单实例按数据根目录区分**：`SCBROWSER_DATA_DIR` 起的测试实例可与日常实例并存，方便实跑验证。
- **标签状态与引擎分离**：`BrowserTab` 构造不再建 WebView2，`View`/`Core` 可能为 `null`；
  会话恢复只立即加载激活标签（详见 ARCHITECTURE §7「惰性恢复」）。
- **扩展的信任模型**：没有商店审核，安装即信任其代码——CRX 必须验签、安装前列权限、
  受管目录按 `<id>/<版本>`、公钥写回 manifest 的 `key` 才能保住 Chrome 同款扩展 ID。
  平台不提供 `chrome.tabs`，只在**我们自己承载的弹窗**里注入兼容层，不要夸大兼容性。
- **页内查找优先原生、旧运行时走 JS 兜底**：`CoreWebView2.Find` 需要 `ICoreWebView2_28`，
  本机 WebView2 131 没有该接口。**不要为「只留一条实现」删掉兜底**（详见 ARCHITECTURE §7）。
- **标签拖拽对齐 Chromium 的「几何层/模型层分离」**：被拖标签留在模型里、渲染偏移只从
  「模型移动 + `UpdateLayout()` 落地后的槽位几何 + 指针」算（不落地就读是飘闪根因）；
  让位/落回用补偿衰减动画，滞后 16px、垂直容差 15、边缘自动滚动判据均对齐 Chromium 常量。
  详见 `ARCHITECTURE.md` §7。
- **打包统一到 `dist/`，脚本名为 `package`**：产物与安装器细节见 `ARCHITECTURE.md` §6 与 `docs/02-使用说明.md`；
  安装器只删程序文件，**绝不删** `%LOCALAPPDATA%\sc-browser` 用户数据。

---

## 待解问题 / 给用户的提问（Open Questions）

- **本轮对齐 Chrome 后的遗留取舍**（有意为之，见代码注释）：书签栏溢出用横向滚动（Chrome 是 » 菜单）；
  书签拖拽是「移入目标文件夹」近似；下载列表整表刷新、无完成气泡；无 favicon 存储（首字母圆片替代，
  不外发域名）；清除数据时间范围只作用于历史（内核 API 限制）；导入导出入口在书签管理器页。
- **未实跑的功能**（清单见「最后验证状态」/「下一步」）。
  其中「设为默认浏览器」会写当前用户注册表，实跑前应先征得用户同意。
- **站点信息里的安全状态可能显示「未知」**：`Security.securityStateChanged` 对本地 http 页面触发不稳定；
  真实 https 站点正常。若要更准，可在导航完成时主动问一次 CDP。
- **无痕的扩展支持未验证**：WebView2 的 InPrivate profile 下扩展行为未实测（普通窗口的扩展链路已验证）。
- **行尾属性后加的坑（已修，供备忘）**：行尾规则只管「以后怎么检出」，不会自动改写已存在的文件——
  要真修必须 `rm` 后 `git checkout -- <file>` 重新落盘。
