# 代码链路图谱 · sc-browser

> **本文件的目的**：给 AI（及人类）一份「读这一份就能定位代码」的导航地图，
> 避免每次改动/排查都全量扫描源码，节省上下文与 token。
>
> **维护铁律**：**改了代码结构（新增函数 / 移动逻辑 / 改数据流 / 改接口），必须同步更新本文件。**
>
> **体量铁律**：本文件超过 ~400 行即拆分——保留总览 + §0.5 路由 + §1 文件职责总表，其余拆到 `arch/<module>.md`。

---

## 0. 一句话架构

WPF 原生外壳（标签条/工具栏/地址栏）＋ 每标签页一个 WebView2 承载网页与内部页面；
C# 宿主用 SQLite 存历史/书签/下载，用消息桥与 WebUI 双向通信，扩展按 profile 管理。

```
                    ┌─────────────────── WPF 外壳（NativeMethods / Themes）───────────────────┐
                    │  MainWindow.xaml  ←→  MainViewModel                                    │
                    │        │                  │                                            │
                    │  BrowserContentHost ──────┘  （IBrowserTabHost：挂载/切换 WebView2）    │
                    └────────┬───────────────────────────────────────────────────────────────┘
                             │ 持有
                    ┌────────▼─────────┐
                    │   TabManager     │  标签集合中枢（创建/关闭/切换/历史记录）
                    └───┬─────────┬────┘
                        │         │
              ┌─────────▼──┐  ┌───▼──────────────────────┐
              │ BrowserTab │  │ WebMessageRouter          │ 内部页面 ↔ 宿主 消息路由
              │ (WebView2) │  └───┬──────────┬────────────┘
              └─────┬──────┘      │          │
                    │             │          │
   ┌────────────────▼──┐  ┌───────▼──┐  ┌────▼─────────┐  ┌──────────────┐
   │ BrowserEnvironment│  │ Storage  │  │ Services     │  │ Extension    │
   │ Provider（单环境） │  │ SQLite   │  │ 设置/主题/会话 │  │ Service+CRX  │
   └───────────────────┘  └──────────┘  └──────────────┘  └──────────────┘
                    │
        ┌───────────▼─────────────┐
        │ Assets/ui/**（WebUI）    │  newtab / history / bookmarks / settings / extensions / downloads
        │ ←→ window.chrome.webview │  通过 common/browser-api.js 与宿主通信
        └─────────────────────────┘
```

技术栈 / 关键约束：**.NET 10 (net10.0-windows) + WPF + WebView2 1.0.4258.31 + SQLite**；
单一 WebView2 环境；外壳 UI 不用 WebView 承载（区别于原 C++ 示例）。

---

## 0.5 任务作用域路由（先定范围，避免污染上下文）

| 任务类型 | 该读（仅限） | 可忽略 | 入口小节 |
|---|---|---|---|
| 改外壳 UI（标签条/工具栏/地址栏/窗口按钮） | `Views/MainWindow.xaml(.cs)`、`Views/TabStripMetrics.cs`、`Themes/*`、`Interop/NativeMethods.cs`、`Common/Converters.cs` | Browser/、Storage/、Assets/ | §2.1、§7 |
| 改标签页行为（导航/事件/安全图标/favicon/重开/拖拽排序/新建位置） | `Browser/BrowserTab.cs`、`Browser/TabManager.cs`、`Browser/TabInsertion.cs`、`Browser/ClosedTabHistory.cs` | Views/（除非动标签条交互）、Assets/、Storage/ | §2.2 |
| 改链接悬停地址提示 | `Views/MainWindow.Status.cs`、`Views/LinkStatusWindow.xaml(.cs)`、`Interop/NativeMethods.cs`（`MakeClickThrough`） | Browser/、Storage/、Assets/ | §2.1、§7 |
| 多窗口 / 无痕 / 窗口级装配 | `Services/BrowserWindowManager.cs`、`Browser/BrowserSession.cs`、`Views/MainWindow.xaml.cs`（窗口与几何部分） | Storage/、Assets/ | arch/flows.md §3.5 |
| 改地址栏（解析/下拉建议） | `Browser/AddressBarResolver.cs`、`Services/OmniboxSuggestions.cs`、`Views/AddressBarPopup.xaml(.cs)`、`ViewModels/MainViewModel.cs`（Suggestions 部分） | Storage/、Assets/ | arch/flows.md §3.6 |
| 改全屏 / 站点信息 / 证书提示 | `Views/MainWindow.xaml(.cs)`（`ToggleFullScreen` / `SecurityButton_Click` / `AskAboutCertificateError`）、`Views/SiteInfoPopup.xaml(.cs)`、`Interop/NativeMethods.cs`（`FillMonitor`）、`Browser/BrowserTab.cs`（`FullScreenChanged` / `CertificateErrorDetected`） | Storage/、Assets/ | §2.1、§7 |
| 改页内查找（Ctrl+F） | `Views/FindBarWindow.xaml(.cs)`、`Views/MainWindow.Find.cs`（会话/防抖/按标签记忆）、`Views/MainWindow.Shortcuts.cs`（快捷键）、`Browser/BrowserTab.cs`（Find 封装与 JS 兜底） | Storage/、Assets/ | §2.1、§2.2、§7、arch/flows.md §3.8 |
| 加/改内部页面（WebUI） | `Assets/ui/<page>/*`、`Browser/WebMessageRouter.cs`、`Browser/InternalPageRegistry.cs`（含消息白名单） | Views/、Storage/（除非要新数据） | §2.3、§4、§7 |
| 加/改数据存储（历史/书签/下载） | `Storage/*`（含 `BrowserDatabase` 的 `user_version` 迁移） | Views/、Assets/ | §2.4、§4 |
| 改扩展能力 | `Browser/ExtensionService.cs`、`Browser/CrxPackage.cs`、`Browser/ExtensionManifest.cs`、`Browser/ExtensionStoreClient.cs`、`Browser/ExtensionApiBridge.cs`、`Views/ExtensionPopupWindow.xaml(.cs)`、`Assets/ui/extensions/*` | Views/（除弹窗）、Storage/ | §2.5、arch/flows.md §3.2、docs/03 |
| 改主题/设置/会话（含「深色影响网页」） | `Services/SettingsService.cs`、`Services/ThemeService.cs`、`Services/SessionStore.cs`、`Browser/WebTheme.cs`、`Themes/*` | Storage/、Views/ | §2.6、arch/flows.md §3.4 |
| 启动流程 / 单实例 / 命令行参数 | `App.xaml.cs`、`Services/SingleInstance.cs`、`Services/StartupCommandRunner.cs`、`Services/BrowserWindowManager.cs`（`HandleForwardedArgsAsync`） | Storage/、Assets/ | §2.6 |
| 改启停/构建/发布（运维） | `manager.sh`、`*.bat`、`installer/*`、`.gitattributes`、`ScBrowser.csproj`、`docs/02-使用说明.md` | 所有业务代码 | §6 |

> 跨多个区域的任务才需要跨列读；仍遵循「**先定位函数名，再只读那几个函数**」。

---

## 1. 文件职责速查（先看这张表）

### 宿主（C#）

| 文件 | 职责 | 何时改它 |
|---|---|---|
| `src/ScBrowser/App.xaml(.cs)` | 应用入口：DI 装配、Serilog 日志、全局异常兜底、启动期初始化顺序；`DisplayVersion` 是对外版本号的唯一来源（关于页 / 日志） | 增删服务注册 / 改启动流程 / 改版本展示 |
| `src/ScBrowser/app.manifest` | PerMonitorV2 DPI 感知、Win10/11 兼容性声明 | 改 DPI 策略 |
| `src/ScBrowser/Infrastructure/AppPaths.cs` | **路径单一源**（用户数据/扩展/日志/DB/会话/WebUI）；`SCBROWSER_DATA_DIR` 开发覆盖 | 新增落盘位置 |
| `src/ScBrowser/Infrastructure/PathSafety.cs` | 防目录穿越的路径判定（**补分隔符**比较），CRX 解包与内部页面资源共用 | 新增「把外部路径落到本地」的地方 |
| `src/ScBrowser/Infrastructure/AtomicFile.cs` | 原子写文件（临时文件 + 替换），settings/session 用 | 新增需防半截写坏的配置文件 |
| `src/ScBrowser/Infrastructure/KnownFolders.cs` | 系统已知文件夹（目前只有「下载」）；不要自己拼 `%USERPROFILE%\Downloads` | 需要系统标准目录时 |
| `src/ScBrowser/Interop/NativeMethods.cs` | Win32 互操作：深色标题栏、圆角窗口、低层鼠标钩子 | 新增窗口级原生外观 / 输入接管 |
| `src/ScBrowser/Themes/Theme.{Light,Dark}.xaml` | 颜色令牌（键名必须成对一致） | 调色 / 加令牌 |
| `src/ScBrowser/Themes/Controls.xaml` | 控件样式（工具栏按钮/标签/窗口按钮/地址栏/对话框按钮） | 改控件外观 |
| `src/ScBrowser/Common/Converters.cs` | 值转换器（安全图标、可空、显隐） | 加绑定转换 |
| `src/ScBrowser/Assets/sc-browser.ico` + `docs/icons/` | 应用图标（exe 内嵌 + 窗口 `Icon`）；`docs/icons/` 是设计源（SVG 为源，附各尺寸导出） | 换图标 |
| `src/ScBrowser/Browser/BrowserEnvironmentProvider.cs` | 创建并缓存**唯一**的 WebView2 环境（含扩展总开关、Edge JSON 查看器开关） | 改环境级选项（代理/扩展开关/浏览器参数） |
| `src/ScBrowser/Browser/BrowserTab.cs` | **单标签页**：全部可观察状态 + **按需创建**的 WebView2 引擎 + 事件接线 + 页内查找（原生/JS 兜底） | 改单页行为（导航/事件/图标/查找）；引擎生命周期见 §7 |
| `src/ScBrowser/Browser/TabManager.cs` | 标签集合中枢：创建（含惰性）/关闭/切换/相邻选择/历史记录 + **按需初始化引擎** + 网页颜色方案 + Ctrl+单击的右侧插入 | 改标签生命周期语义 |
| `src/ScBrowser/Browser/TabInsertion.cs` | 「Ctrl+单击新标签插在哪」的纯函数：发起标签右侧、跳过由它连续打开的标签（`Opener` 链） | 改新标签排列规则 |
| `src/ScBrowser/Browser/WebTheme.cs` | 应用主题 → `CoreWebView2Profile.PreferredColorScheme` 映射 | 改「深色如何影响网页」 |
| `src/ScBrowser/Browser/BrowserSession.cs` | 会话类型（普通/无痕）→ WebView2 控制器选项 | 加新的隔离会话 |
| `src/ScBrowser/Browser/ClosedTabHistory.cs` | 「最近关闭的标签」栈（Ctrl+Shift+T，容量 25） | 改重开策略 |
| `src/ScBrowser/Browser/IBrowserTabHost.cs` | 内容区宿主抽象（视图层实现，防 TabManager 依赖控件） | 改挂载约定 |
| `src/ScBrowser/Browser/InternalPageRegistry.cs` | `browser://<页面>/` 自定义 scheme：地址解析、资源路径、**每页消息白名单** | 新增内部页面 / 调整页面权限 |
| `src/ScBrowser/Browser/AddressBarResolver.cs` | 地址栏输入 → 地址/搜索词判定 | 改地址解析规则 |
| `src/ScBrowser/Browser/SecurityState.cs` | 安全状态枚举（锁形图标语义） | 加安全状态 |
| `src/ScBrowser/Browser/WebMessageRouter.cs` | **内部页面 ↔ 宿主消息路由**（协议唯一实现处，按来源鉴权） | 新增 WebUI 能调用的能力 |
| `src/ScBrowser/Browser/ExtensionService.cs` | 扩展列出/安装/启停/卸载；受管目录 `<id>/<版本>`、安装索引、元数据与图标 | 改扩展能力 |
| `src/ScBrowser/Browser/ExtensionManifest.cs` | 解析 `manifest.json`（多语言名、权限、图标、弹窗、选项页、update_url）；写回 `key` | manifest 相关 |
| `src/ScBrowser/Browser/ExtensionStoreClient.cs` | 商店链接/ID → 下载 `.crx`（Chrome / Edge） | 改商店来源 |
| `src/ScBrowser/Browser/ExtensionApiBridge.cs` | 扩展弹窗里的 `chrome.tabs` / `windows` 兼容层脚本（PoC） | 补扩展 API |
| `src/ScBrowser/Browser/CrxPackage.cs` | CRX 解包（v2/v3 头解析 + ZIP 提取 + 目录穿越防护） | 改解包逻辑 |
| `src/ScBrowser/Storage/BrowserDatabase.cs` | SQLite 建表 / 连接工厂（WAL） | 改 schema |
| `src/ScBrowser/Storage/Entries.cs` | 数据记录定义（HistoryEntry/BookmarkEntry/DownloadEntry + 状态枚举） | 加字段 |
| `src/ScBrowser/Storage/{History,Bookmark,Download}Repository.cs` | 三个仓储的读写 | 改查询/写入 |
| `src/ScBrowser/Storage/BookmarkHtml.cs` | Netscape 书签 HTML 的解析/生成（导入导出，纯函数 + 单测） | 改导入导出格式 |
| `src/ScBrowser/Services/SettingsService.cs` | 设置读写（JSON）＋ 归一化 ＋ 变更事件 | 加设置项 |
| `src/ScBrowser/Services/ThemeService.cs` | 运行时换肤（替换颜色字典索引 0）+ `Applied` 事件（网页跟着换） | 改换肤机制 |
| `src/ScBrowser/Services/BrowserWindowManager.cs` | **窗口总管**：按窗口组装 TabManager/MainViewModel/MainWindow，跟踪并聚合跨窗口事件 | 加窗口级能力、会话保存 |
| `src/ScBrowser/Services/WindowPlacement.cs` | 窗口几何的合法性判断与钳制（纯函数） | 改窗口记忆行为 |
| `src/ScBrowser/Services/OmniboxSuggestions.cs` | 地址栏下拉候选构建（纯函数：搜索行 / 直接打开 / 历史匹配排序） | 改下拉内容 |
| `src/ScBrowser/Services/SessionStore.cs` | 会话保存/恢复（JSON） | 改会话内容 |
| `src/ScBrowser/Services/DownloadManager.cs` | 下载接管、进度节流落盘、列表 | 改下载行为 |
| `src/ScBrowser/Services/StartupCommandRunner.cs` | 命令行参数：网址/本地文件（`ExtractUrls`）、`--install-extension` | 加启动参数 |
| `src/ScBrowser/Services/SingleInstance.cs` | 单实例锁 + 命名管道转发二次启动参数（按数据根目录区分） | 改二次启动行为 |
| `src/ScBrowser/Services/DefaultBrowserService.cs` | 注册 ProgID / URL 关联（HKCU，按用户）并打开系统「默认应用」 | 改默认浏览器行为 |
| `src/ScBrowser/ViewModels/MainViewModel.cs` | 外壳状态投影与命令（不碰 WebView2） | 加工具栏/菜单命令 |
| `src/ScBrowser/Views/MainWindow.xaml(.cs)` | 外壳布局（含自绘窗口边框 + 浮动查找条）+ 窗口外观 + **键盘快捷键** | 改外壳结构/快捷键/查找条 |
| `src/ScBrowser/Views/Controls/BrowserContentHost.cs` | 内容区宿主实现（WebView2 挂载与显示切换） | 改内容区挂载方式 |
| `src/ScBrowser/Views/AddressBarPopup.xaml(.cs)` | 地址栏下拉浮层（独立顶层窗口，规避 airspace） | 改下拉外观/交互 |
| `src/ScBrowser/Views/TabReorder.cs` | 标签拖拽的纯函数换算：落点（指针落在哪个槽位）、标签条命中判定（垂直容差 15 = Chrome 磁性）、让位动画补偿量 `SlideCompensation`，纯函数 + 单测 | 改拖拽落点 / 分窗判定规则 |
| `src/ScBrowser/Views/TabStripMetrics.cs` | 标签宽度换算（纯函数 + 单测）：等宽、按可用宽度（已扣除「+」新建按钮）压缩、最小 28 / 最大 220，保证总宽不超视口 | 改标签宽度/溢出策略 |
| `src/ScBrowser/Views/TabStripSlideAnimator.cs` | Chrome 式让位/落回动画的视图层：每容器补偿 `TranslateTransform` + 指数衰减（τ≈66ms），换位前快照、换位后结算、松手落回、窗口关闭清理 | 改让位动画手感 |
| `src/ScBrowser/Assets/ui/settings/*` | **WebUI 设置页**：Chrome 式「左侧分类导航 + 右侧内容」，每个分类一个独立页面（hash 路由：`browser://settings#downloads` 等，无 hash 显示分类卡片首页）；「关于 ScBrowser」条目承载原关于弹窗内容 | 改设置页 |
| `src/ScBrowser/Views/BookmarkMenuWindow.xaml(.cs)` | 地址栏星标的弹出菜单（名称/网址/文件夹/完成/移除，Chrome 同款，独立顶层窗口；另可作「新建文件夹」模式） | 改星标菜单 |
| `src/ScBrowser/Views/ClearBrowsingDataDialog.xaml(.cs)` | 清除浏览数据对话框（Ctrl+Shift+Delete；范围仅作用于历史，WebView 数据全清） | 改清除浏览数据 |
| `src/ScBrowser/Views/MainWindow.Bookmarks.cs` | 主窗口分册：书签栏交互（点击/条目右键/空白区右键菜单/文件夹下拉/编辑删除）与星标菜单定位 | 改书签栏行为 |
| `src/ScBrowser/Views/MainWindow.Toolbar.cs` | 主窗口分册：自定义工具栏（拼图菜单行内图钉 / 工具栏右键自定义菜单 / 扩展按钮右键菜单） | 改工具栏扩展交互 |
| `src/ScBrowser/Views/SiteInfoPopup.xaml(.cs)` | 站点信息浮层（来源 / 安全状态 / 站点权限 / 清除站点数据） | 改站点信息与权限管理 |
| `src/ScBrowser/Views/FindBarWindow.xaml(.cs)` | **浮动查找条**（独立顶层窗口，输入框/计数/上一下一/关闭）；搜索逻辑在 `MainWindow.Find.cs` | 改查找条外观与交互 |
| `src/ScBrowser/Views/LinkStatusWindow.xaml(.cs)` | **链接悬停地址气泡**：独立顶层窗口，盖在内容区左下角；`MakeClickThrough` 保证点击穿透/不激活；状态逻辑在 `MainWindow.Status.cs` | 改悬停提示外观 |
| `src/ScBrowser/Views/MainWindow.Status.cs` | 主窗口分册：订阅激活标签的 `StatusText`，显示/定位/隐藏链接悬停气泡 | 改悬停提示行为 |
| `src/ScBrowser/Views/ExtensionPopupWindow.xaml(.cs)` | 扩展工具栏弹窗（同 profile 的 WebView2 + `chrome.tabs` 兼容层） | 改扩展弹窗 |
| `src/ScBrowser/Views/MainWindow.{Tabs,Shortcuts,Find,Status}.cs` | 主窗口的 partial 分册（标签条交互与宽度 / 快捷键 / 页内查找 / 链接悬停气泡） | 改对应区域 |
| `src/ScBrowser/Assets/ui/**` | **WebUI 内部页面**（HTML/CSS/JS） | 改内部页面 |
| `src/ScBrowser/Assets/ui/common/browser-api.js` | WebUI 侧消息桥（协议客户端） | 改协议 |

### 其他

| 路径 | 职责 |
|---|---|
| `tests/ScBrowser.Tests/` | xUnit 单测（地址栏解析、CRX 解包、内部页面注册表与消息鉴权、命令行/原子写等加固项） |
| `samples/extensions/hello-sc-browser/` | 示例扩展，用于验证扩展加载链路 |
| `manager.sh` | 统一管理脚本（构建/运行/测试/打包/清理/日志）·**命令语义的单一来源** |
| `installer/ScBrowser.iss` | Inno Setup 7 安装脚本：按用户安装、卸载、开始菜单/桌面快捷方式 |
| `installer/ScBrowser.Installer.csproj` | 仅用于通过 NuGet 还原 Inno Setup 编译器（`Tools.InnoSetup`），不参与应用构建 |
| `check-docs.sh` | 文档体系机械体检（技术栈无关） |
| `build.bat` / `run.bat` / `test.bat` / `verify.bat` / `clean.bat` / `package.bat` / `ci.bat` | `manager.sh` 各命令的 Windows 原生等价物（纯 cmd + dotnet，不需要 bash）；`dev.bat` 无对应命令，是「询问式关闭运行实例 → build+test → 启动 Debug 版」的便捷入口 |
| `.gitattributes` | 行尾策略：`*.sh`=LF、`*.bat`=CRLF（否则 Windows 上脚本执行失败） |

---

## 2. 按「我要做什么」反查代码位置（任务 → 入口）

> 这张表已按体量要求拆到 [`arch/lookup.md`](arch/lookup.md)——§0.5 里的「§2.1 / §2.2 …」都指那份分册。
> **先按函数名 grep，再只读那几个函数**；搜不到 / 对不上以代码为准，并顺手订正分册。

### 2.1～2.6 速览（详细条目见分册）

| 小节 | 覆盖 |
|---|---|
| §2.1 | 外壳 UI：工具栏/标签条样式、窗口按钮、拖拽、快捷键、全屏、状态栏、站点信息、证书错误、查找条、菜单 |
| §2.2 | 标签页：创建/惰性加载、重开、排序、无痕、历史、音频/静音、休眠、引擎重建、迁移到新窗口、页内查找 |
| §2.3 | 内部页面：新增页面、消息鉴权、宿主事件推送 |
| §2.4 | 存储：建表、历史、书签、下载记录与控制 |
| §2.5 | 扩展：列表/安装/启停/卸载、CRX 解包 |
| §2.6 | 设置/主题/会话：加设置项、换肤、会话、单实例、下载接管 |

---

## 3. 重点链路详解（已拆分为分册）

每条链路的「怎么连、为什么这么连、哪里容易写错」都在 [`arch/flows.md`](arch/flows.md)：

| 小节 | 主题 | 什么时候看 |
|---|---|---|
| §3.1 | 内部页面消息链路（WebUI ↔ 宿主） | 改协议 / 加页面能力 |
| §3.2 | 扩展加载与运行 | 改扩展相关 |
| §3.3 | 键盘快捷键在网页聚焦时仍然生效 | 改快捷键 |
| §3.4 | 主题贯穿：应用主题 → 外壳 → 网页内容 | 改主题 |
| arch/flows.md §3.5 | 多窗口与无痕（一份 vs 共享） | 动窗口/标签的装配 |
| arch/flows.md §3.6 | 地址栏下拉（omnibox 建议） | 改地址栏 |
| arch/flows.md §3.7 | 标签惰性恢复（状态与引擎分离） | 改标签创建 / 会话恢复 |
| arch/flows.md §3.8 | 输入接管与页内查找（滚轮 / JSON / 虚拟滚动） | 改标签条滚轮 / 页内查找 |

---

## 4. 数据流 / 接口契约（WebUI ↔ 宿主）

协议：页面 → 宿主 `{ type, requestId?, payload }`；宿主 → 页面 `{ type:"response", requestId, ok, result|error }`
或事件推送 `{ type, payload }`。**C# 侧序列化统一 camelCase**。

| 调用方 | 消息 type | 关键字段 | 返回 |
|---|---|---|---|
| newtab | `navigation.navigate` | `input`（原始输入，由宿主解析） | — |
| 各页 | `navigation.openInNewTab` | `url`, `active` | — |
| history | `history.query` | `search`, `limit`, `offset` | `HistoryEntry[]` |
| history | `history.remove` / `history.removeByUrl` / `history.removeMany` / `history.clear` | `id` / `url` / `ids` | — |
| history | `history.visits` | `search`, `from`, `to`（ISO）, `limit` | `VisitEntry[]`（按时间倒序） |
| newtab | `history.topSites` | `limit` | `HistoryEntry[]` |
| bookmarks | `bookmarks.query` / `bookmarks.add` / `bookmarks.remove` / `bookmarks.toggle` / `bookmarks.exists` | `url`, `title`, `id`, `parentId` | 视消息而定 |
| bookmarks | `bookmarks.addFolder` / `bookmarks.rename` / `bookmarks.move` / `bookmarks.removeById` | `title`, `id`, `parentId` | 新 id / 是否成功 |
| bookmarks | `bookmarks.edit` | `id`, `title`, `url` | 是否成功 |
| bookmarks | `bookmarks.importFromFile` / `bookmarks.exportToFile` | —（弹系统文件对话框） | `{imported}` / `{exported}`；取消为 null |
| bookmarks | `bookmarks.openAll` | `id`（文件夹，0 = 全部） | `{opened}` |
| downloads | `downloads.query` / `downloads.clear` / `downloads.remove` | `id` | `DownloadEntry[]` |
| downloads | `downloads.openFile` / `downloads.revealInFolder` | `id`（**不收路径**，宿主查库） | — |
| downloads | `downloads.pause` / `downloads.resume` / `downloads.cancel` / `downloads.retry` | `id` | — |
| 扩展 | `extensions.list` | — | `ExtensionInfo[]`（含版本、描述、权限、图标 data URL、弹窗/选项路径、MV 版本、`isPinned`/`pinOrder`） |
| 扩展 | `extensions.installFromFolder` / `extensions.installFromCrx` | —（弹系统对话框） | `ExtensionInfo \| null`（用户在权限确认里取消时为空） |
| 扩展 | `extensions.installFromStore` | `input`（商店链接或 32 位 ID） | `ExtensionInfo \| null` |
| extensions | `extensions.setPinned` | `id`, `pinned` | — |
| extensions | `extensions.remove` / `extensions.setEnabled` / `extensions.openManagedFolder` | `id`, `enabled` | — |
| settings | `settings.get` | — | `AppSettings` |
| settings | `settings.update` / `settings.setTheme` | 偏好字段（合并，见 `CopyPreferencesFrom`） / `theme` | — |
| settings | `app.info` | — | 版本与各路径 |
| settings | `app.clearBrowsingData` | `cache`, `cookies`, `storage` | — |

**宿主 → 页面事件**：`downloads.changed`、`extensions.changed`、`settings.changed`（只推给停在内部页面上的标签）、
`bookmarks.changed`（书签增删改/导入导出后推给书签页刷新；书签栏与星标由 `BookmarkRepository.Changed` 驱动）。

**鉴权**：每条消息按发送文档地址（`args.Source`）判定，只有 `InternalPageRegistry.Pages` 登记的页面、且类型命中该页白名单前缀才处理；普通网页 / `file://` 的消息直接丢弃且不回执。

**数据铁律**：
- 内部页面渲染不可信文本（标题/地址/扩展名）一律用 `textContent`，**禁止 `innerHTML`**（防 XSS）。
- 时间统一以 ISO 8601 UTC 字符串入库，展示层本地化。
- 用户数据仅落 `%LOCALAPPDATA%\sc-browser`，不写仓库内。

---

## 6. 运维 / 脚本（启停 / 构建 / 发布）—— 不碰业务即可独立维护

> 做运维任务只读本节 + `manager.sh` + `src/ScBrowser/ScBrowser.csproj`，不用打开业务代码。
> **与 docs/02 分工**：本节只留代码/脚本位置指针；具体操作步骤与坑以 [`docs/02-使用说明.md`](docs/02-使用说明.md) 为准。

- **统一入口**：`manager.sh`（`build` / `run` / `start` / `stop` / `test` / `verify` / `package` / `clean` / `data` / `logs` / `reset`）。
  **`.bat` 是等价物不是第二份真源**：`build.bat` / `run.bat` / `test.bat` / `verify.bat` / `clean.bat` / `package.bat` / `ci.bat`
  用纯 cmd + dotnet 重写同一批命令（不依赖 bash）；**改命令语义时两处必须同步**。
  另有 Windows 专属 `dev.bat`（无 manager.sh 对应命令）：检测到 ScBrowser 在运行时先列进程并**询问**
  （仅输入 Y 才关闭：先 WM_CLOSE 正常关闭、12 秒超时强杀；其它输入一律取消），再 build + test + 启动 Debug 版。
  `dev.bat` 的外部命令一律 System32 全路径（git-bash 派生的 cmd 里 PATH 可能先命中 GNU 同名工具）；
  实例检测靠解析 tasklist 输出（`tasklist /FI` 退出码恒为 0，不能判）；等待用 ping 延时（timeout 在 stdin 重定向时报错）。
  `verify.bat` / `ci.bat` 的文档体检需 bash，检测到 `sh`（退而求其次 `bash`）才跑，否则只告警不失败。
- **打包产物（`dist/`，已 gitignore）**：`package` 固定产出——
  `dist/dev/ScBrowser_<版本>_x64-unpack-debug.zip`（Debug 框架依赖发布 + `ScBrowser.pdb`
  + `logs/`（dev/release 发布日志、安装程序编译日志）+ `build-info.txt`，zip 内带单一根目录），
  `dist/release/unpack/`（自包含、可直接运行的目录），
  `dist/release/ScBrowser_<版本>_x64-unpack.zip`（打包上述目录的**内容**，zip 根下直接是
  `ScBrowser.exe`，不再套一层版本目录），以及
  `dist/release/ScBrowser_<版本>_x64-setup.exe`。暂存目录 `.runtime/staging`
  只在成功后删除。zip 由 Windows 自带的 `tar.exe` 生成（不用 PowerShell ZipFile：
  后者会写出反斜杠条目名）。
  安装程序由 Inno Setup 7 生成，工具通过 NuGet `Tools.InnoSetup` 还原（首次需联网，之后走缓存），
  **不要求系统预装 Inno Setup**；安装脚本在 `installer/ScBrowser.iss`，按用户安装到
  `%LOCALAPPDATA%\Programs\ScBrowser`（无 UAC），安装 `dist/release/unpack/`
  的全部文件，不触碰用户浏览数据。`clean` 只清 bin/obj 与旧产物，保留 `dist/` 成品包。
- **行尾策略**：`.gitattributes` 钉住 `*.sh`=LF、`*.bat`=CRLF。改脚本时优先按属性办事，
  别让 Windows 的 `core.autocrlf` 把 `.sh` 检成 CRLF（bash 会报 `No such file or directory` / 变量尾带 `\r` 而假失败）。
- **路径单一源**：`Infrastructure/AppPaths.cs`；`manager.sh` 中的 `APPDATA_DIR` 必须与其保持一致
  （两边都认 `SCBROWSER_DATA_DIR` 开发覆盖，用法见 `docs/02-使用说明.md` §2.4.2）。
- **提交前闸门**：`sh manager.sh verify` = 构建(-warnaserror) + 单测 + `check-docs.sh`。

---

## 7. 易踩的坑 / 设计取舍

详见 [`arch/pitfalls.md`](arch/pitfalls.md)（WebView2、SQLite、下载 / 导航、单实例、打包与 UI 的长期坑）。
