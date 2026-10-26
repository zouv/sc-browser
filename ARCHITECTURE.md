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
| 改外壳 UI（标签条/工具栏/地址栏/窗口按钮） | `Views/MainWindow.xaml(.cs)`、`Themes/*`、`Interop/NativeMethods.cs`、`Common/Converters.cs` | Browser/、Storage/、Assets/ | §2.1、§7 |
| 改标签页行为（导航/事件/安全图标/favicon/重开/拖拽排序） | `Browser/BrowserTab.cs`、`Browser/TabManager.cs`、`Browser/ClosedTabHistory.cs` | Views/（除非动标签条交互）、Assets/、Storage/ | §2.2 |
| 多窗口 / 无痕 / 窗口级装配 | `Services/BrowserWindowManager.cs`、`Browser/BrowserSession.cs`、`Views/MainWindow.xaml.cs`（窗口与几何部分） | Storage/、Assets/ | arch/flows.md §3.5 |
| 改地址栏（解析/下拉建议） | `Browser/AddressBarResolver.cs`、`Services/OmniboxSuggestions.cs`、`Views/AddressBarPopup.xaml(.cs)`、`ViewModels/MainViewModel.cs`（Suggestions 部分） | Storage/、Assets/ | arch/flows.md §3.6 |
| 改全屏 / 站点信息 / 证书提示 | `Views/MainWindow.xaml(.cs)`（`ToggleFullScreen` / `SecurityButton_Click` / `AskAboutCertificateError`）、`Views/SiteInfoPopup.xaml(.cs)`、`Interop/NativeMethods.cs`（`FillMonitor`）、`Browser/BrowserTab.cs`（`FullScreenChanged` / `CertificateErrorDetected`） | Storage/、Assets/ | §2.1、§7 |
| 改页内查找（Ctrl+F） | `Views/MainWindow.xaml(.cs)`（查找条/快捷键）、`Browser/BrowserTab.cs`（Find 封装与 JS 兜底） | Storage/、Assets/ | §2.1、§2.2、§7 |
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
| `src/ScBrowser/App.xaml(.cs)` | 应用入口：DI 装配、Serilog 日志、全局异常兜底、启动期初始化顺序 | 增删服务注册 / 改启动流程 |
| `src/ScBrowser/app.manifest` | PerMonitorV2 DPI 感知、Win10/11 兼容性声明 | 改 DPI 策略 |
| `src/ScBrowser/Infrastructure/AppPaths.cs` | **路径单一源**（用户数据/扩展/日志/DB/会话/WebUI）；`SCBROWSER_DATA_DIR` 开发覆盖 | 新增落盘位置 |
| `src/ScBrowser/Infrastructure/PathSafety.cs` | 防目录穿越的路径判定（**补分隔符**比较），CRX 解包与内部页面资源共用 | 新增「把外部路径落到本地」的地方 |
| `src/ScBrowser/Infrastructure/AtomicFile.cs` | 原子写文件（临时文件 + 替换），settings/session 用 | 新增需防半截写坏的配置文件 |
| `src/ScBrowser/Infrastructure/KnownFolders.cs` | 系统已知文件夹（目前只有「下载」）；不要自己拼 `%USERPROFILE%\Downloads` | 需要系统标准目录时 |
| `src/ScBrowser/Interop/NativeMethods.cs` | Win32 互操作：深色标题栏、圆角窗口 | 新增窗口级原生外观 |
| `src/ScBrowser/Themes/Theme.{Light,Dark}.xaml` | 颜色令牌（键名必须成对一致） | 调色 / 加令牌 |
| `src/ScBrowser/Themes/Controls.xaml` | 控件样式（工具栏按钮/标签/窗口按钮/地址栏） | 改控件外观 |
| `src/ScBrowser/Common/Converters.cs` | 值转换器（安全图标、可空、显隐） | 加绑定转换 |
| `src/ScBrowser/Interop/NativeMethods.cs` | Win32 互操作（DWM 深色/圆角、最大化钉工作区） | 加系统级外观/窗口行为 |
| `src/ScBrowser/Assets/sc-browser.ico` + `docs/icons/` | 应用图标（exe 内嵌 + 窗口 `Icon`）；`docs/icons/` 是设计源（SVG 为源，附各尺寸导出） | 换图标 |
| `src/ScBrowser/Browser/BrowserEnvironmentProvider.cs` | 创建并缓存**唯一**的 WebView2 环境（含扩展总开关） | 改环境级选项（代理/扩展开关） |
| `src/ScBrowser/Browser/BrowserTab.cs` | **单标签页**：全部可观察状态 + **按需创建**的 WebView2 引擎 + 事件接线 + 页内查找（原生/JS 兜底） | 改单页行为（导航/事件/图标/查找）；引擎生命周期见 §7 |
| `src/ScBrowser/Browser/TabManager.cs` | 标签集合中枢：创建（含惰性）/关闭/切换/相邻选择/历史记录 + **按需初始化引擎** + 网页颜色方案 | 改标签生命周期语义 |
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
| `src/ScBrowser/Views/TabReorder.cs` | 标签拖拽的落点换算（指针落在哪个槽位，纯函数 + 单测） | 改拖拽落点规则 |
| `src/ScBrowser/Views/AboutDialog.xaml(.cs)` | 「关于」对话框（图标 / 版本 / 用户数据目录） | 改关于页 |
| `src/ScBrowser/Views/SiteInfoPopup.xaml(.cs)` | 站点信息浮层（来源 / 安全状态 / 站点权限 / 清除站点数据） | 改站点信息与权限管理 |
| `src/ScBrowser/Views/ExtensionPopupWindow.xaml(.cs)` | 扩展工具栏弹窗（同 profile 的 WebView2 + `chrome.tabs` 兼容层） | 改扩展弹窗 |
| `src/ScBrowser/Views/MainWindow.{Tabs,Shortcuts,Find}.cs` | 主窗口的 partial 分册（标签条交互 / 快捷键 / 页内查找） | 改对应区域 |
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
| `build.bat` / `run.bat` / `test.bat` / `verify.bat` / `clean.bat` / `package.bat` / `ci.bat` | `manager.sh` 各命令的 Windows 原生等价物（纯 cmd + dotnet，不需要 bash） |
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

---

## 4. 数据流 / 接口契约（WebUI ↔ 宿主）

协议：页面 → 宿主 `{ type, requestId?, payload }`；宿主 → 页面 `{ type:"response", requestId, ok, result|error }`
或事件推送 `{ type, payload }`。**C# 侧序列化统一 camelCase**。

| 调用方 | 消息 type | 关键字段 | 返回 |
|---|---|---|---|
| newtab | `navigation.navigate` | `input`（原始输入，由宿主解析） | — |
| 各页 | `navigation.openInNewTab` | `url`, `active` | — |
| history | `history.query` | `search`, `limit`, `offset` | `HistoryEntry[]` |
| history | `history.remove` / `history.removeByUrl` / `history.clear` | `id` / `url` | — |
| history | `history.visits` | `search`, `from`, `to`（ISO）, `limit` | `VisitEntry[]`（按时间倒序） |
| newtab | `history.topSites` | `limit` | `HistoryEntry[]` |
| bookmarks | `bookmarks.query` / `bookmarks.add` / `bookmarks.remove` / `bookmarks.toggle` / `bookmarks.exists` | `url`, `title`, `id`, `parentId` | 视消息而定 |
| bookmarks | `bookmarks.addFolder` / `bookmarks.rename` / `bookmarks.move` / `bookmarks.removeById` | `title`, `id`, `parentId` | 新 id / 是否成功 |
| downloads | `downloads.query` / `downloads.clear` / `downloads.remove` | `id` | `DownloadEntry[]` |
| downloads | `downloads.openFile` / `downloads.revealInFolder` | `id`（**不收路径**，宿主查库） | — |
| downloads | `downloads.pause` / `downloads.resume` / `downloads.cancel` | `id` | — |
| 扩展 | `extensions.list` | — | `ExtensionInfo[]`（含版本、描述、权限、图标 data URL、弹窗/选项路径、MV 版本、`isPinned`/`pinOrder`） |
| 扩展 | `extensions.installFromFolder` / `extensions.installFromCrx` | —（弹系统对话框） | `ExtensionInfo \| null`（用户在权限确认里取消时为空） |
| 扩展 | `extensions.installFromStore` | `input`（商店链接或 32 位 ID） | `ExtensionInfo \| null` |
| extensions | `extensions.setPinned` | `id`, `pinned` | — |
| extensions | `extensions.remove` / `extensions.setEnabled` / `extensions.openManagedFolder` | `id`, `enabled` | — |
| settings | `settings.get` | — | `AppSettings` |
| settings | `settings.update` / `settings.setTheme` | 偏好字段（合并，见 `CopyPreferencesFrom`） / `theme` | — |
| settings | `app.info` | — | 版本与各路径 |
| settings | `app.clearBrowsingData` | `cache`, `cookies`, `storage` | — |

**宿主 → 页面事件**：`downloads.changed`、`extensions.changed`、`settings.changed`（只推给停在内部页面上的标签）。

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

- **地址栏「编辑态」不能用「获得焦点」判定**：启动时会自动聚焦地址栏，若那时即算编辑态，
  首个页面加载完成后写回地址栏会被挡掉，结果地址栏恒为空。改为「用户敲下会改变文本的键」才置位
  （`AddressBar_PreviewKeyDown`）。
- **每标签一个 WebView2 是刻意的取舍**：换来切换零延迟与页面状态保留，代价是内存随标签数增长
  （与 Chrome 进程模型一致）。不要为了省内存改成单 WebView2 反复导航——会丢状态且切换卡顿。
- **airspace（空气墙）**：WebView2 是 HWND 控件，任何 WPF 元素都无法绘制在其之上。
  因此**浮层必须走独立顶层窗口**（主菜单用 `ContextMenu` 正是这个原因）。
- **ZIP 段必须切到独立流再解包**：ZIP 的中央目录偏移是相对归档起点记录的，
  `ZipArchive` 从非零偏移的流起读会报 "End Of Central Directory ... does not correspond"。
- **`CoreWebView2BrowsingDataKinds` 没有 `None`**：用 `(CoreWebView2BrowsingDataKinds)0` 作为空集起点。
- **`TotalBytesToReceive` 是 `ulong?`、`BytesReceived` 是 `long`**：算进度前先统一成 `long`。
- **MVVM 源生成器命名冲突**：`[ObservableProperty] _activeTab` 会生成 `OnActiveTabChanged` 部分方法钩子，
  自己再写同名方法会 CS0111；本项目把自定义方法命名为 `HandleActiveTabChanged`。
- **XML 注释里不能出现连续 `--`**：XAML 注释中的装饰线要用 `====`。
- **`IsKeyboardFocusWithin` 是只读依赖属性**：不能在 XAML 里绑定写回，只能走代码。
- **自绘外壳的第三个坑：缩放带必须内缩**：WebView2 是独立子 HWND，会吃掉落在它身上的鼠标事件——
  缩放命中带若压在网页上，`WindowChrome` 的缩放判定永远轮不到。因此根布局留了
  `Margin="6,0,6,6"` 的内缩带（`MainWindow.xaml`），改布局时别把这圈吃掉，否则窗口又变成拖不动尺寸。
  **但最大化时必须把它收成 0**：最大化不需要缩放，留着白让出 14×8 px 视口，
  宽度卡得紧的页面（例如自建面板）会因此冒出滚动条——`MainWindow.ApplyWindowedFrame` 负责切换。
- **任何网页都能 `chrome.webview.postMessage`**：WebView2 给所有顶层文档注入消息桥，并非只给内部页面。
  宿主能力必须按 `args.Source` 鉴权（`InternalPageRegistry.IsMessageAllowed`），页面传来的路径一律不信（下载按 id 查库）。
  内部页面因此用自定义 scheme `browser://<页面>/`（每页一个来源）而不是 `file://`；也不要换成 `https` 虚拟主机映射——
  扩展内容脚本的 `<all_urls>` 会匹配到它，扩展就能注入内部页面借道越权。
- **`CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` 是只读且默认 null**：只能经构造函数
  `new CoreWebView2EnvironmentOptions(customSchemeRegistrations: …)` 传入，`.Add` 会空引用。
  资源过滤器优先用带 `RequestSourceKinds` 的重载，旧运行时退回旧重载（`BrowserTab.ServeInternalPages`）。
- **防目录穿越要「目录 + 分隔符」前缀比较**（`PathSafety.IsUnderDirectory`）：只比目录本身，
  `…\foo-evil\x` 也以 `…\foo` 开头，同级同前缀目录会被放行。
- **标签状态与引擎分离（惰性恢复）**：`BrowserTab` 的构造函数**不再创建 WebView2**，`View` 在第一次
  `EnsureInitializedAsync` 时才创建并触发 `EngineAttached`（宿主据此挂树）。会话恢复时只有激活的标签
  立刻建引擎，其余只持有地址/标题，激活时才加载——标签数不再等于 WebView2 进程数。
  **改单页行为时注意**：`View` / `Core` 都可能为 `null`，导航与缩放会被记成「待处理」，初始化完成后补上；
  宿主挂载必须在 `EnsureCoreWebView2Async` 之前完成，所以挂树动作只能由 `EngineAttached` 触发，不要提前调用。
  顺带的好处：恢复出来的后台标签不再重复写历史（导航完成才记历史）。
- **`ClearValue(VisibilityProperty)` 会把 XAML 绑定一起清掉**：全屏需要收起错误条 / 状态栏，
  但它们是绑定控制可见性的。清掉本地值等于连绑定一起删，退出全屏后绑定失效、退回默认值
  `Visible`，于是留下一条没有文字的红条。正确做法是把「chrome 是否可见」做成窗口依赖属性
  （`MainWindow.ChromeVisible`），与文本一起走 `TextAndChromeVisibility` 多值绑定。
- **先移除活跃项再发变更通知**：`DownloadManager.OnProgress` 完成时若先 `Changed` 再 `_active.TryRemove`，
  界面在通知里读到的 `ActiveCount` 还是旧值，工具栏下载指示器就永远不消失。
- **全屏用 Win32 铺满显示器**：`WindowState=Maximized` 只铺工作区（留出任务栏），
  不能当全屏用。`NativeMethods.FillMonitor` 按显示器完整矩形 `SetWindowPos`；
  退出时要把 `Left/Top/Width/Height` 重新设回去（Win32 挪过窗口后 WPF 的值已不同步）。
  全屏时还要把 `WindowChrome.CaptionHeight` 归零，否则点网页顶部会变成拖窗口。
- **证书错误只该问「顶层导航」**：`ServerCertificateErrorDetected` 对每个子资源都会触发，
  逐个弹窗不可用。`BrowserTab` 用 `_pendingMainFrameUri`（NavigationStarting 记下、Completed 清空）过滤。
- **CRX3 的头部有两把公钥**：商店包一般同时带 `sha256_with_rsa` 与 `sha256_with_ecdsa`，
  `signed_header_data.crx_id` 只对应其中一把。必须先按 crx_id 筛出匹配的那份再验签，
  否则会误报「头部 ID 与公钥不一致」（实现见 `CrxPackage.ReadV3`）。
- **扩展图标只能是 data URL**：内部页面是 `browser://` 来源，加载不了扩展目录里的 `file://` 图片；
  `ExtensionService` 把图标读成 base64 data URL 再交给页面。
- **`chrome.tabs` 兼容层只覆盖我们自己承载的扩展页面**：`AddScriptToExecuteOnDocumentCreatedAsync`
  注入的补丁在弹窗 WebView2 里生效，后台 service worker 拿不到——不要把它当成完整实现。
- **无痕不能用「新环境」实现**：必须同一个 `CoreWebView2Environment` + InPrivate 的
  `CoreWebView2ControllerOptions`（见 §3.5）。
- **主题令牌键必须成对存在**：`Theme.Light.xaml` 与 `Theme.Dark.xaml` 键名不一致时，换肤会缺键。
- **自绘窗口外壳的两个坑**（`WindowStyle=None` + `WindowChrome`）：
  1. **最大化会被撑出屏幕**：系统仍按「带边框窗口」算最大化尺寸，把隐形缩放边框也算进去，
     窗口于是溢出屏幕若干像素（本机实测 7px），贴着边缘的内容被裁掉（右侧关闭按钮首当其冲）。
     解法是在 `WM_GETMINMAXINFO` 里把尺寸钉到显示器工作区（`NativeMethods.ClampMaximizedToWorkArea`），
     别用「最大化时补 Margin」那种猜边宽的写法，多屏/DPI 变化下会错。
  2. **标题栏区域内点击会变成拖窗口**：`CaptionHeight` 覆盖的整条标签条都走了标题栏命中测试，
     里面的按钮/标签必须标 `WindowChrome.IsHitTestVisibleInChrome="True"`（本项目统一写在
     `Controls.xaml` 的按钮样式里）。验证手法：`SendMessage(hwnd, WM_NCHITTEST, …)` 看返回
     是 `HTCAPTION` 还是 `HTCLIENT`——比肉眼试点更可靠。
- **绑定的路径取不到值时，目标属性会退回「类型默认值」**：`Visibility` 的默认值是
  **Visible**（不是 Collapsed）。所以 `Visibility="{Binding ActiveTab.LastError, Converter=...}"`
  在 `ActiveTab` 为 `null` 的那一瞬间（新建窗口、首个标签还没建好）会**显示一个没有内容的红条**，
  随后又消失——看起来就是「开窗时红条闪一下」。凡是靠绑定控制显隐的地方，
  都要显式写 `FallbackValue=Collapsed`（必要时再加 `TargetNullValue`）。
- **WebView2 的默认底色也要跟着主题走**：`WebView2.DefaultBackgroundColor` 默认是**白色**，
  深色主题下新建标签/新窗口会在页面画出来之前先闪一下白底。见
  `WebTheme.DefaultBackground` + `TabManager.ApplyWebTheme`（与颜色方案一起设）。
- **拖拽补偿量要用「槽位几何」而不是元素偏移**：标签条是 `ItemsControl + DataTemplate`，
  对被拖的 Button 调 `VisualTreeHelper.GetOffset` 拿到的是它**相对自己 ContentPresenter** 的偏移
  （恒为 0），不是标签条坐标——用它算补偿，每次换位标签都会瞬移。正解见
  `TabReorder.Step`：偏移 = 视觉左边缘 − **落点槽位**左边缘（槽位几何在换位前后不变）。
- **导航失败提示要延迟落地**：WebView2 冷启动、被后续导航取代的请求，常在「失败」后几毫秒内就成功；
  立刻挂红条的表现是「闪一下」（新开窗口最容易看到）。`BrowserTab.ReportNavigationError` 的做法是
  延迟 700ms + 版本号作废（期间任何新导航/成功导航都会作废它），把 `OperationCanceled` 直接排除，
  并且**标签首次导航失败会自动重试一次**（冷启动偶发失败重试即好，真实故障重试仍失败、照常报错）。
- **深色「影响网页」要顺着站点走**：把 `CoreWebView2Profile.PreferredColorScheme` 设成 Dark/Light/Auto，
  让页面的 `prefers-color-scheme` 随应用主题变化即可——声明了深色样式的站点自动变深，
  没声明的保持原样。**不要**用 CDP 强制反色：会把排版、图片、图表拧坏。
  内部页面（`browser://*`）的 `common/base.css` 用的是同一个媒体查询，因此一并跟随，无需另推消息。
- **全应用只有一个 WebView2 profile**：颜色方案是 profile 级设置，设一次即影响所有标签，
  但新标签仍会显式再设一次（见 `TabManager.ApplyWebTheme`），避免依赖「之前一定设过」的隐含前提。
- **页内查找要兼容旧 WebView2 运行时**：`CoreWebView2.Find` 需要 `ICoreWebView2_28`，
  本机 WebView2 131 没有该接口，访问会抛 `NotImplementedException`。`BrowserTab` 因此在
  原生不可用（或启动失败）时退回 JS：用 `TreeWalker` 收集文本节点的匹配区间，再用
  `CSS.highlights` 高亮，状态挂在 `window.__scFind` 上。JS 路径只覆盖主文档、匹配不跨文本节点，
  是旧运行时的降级方案；升级运行时后会自动走原生路径。**不要为了「只留一条实现」删掉兜底。**
