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
| 改外壳 UI（标签条/工具栏/地址栏外观与交互） | `Views/MainWindow.xaml(.cs)`、`Themes/*`、`Common/Converters.cs` | Browser/、Storage/、Assets/ | §2.1 |
| 改标签页行为（导航/事件/安全图标/favicon） | `Browser/BrowserTab.cs`、`Browser/TabManager.cs` | Views/、Assets/、Storage/ | §2.2 |
| 加/改内部页面（WebUI） | `Assets/ui/<page>/*`、`Browser/WebMessageRouter.cs`、`Browser/InternalPageRegistry.cs` | Views/、Storage/（除非要新数据） | §2.3、§4 |
| 加/改数据存储（历史/书签/下载） | `Storage/*` | Views/、Assets/ | §2.4、§4 |
| 改扩展能力 | `Browser/ExtensionService.cs`、`Browser/CrxPackage.cs`、`Assets/ui/extensions/*` | Views/、Storage/ | §2.5、§3.2 |
| 改主题/设置/会话 | `Services/SettingsService.cs`、`Services/ThemeService.cs`、`Services/SessionStore.cs`、`Themes/*` | Browser/、Storage/ | §2.6 |
| 改启停/构建/发布（运维） | `manager.sh`、`ScBrowser.csproj`、`docs/02-使用说明.md` | 所有业务代码 | §6 |

> 跨多个区域的任务才需要跨列读；仍遵循「**先定位函数名，再只读那几个函数**」。

---

## 1. 文件职责速查（先看这张表）

### 宿主（C#）

| 文件 | 职责 | 何时改它 |
|---|---|---|
| `src/ScBrowser/App.xaml(.cs)` | 应用入口：DI 装配、Serilog 日志、全局异常兜底、启动期初始化顺序 | 增删服务注册 / 改启动流程 |
| `src/ScBrowser/app.manifest` | PerMonitorV2 DPI 感知、Win10/11 兼容性声明 | 改 DPI 策略 |
| `src/ScBrowser/Infrastructure/AppPaths.cs` | **路径单一源**（用户数据/扩展/日志/DB/会话/WebUI） | 新增落盘位置 |
| `src/ScBrowser/Interop/NativeMethods.cs` | Win32 互操作：深色标题栏、圆角窗口 | 新增窗口级原生外观 |
| `src/ScBrowser/Themes/Theme.{Light,Dark}.xaml` | 颜色令牌（键名必须成对一致） | 调色 / 加令牌 |
| `src/ScBrowser/Themes/Controls.xaml` | 控件样式（工具栏按钮/标签/地址栏） | 改控件外观 |
| `src/ScBrowser/Common/Converters.cs` | 值转换器（安全图标、可空、显隐） | 加绑定转换 |
| `src/ScBrowser/Browser/BrowserEnvironmentProvider.cs` | 创建并缓存**唯一**的 WebView2 环境（含扩展总开关） | 改环境级选项（代理/扩展开关） |
| `src/ScBrowser/Browser/BrowserTab.cs` | **单标签页**：持有 WebView2 + 全部可观察状态 + 事件接线 | 改单页行为（导航/事件/图标） |
| `src/ScBrowser/Browser/TabManager.cs` | 标签集合中枢：创建/关闭/切换/相邻选择/历史记录 | 改标签生命周期语义 |
| `src/ScBrowser/Browser/IBrowserTabHost.cs` | 内容区宿主抽象（视图层实现，防 TabManager 依赖控件） | 改挂载约定 |
| `src/ScBrowser/Browser/InternalPageRegistry.cs` | `browser://xxx` → 本地 WebUI 文件映射 | 新增内部页面 |
| `src/ScBrowser/Browser/AddressBarResolver.cs` | 地址栏输入 → 地址/搜索词判定 | 改地址解析规则 |
| `src/ScBrowser/Browser/SecurityState.cs` | 安全状态枚举（锁形图标语义） | 加安全状态 |
| `src/ScBrowser/Browser/WebMessageRouter.cs` | **内部页面 ↔ 宿主消息路由**（协议唯一实现处） | 新增 WebUI 能调用的能力 |
| `src/ScBrowser/Browser/ExtensionService.cs` | 扩展列出/安装/启停/卸载 | 改扩展能力 |
| `src/ScBrowser/Browser/CrxPackage.cs` | CRX 解包（v2/v3 头解析 + ZIP 提取 + 目录穿越防护） | 改解包逻辑 |
| `src/ScBrowser/Storage/BrowserDatabase.cs` | SQLite 建表 / 连接工厂（WAL） | 改 schema |
| `src/ScBrowser/Storage/Entries.cs` | 数据记录定义（HistoryEntry/BookmarkEntry/DownloadEntry + 状态枚举） | 加字段 |
| `src/ScBrowser/Storage/{History,Bookmark,Download}Repository.cs` | 三个仓储的读写 | 改查询/写入 |
| `src/ScBrowser/Services/SettingsService.cs` | 设置读写（JSON）＋ 归一化 ＋ 变更事件 | 加设置项 |
| `src/ScBrowser/Services/ThemeService.cs` | 运行时换肤（替换颜色字典索引 0） | 改换肤机制 |
| `src/ScBrowser/Services/SessionStore.cs` | 会话保存/恢复（JSON） | 改会话内容 |
| `src/ScBrowser/Services/DownloadManager.cs` | 下载接管、进度节流落盘、列表 | 改下载行为 |
| `src/ScBrowser/Services/StartupCommandRunner.cs` | 启动命令行参数（`--install-extension`） | 加启动参数 |
| `src/ScBrowser/ViewModels/MainViewModel.cs` | 外壳状态投影与命令（不碰 WebView2） | 加工具栏/菜单命令 |
| `src/ScBrowser/Views/MainWindow.xaml(.cs)` | 外壳布局 + 窗口外观 + **键盘快捷键** | 改外壳结构/快捷键 |
| `src/ScBrowser/Views/Controls/BrowserContentHost.cs` | 内容区宿主实现（WebView2 挂载与显示切换） | 改内容区挂载方式 |
| `src/ScBrowser/Assets/ui/**` | **WebUI 内部页面**（HTML/CSS/JS） | 改内部页面 |
| `src/ScBrowser/Assets/ui/common/browser-api.js` | WebUI 侧消息桥（协议客户端） | 改协议 |

### 其他

| 路径 | 职责 |
|---|---|
| `tests/ScBrowser.Tests/` | xUnit 单测（地址栏解析、CRX 解包） |
| `samples/extensions/hello-sc-browser/` | 示例扩展，用于验证扩展加载链路 |
| `manager.sh` | 统一管理脚本（构建/运行/测试/发布/清理/日志） |
| `check-docs.sh` | 文档体系机械体检（技术栈无关） |

---

## 2. 按「我要做什么」反查代码位置（任务 → 入口）

> 行号会随改动漂移；**优先按「函数名 / 注释锚点」搜索**。
> **自愈规则**：搜不到/对不上 → 以 grep/代码为准，完成后顺手订正本表。

### 2.1 外壳 UI（`Views/MainWindow.xaml` / `Views/MainWindow.xaml.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 工具栏按钮外观 | `Themes/Controls.xaml` 的 `ToolbarButtonStyle` | 用 `DynamicResource` 引颜色令牌 |
| 标签条项的样式 | `Controls.xaml` 的 `BrowserTabButtonStyle` / `TabCloseButtonStyle` | `IsActive` 触发激活态背景 |
| 地址栏回车/失焦行为 | `AddressBar_KeyDown` / `AddressBar_LostKeyboardFocus` | 失焦会恢复显示当前页地址 |
| 「编辑态」判定 | `AddressBar_PreviewKeyDown` + `IsTextProducingKey` | **不能**用「获得焦点」判定，见 §7 |
| 键盘快捷键 | `MainWindow.TryHandleShortcut` | 单一 switch，按修饰键分组 |
| 主菜单项 | `MenuButton_Click` + XAML 里的 `Button.ContextMenu` | |
| 窗口深色标题栏/圆角 | `NativeMethods.ApplyDarkMode` / `ApplyRoundedCorners` | |
| 启动/退出钩子 | `MainWindow.OnLoaded` / `OnClosing` | 会话恢复与保存在这里 |

### 2.2 标签页（`Browser/BrowserTab.cs` / `Browser/TabManager.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 新增标签 | `TabManager.CreateAsync` | 顺序：入集合 → 挂树 → 初始化 → 接线 → 激活 → 导航 |
| 关闭标签的邻居选择 | `TabManager.Close` | 优先右邻，否则左邻（Chrome 语义） |
| 页内导航到内部页面 | `BrowserTab.Navigate` + `InternalPageRegistry.ResolveToFileUri` | 内部页面用 `_logicalUrl` 保逻辑地址 |
| 地址栏显示地址的推导 | `BrowserTab.UpdateUrlFromCore` | 内部页面显示 `browser://xxx` 而非 `file://` |
| 安全图标数据来源 | `BrowserTab.EnableSecurityEventsAsync` / `OnSecurityStateChanged` | CDP `Security.enable` + `securityStateChanged` |
| favicon 获取 | `BrowserTab.UpdateFaviconAsync` | `Core.GetFaviconAsync(Png)` |
| 历史入库时机 | `TabManager.RecordHistory`（订阅 `BrowserTab.NavigationFinished`） | 跳过 `browser://`/`about:`/`file://` |
| 新窗口请求转新标签 | `TabManager.WireTabEvents` 的 `NewWindowRequested` | |
| WebView2 设置项 | `BrowserTab.ConfigureSettings` | 注意 `AreBrowserAcceleratorKeysEnabled=false` |

### 2.3 内部页面（`Assets/ui/` + `Browser/WebMessageRouter.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 新增一个内部页面 | `InternalPageRegistry.Pages` 加一行 + 建 `Assets/ui/<name>/index.html` | |
| 新增页面可调用的宿主能力 | `WebMessageRouter.DispatchAsync` 加一个 `case` | 同时更新 §4 契约表 |
| WebUI 侧发请求 | `browser-api.js` 的 `sc.send` / `sc.emit` / `sc.on` | |
| 宿主推事件给页面 | `WebMessageRouter.Broadcast` | 例：`downloads.changed`、`extensions.changed` |
| 取值助手（容错解析） | `GetString` / `GetBool` / `GetInt` / `GetLong` | 字段缺失时返回兜底值 |

### 2.4 数据存储（`Storage/`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 加表/加列 | `BrowserDatabase.Initialize` 的建表 SQL | 幂等 `IF NOT EXISTS` |
| 历史去重语义 | `HistoryRepository.RecordVisit` | 同 URL 累加 `visit_count` |
| 历史查询/搜索 | `HistoryRepository.Query` / `QueryTopSites` | `search` 用 LIKE 匹配地址与标题 |
| 书签增删/切换 | `BookmarkRepository.Add` / `Remove` / `Toggle` / `Exists` | |
| 下载记录 | `DownloadRepository.Upsert` / `MarkInProgressAsFailed` | 启动时清理幽灵「下载中」项 |

### 2.5 扩展（`Browser/ExtensionService.cs` / `CrxPackage.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 列出扩展 | `ExtensionService.ListAsync` | 平台只给 id/name/isEnabled |
| 从目录安装 | `ExtensionService.InstallFromFolderAsync` | 先校验 `manifest.json` |
| 从 `.crx` 安装 | `ExtensionService.InstallFromCrxAsync` → `CrxPackage.ExtractTo` | 解到 `AppPaths.ManagedExtensionsFolder` |
| 定位 ZIP 偏移 | `CrxPackage.FindZipOffset` | v2：16+key+sig；v3：12+header |
| 解包 | `CrxPackage.ExtractTo` | **ZIP 段要切独立流**，见 §7 |
| 目录名规整 | `CrxPackage.DeriveFolderName` | 不得以 `_` 开头 |

### 2.6 设置 / 主题 / 会话（`Services/`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 加设置项 | `AppSettings` 加属性（**必须给默认值**） + `SettingsService.Normalize` | 旧配置才能平滑升级 |
| 换肤 | `ThemeService.Apply` / `SwapDictionary` | 替换 `MergedDictionaries[0]` |
| 会话保存/恢复 | `SessionStore.Save` / `Load` / `SnapshotUrls` | 落 `session.json` |
| 下载接管 | `DownloadManager.OnDownloadStarting` / `OnProgress` | 进度按 1% 节流落盘 |

---

## 3. 重点链路详解（高频改动区 / 容易写错的地方）

### 3.1 内部页面消息链路（WebUI ↔ 宿主）

- **需求语义**：内部页面（HTML/JS）要能读写历史、书签、扩展、设置等宿主能力。
- **关键坑**：WebUI 跑在 `file://` 下，不能用 `fetch` 调宿主；必须走 WebView2 消息桥。
  请求/响应要能配对，否则页面无法 `await` 结果。
- **正确实现**：
  1. 页面 `sc.send(type, payload)` → `window.chrome.webview.postMessage({type, requestId, payload})`
  2. 宿主 `TabManager.WebMessageReceived` → `WebMessageRouter.HandleAsync` → `DispatchAsync`
  3. 宿主 `WebMessageRouter.Reply` 回 `{type:"response", requestId, ok, result|error}`
  4. 页面侧按 `requestId` 配对 resolve/reject（含 15s 超时兜底）
- **调用链**：`Assets/ui/*.js` → `browser-api.js` → WebView2 bridge → `TabManager.WebMessageReceived`
  → `WebMessageRouter.HandleAsync` → `DispatchAsync` → `Storage/*` 或 `Services/*`

### 3.2 扩展加载与运行

- **需求语义**：能加载解包扩展与 `.crx`，启停/卸载，且在页面上真正生效。
- **关键坑**（三条，缺一不可）：
  1. `CoreWebView2EnvironmentOptions.AreBrowserExtensionsEnabled` 必须为 `true`，
     否则 `AddBrowserExtensionAsync` 返回 `ERROR_NOT_SUPPORTED`。**此选项只在环境创建期生效，改动需重启**。
  2. 扩展按 **profile** 维度管理，而 profile 只能从已初始化的 `CoreWebView2` 上取
     → 因此要求至少有一个标签页已就绪（见 `TabManager.Profile`）。
  3. 扩展内容**不会**被复制到用户数据目录；安装后改动扩展目录内的文件会导致它被自动卸载。
- **正确实现**：`ExtensionService.InstallFromFolderAsync`（目录）/ `InstallFromCrxAsync`（先 `CrxPackage.ExtractTo` 再走目录安装）
- **调用链**：`Assets/ui/extensions/extensions.js` → `WebMessageRouter` `extensions.*`
  → `ExtensionService` → `CoreWebView2Profile.AddBrowserExtensionAsync`

### 3.3 键盘快捷键在网页聚焦时仍然生效

- **需求语义**：焦点在网页里时，Ctrl+T / Ctrl+W / F5 等仍应由外壳处理。
- **关键坑**：WebView2 是基于 HWND 的控件，键盘事件本不会回到 WPF 输入系统；
  且若 `AreBrowserAcceleratorKeysEnabled` 为 `true`，WebView2 会先把这些键当浏览器快捷键吃掉。
- **正确实现**：`BrowserTab.ConfigureSettings` 关闭 `AreBrowserAcceleratorKeysEnabled`；
  WPF 版 WebView2 内部会把 `CoreWebView2Controller.AcceleratorKeyPressed` 转发成
  WPF 的 `PreviewKeyDown` / `KeyDown` 重新抛出，因此**只需在 `MainWindow.OnPreviewKeyDown` 处理**，无需反射。
- **调用链**：按键 → `CoreWebView2Controller_AcceleratorKeyPressed`（WPF 控件内部）
  → `PreviewKeyDownEvent` → `MainWindow.OnPreviewKeyDown` → `TryHandleShortcut`

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
| newtab | `history.topSites` | `limit` | `HistoryEntry[]` |
| bookmarks | `bookmarks.query` / `bookmarks.add` / `bookmarks.remove` / `bookmarks.toggle` / `bookmarks.exists` | `url`, `title`, `id` | 视消息而定 |
| downloads | `downloads.query` / `downloads.clear` / `downloads.remove` | `id` | `DownloadEntry[]` |
| downloads | `downloads.openFile` / `downloads.revealInFolder` | `path` | — |
| extensions | `extensions.list` | — | `ExtensionInfo[]` |
| extensions | `extensions.installFromFolder` / `extensions.installFromCrx` | —（弹系统对话框） | `ExtensionInfo \| null` |
| extensions | `extensions.remove` / `extensions.setEnabled` / `extensions.openManagedFolder` | `id`, `enabled` | — |
| settings | `settings.get` | — | `AppSettings` |
| settings | `settings.update` / `settings.setTheme` | `AppSettings` 全量 / `theme` | — |
| settings | `app.info` | — | 版本与各路径 |
| settings | `app.clearBrowsingData` | `cache`, `cookies`, `storage` | — |

**宿主 → 页面事件**：`downloads.changed`、`extensions.changed`、`settings.changed`。

**数据铁律**：
- 内部页面渲染不可信文本（标题/地址/扩展名）一律用 `textContent`，**禁止 `innerHTML`**（防 XSS）。
- 时间统一以 ISO 8601 UTC 字符串入库，展示层本地化。
- 用户数据仅落 `%LOCALAPPDATA%\sc-browser`，不写仓库内。

---

## 6. 运维 / 脚本（启停 / 构建 / 发布）—— 不碰业务即可独立维护

> 做运维任务只读本节 + `manager.sh` + `src/ScBrowser/ScBrowser.csproj`，不用打开业务代码。
> **与 docs/02 分工**：本节只留代码/脚本位置指针；具体操作步骤与坑以 [`docs/02-使用说明.md`](docs/02-使用说明.md) 为准。

- **统一入口**：`manager.sh`（`build` / `run` / `start` / `stop` / `test` / `verify` / `publish` / `clean` / `data` / `logs` / `reset`）。
- **路径单一源**：`Infrastructure/AppPaths.cs`；`manager.sh` 中的 `APPDATA_DIR` 必须与其保持一致。
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
- **主题令牌键必须成对存在**：`Theme.Light.xaml` 与 `Theme.Dark.xaml` 键名不一致时，换肤会缺键。
