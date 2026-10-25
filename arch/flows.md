# 重点链路详解（ARCHITECTURE.md 分册）

> 本文件是 [`../ARCHITECTURE.md`](../ARCHITECTURE.md) 的分册：主文件只留总览、任务作用域路由、
> 文件职责总表与易踩的坑；「高频改动区到底怎么连」这类纵深内容拆到这里，避免主文件变成 token 黑洞。
> 编号沿用主文件（§3.x），交叉引用不会因此失效。

### 3.7 标签惰性恢复（状态与引擎分离）

- **为什么**：恢复 N 个标签时若立刻建 N 个 WebView2，启动慢、内存被占满。现在标签是「状态 + 按需引擎」：
  `BrowserTab` 先持有地址/标题/缩放，`View` 为空；被激活时才 `EnsureInitializedAsync` 创建 WebView2。
- **链路**：`MainViewModel.StartAsync`（只有 activeIndex 那个 `deferLoad:false`）
  → `TabManager.CreateAsync(..., deferLoad)`（`SetInitialLocation` 写好状态、`WireTabEvents` 接 tab 级事件）
  → 用户点开标签 → `TabManager.Activate` → `TryEnsureEngineAsync` → `BrowserTab.EnsureInitializedAsync`
  → `EngineAttached`（宿主 `AttachTab` 挂树）→ `EnsureCoreWebView2Async` → 补 `_pendingUrl` 导航 / `_pendingZoom`。
- **注意**：
  1. `View` / `Core` 都可能为 `null`，所有读写它们的地方要空安全；缩放与导航在引擎未就绪时记成待处理。
  2. `TabCreated` 现在在引擎创建**之前**触发（tab 级事件订阅者不受影响）。
  3. 激活时初始化失败不抛到 UI 线程，而是把原因挂到该标签的错误条（`ShowEngineError`）。
  4. 惰性标签不会在启动时导航，因此不会重复写历史。

---

## 3. 重点链路详解（高频改动区 / 容易写错的地方）

### 3.1 内部页面消息链路（WebUI ↔ 宿主）

- **需求语义**：内部页面（HTML/JS）要能读写历史、书签、扩展、设置等宿主能力。
- **页面怎么来的**：`browser://<页面>/` 是环境创建时注册的自定义 scheme（`BrowserEnvironmentProvider.CreateAsync`），
  由 `BrowserTab.ServeInternalPages` 拦 `WebResourceRequested`、按 `InternalPageRegistry.ResolveResourcePath`
  从 `Assets/ui/` 读文件回应。页面里的 `../common/x` 会被浏览器规整成 `browser://<页面>/common/x`，所以无需改引用。
- **关键坑**：
  1. 页面不能用 `fetch` 调宿主；必须走 WebView2 消息桥。请求/响应要能配对，否则页面无法 `await` 结果。
  2. **消息桥对所有网页开放**（WebView2 给每个顶层文档都注入 `chrome.webview`），所以宿主必须按来源鉴权，
     见下方第 2 步；网页也不得自行跳到 `browser://`（`BrowserTab.ShouldBlockInternalNavigation`）。
- **正确实现**：
  1. 页面 `sc.send(type, payload)` → `window.chrome.webview.postMessage({type, requestId, payload})`
  2. 宿主 `TabManager.WebMessageReceived` → `WebMessageRouter.HandleAsync`：
     `args.Source` 不是内部页面 → 丢弃不回执；是内部页面但不在白名单（`InternalPageRegistry.IsMessageAllowed`）→ 回错误
  3. 通过鉴权 → `DispatchAsync` → `WebMessageRouter.Reply` 回 `{type:"response", requestId, ok, result|error}`
  4. 页面侧按 `requestId` 配对 resolve/reject（含 15s 超时兜底）
- **调用链**：`Assets/ui/*.js` → `browser-api.js` → WebView2 bridge → `TabManager.WebMessageReceived`
  → `WebMessageRouter.HandleAsync`（鉴权）→ `DispatchAsync` → `Storage/*` 或 `Services/*`

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

### 3.4 主题贯穿：应用主题 → 外壳 → 网页内容

- **需求语义**：设置里选深色，外壳与网页内容都要变深（此前深色只影响外壳颜色与窗口边框）。
- **唯一入口**：`ThemeService.Apply(theme)`——换颜色字典、通知 DWM、并触发 `Applied` 事件。
- **调用链**：
  1. 启动：`App.OnStartup` → `Apply(settings.Theme)`（必须在主窗口显示前，否则会先亮后暗闪一下）。
  2. 运行中：WebUI 设置页 → `settings.update` / `settings.setTheme` → `WebMessageRouter` → `Apply`。
  3. `Applied` →（组合根 `App` 订阅）→ `TabManager.ApplyWebTheme(theme)` → 逐个标签设
     `CoreWebView2.Profile.PreferredColorScheme = WebTheme.Resolve(theme)`。
  4. 新标签：`TabManager.CreateAsync` 在 `InitializeAsync` 之后补设一次（见 §7 的 profile 说明）。
- **为什么订阅放在 `App`**：`ThemeService` 属服务层，不该反向依赖标签层；放在组合根同时保证
  「只要走 `Apply`，网页就一定同步」，将来新增第三个调用点也不会漏。
- **`System` 语义**：映射为 `Auto` 交给 WebView2 跟随系统，而不是在切换那一刻固化成当时的深浅。

### 3.5 多窗口与无痕（一份 vs 共享）

- **按窗口一份**：`TabManager` / `MainViewModel` / `MainWindow`。它们**不注册到 DI 容器**，
  由 `BrowserWindowManager.CreateWindow` 手工 new——容器表达不了「按会话类型构造」，
  而且单例服务若持有某个窗口实例，多窗口下必然出错。
- **全应用共享**：`BrowserEnvironmentProvider`（唯一环境）、`SettingsService`、`ThemeService`、
  三个仓储、`BrowserWindowManager`、`DownloadManager`、`WebMessageRouter`。
- **无痕怎么做的**：仍然是**同一个环境**，只把 `CoreWebView2ControllerOptions.IsInPrivateModeEnabled`
  打开（见 `Browser/BrowserSession.cs`）。**不要**为无痕再开一个环境——那会破坏「单环境」铁律，
  扩展安装态、Cookie 分区也跟着乱。
- **订阅顺序**：`DownloadManager` / `WebMessageRouter` 都订阅
  `BrowserWindowManager.TabManagerCreated`，所以它们**必须在第一个窗口创建之前**构造完毕
  （见 `App.OnStartup` 的注释），否则会漏掉首个窗口的标签页。
- **无痕的边界**：不写历史（`TabManager.RecordHistory` 直接返回）、不保存会话、下载不入库
  （`DownloadManager.OnDownloadStarting` 交给 WebView2 默认提示条）；会话保存只取第一个非无痕窗口。
- **窗口关闭**：`BrowserWindowManager.Forget` 同步摘掉该窗口的 TabManager，
  否则跨窗口服务会一直持有已死的标签。

### 3.6 地址栏下拉（omnibox 建议）

- **为什么是独立顶层窗口**：WebView2 是 HWND 控件，WPF 元素盖不到它上面（airspace），
  下拉要悬在网页上就只能开窗口 —— `Views/AddressBarPopup.xaml`。
- **不抢焦点**：弹出层 `ShowActivated=False`，内容 `Focusable=False`；
  ↑/↓/Enter/Esc 全部由 `MainWindow.HandleSuggestionKeys` 处理，焦点始终留在地址栏。
- **触发规则**（`MainWindow`）：鼠标点进地址栏或 Ctrl+L → 列出最近访问；
  键盘输入（`IsAddressBarEditing` 为真）→ 按输入过滤；
  页面导航完成后回填地址**不弹**（那种文本变化不经过键盘事件）。
- **回路坑**：↓ 选中候选会把地址回填到输入框 → 触发 `TextChanged` → 若照常重建列表，
  高亮会被清掉，表现为「↓ 只能按一次」。解法在 `MainViewModel.UpdateSuggestions`：
  新文本等于当前高亮项地址时直接返回（见 §7）。
- **内容来源**：只查本地历史（`HistoryRepository.Query`）+ 一行「使用 XX 搜索 / 直接打开」
  （`Services/OmniboxSuggestions.cs`，纯函数、可单测）。**不外发输入内容**（没有网络搜索建议）。

---
