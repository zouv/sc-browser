# 任务 → 代码位置反查表（ARCHITECTURE.md 分册）

> 本文件是 [`../ARCHITECTURE.md`](../ARCHITECTURE.md) 的分册：§0.5 路由表里写「§2.x」的地方，
> 对应本文件的同名小节。主文件超 ~400 行时按 AGENTS §1.1 拆分，编号保持不变。
>
> **行号会随改动漂移——优先按「函数名 / 注释锚点」搜索。**
> **自愈规则**：搜不到 / 对不上 → 以 grep / 代码为准，顺手订正本表。

---

## 2. 按「我要做什么」反查代码位置（任务 → 入口）

> 行号会随改动漂移；**优先按「函数名 / 注释锚点」搜索**。
> **自愈规则**：搜不到/对不上 → 以 grep/代码为准，完成后顺手订正本表。

### 2.1 外壳 UI（`Views/MainWindow.xaml` / `Views/MainWindow.xaml.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 工具栏按钮外观 | `Themes/Controls.xaml` 的 `ToolbarButtonStyle` | 用 `DynamicResource` 引颜色令牌 |
| 标签条项的样式 | `Controls.xaml` 的 `BrowserTabButtonStyle` / `TabCloseButtonStyle` | `IsActive` 触发激活态背景 |
| 窗口按钮（最小化/最大化/关闭） | `Controls.xaml` 的 `CaptionButtonStyle` / `CloseCaptionButtonStyle` + `MinimizeButton_Click` / `MaximizeRestoreButton_Click` / `CloseButton_Click` | **位于标题栏命中区内，必须 `IsHitTestVisibleInChrome`**，见 §7 |
| 标签条交互（中键关 / 拖拽重排 / 拖出分窗 / 跨窗口合并 / 右键菜单） | `MainWindow.xaml.cs` 的 `TabButton_*` / `TabMenu_*`；菜单命令在 `MainViewModel`（`NewTabToRightCommand` / `DuplicateTabCommand` 等） | 拖拽是**自己捕获鼠标**的实时状态机（不用 `DragDrop`）：`ContinueDrag` / `ProcessStripDrag` / `HandOffToWindow` / `DetachToNewWindow` + `DragPhase`。**离开标签条立刻分窗、进入别的标签条立刻合并**，合并/分窗后由 `TakeOverStripDrag` / `TakeOverDetachedDrag` 在新窗口重新捕获鼠标；`_handOffPending` 防止预览+冒泡重复交接。落点 = `TabReorder.ResolveTargetIndex`，是否离开标签条 = `TabReorder.IsInStrip` |
| 窗口记忆（尺寸/位置） | `RestoreWindowPlacement` / `SaveWindowPlacement` + `Services/WindowPlacement.cs` | 只对主窗口生效；最大化存 `RestoreBounds` |
| 边框带（可视边框 + 缩放区） | `ApplyWindowedFrame`（`OnStateChanged` / `OnLoaded` 里调用） | **最大化时归零**，把视口还给页面，见 §7 |
| 「关于」对话框 | `Menu_About` → `Views/AboutDialog` | 版本取自程序集，路径取自 `AppPaths`；按钮用 `DialogButtonStyle` / `DialogPrimaryButtonStyle`（`Controls.xaml`） |
| 多窗口入口 | `OpenNewWindow` / `OpenNewIncognitoWindow` → `BrowserWindowManager.CreateWindow` | 见 §3.5 |
| 主菜单 | `MenuButton_Click` + XAML 里 `MenuButton.ContextMenu`（分组见注释）+ `Menu_*` 处理器 | 样式：`BrowserMenuStyle` / `BrowserMenuItemStyle` |
| 自绘窗口边框（无系统标题栏） | XAML 的 `WindowChrome.WindowChrome`（`CaptionHeight=38`）+ `NativeMethods.ClampMaximizedToWorkArea` | 见 §7 的两个坑 |
| 地址栏回车/失焦行为 | `AddressBar_KeyDown` / `AddressBar_LostKeyboardFocus` | 失焦会恢复显示当前页地址 |
| 「编辑态」判定 | `AddressBar_PreviewKeyDown` + `IsTextProducingKey` | **不能**用「获得焦点」判定，见 §7 |
| 键盘快捷键 | `MainWindow.TryHandleShortcut` | 单一 switch，按修饰键分组 |
| 全屏（F11 / 网页视频） | `ToggleFullScreen` / `SetUserFullScreen` / `OnTabFullScreenChanged` / `UpdateFullScreenState` / `EnterFullScreen` / `ExitFullScreen` / `SetChromeVisible` + `NativeMethods.FillMonitor` | 两个来源（用户 F11 / 网页 requestFullscreen）合流到 `_fullScreenApplied` |
| 链接悬停状态栏 | XAML 的 `StatusBar`（绑定 `ActiveTab.StatusText`）+ `BrowserTab.WireEvents` 的 `StatusBarTextChanged` | WebView2 自带状态栏关闭（`IsStatusBarEnabled=false`） |
| 站点信息 | `SecurityButton_Click` → `Views/SiteInfoPopup` | 独立顶层窗口（airspace）；按钮同样用 `DialogButtonStyle` / `DialogPrimaryButtonStyle` |
| 证书错误 | `BrowserTab` 的 `ServerCertificateErrorDetected` → `TabManager.CertificateErrorDetected` → `AskAboutCertificateError` | 只对顶层导航询问，子资源走默认 |
| 页内查找条（Ctrl+F） | `Views/FindBarWindow.xaml(.cs)`；`MainWindow.Find.cs` 的 `ShowFindBar` / `CloseFindBar` / `OnActiveTabChanged` / `ShowFindWindow` / `HideFindBarForBackground` / `RunFindAsync` / `StepFind`；工具栏放大镜 `FindButton_Click` | **独立顶层窗口**（Popup 拿不到键盘焦点）；`ShowFindWindow` 里先 `BringToForeground` 再延后聚焦；查找状态按 `tab.Id` 存于 `_findStates`；切后台/最小化隐藏，回前台按当前标签状态恢复 |
| 主菜单项 | `MenuButton_Click` + XAML 里的 `Button.ContextMenu` | |
| 窗口深色标题栏/圆角 | `NativeMethods.ApplyDarkMode` / `ApplyRoundedCorners`（由 `OnStateChanged` 在最大化时改直角） | |
| 应用图标 | `ScBrowser.csproj` 的 `ApplicationIcon` + `Assets/sc-browser.ico`；窗口用 `Icon="/Assets/sc-browser.ico"` | 设计源在 `docs/icons/`（改设计后重新导出 `.ico` 并覆盖 Assets 里那份） |
| 启动/退出钩子 | `MainWindow.OnSourceInitialized` / `OnLoaded` / `OnClosing` | 会话恢复与保存在这里 |

### 2.2 标签页（`Browser/BrowserTab.cs` / `Browser/TabManager.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 新增标签 | `TabManager.CreateAsync` | 顺序：入集合 →（可选挪位）→ 接线 → 创建引擎（除非 `deferLoad`）→ 激活 → 导航；`insertAt` 供「在右侧新增」 |
| 按需加载（惰性恢复） | `CreateAsync(deferLoad: true)` → 激活时 `Activate` → `TryEnsureEngineAsync`；引擎创建后 `BrowserTab.EngineAttached` → `IBrowserTabHost.AttachTab` | `BrowserTab.View` 可能为 `null`，所有调用点要空安全 |
| 未加载标签的地址/标题/缩放 | `BrowserTab.SetInitialLocation` / `ZoomFactor` / `_pendingUrl` / `_pendingZoom` | 引擎就绪后自动补导航与缩放 |
| 重开已关闭的标签 | `TabManager.ReopenLastClosedAsync` + `ClosedTabHistory` | `Close` 时入栈（Dispose 前取状态） |
| 标签排序 | `TabManager.Move` | 由标签条拖拽调用；下标语义见 §2.1 的 `TabButton_DragOver` |
| 无痕 / 普通差异 | `TabManager.SessionKind` / `IsIncognito`；`RecordHistory` 直接跳过 | 控制器选项见 `Browser/BrowserSession.cs` |
| 关闭标签的邻居选择 | `TabManager.Close` | 优先右邻，否则左邻（Chrome 语义） |
| 页内导航到内部页面 | `BrowserTab.Navigate` + `InternalPageRegistry.ToNavigableUri` | 未登记页面退回 newtab；宿主发起的导航放行 |
| 内部页面资源供给 | `BrowserTab.ServeInternalPages`（`WebResourceRequested`）+ `InternalPageRegistry.ResolveResourcePath` | scheme 注册在 `BrowserEnvironmentProvider.CreateAsync` |
| 网页→内部页面拦截 | `BrowserTab.ShouldBlockInternalNavigation`（NavigationStarting）+ `NewWindowRequested` 里的判断 | 放行宿主导航 / 内部页出发 / 前进后退刷新 |
| 地址栏显示地址的推导 | `BrowserTab.UpdateUrlFromCore` | 内部页面去尾部 `/`（`browser://settings`） |
| 崩溃 / 无响应提示 | `BrowserTab.OnProcessFailed` → `LastError`；错误条「重新加载」= `ReloadTabCommand` | 主进程退出时环境缓存由 `BrowserEnvironmentProvider` 作废 |
| 安全图标数据来源 | `BrowserTab.EnableSecurityEventsAsync` / `OnSecurityStateChanged` | CDP `Security.enable` + `securityStateChanged` |
| favicon 获取 | `BrowserTab.UpdateFaviconAsync` | `Core.GetFaviconAsync(Png)` |
| 历史入库时机 | `TabManager.RecordHistory`（订阅 `BrowserTab.NavigationFinished`） | 跳过 `browser://`/`about:`/`file://` |
| 新窗口请求转新标签 | `TabManager.WireTabEvents` 的 `NewWindowRequested` | |
| WebView2 设置项 | `BrowserTab.ConfigureSettings` | 注意 `AreBrowserAcceleratorKeysEnabled=false` |
| 页内查找（引擎） | `BrowserTab.StartFindAsync` / `FindNext` / `FindPrevious` / `StopFind` | 优先 `CoreWebView2.Find`；旧运行时自动退回 JS，见 §7 |
| 音频指示 / 静音 | `BrowserTab.IsPlayingAudio` / `IsMuted` / `ShowAudioIcon` / `ToggleMute` | 标签条上的扬声器按钮与右键菜单都走 `ToggleMuteCommand` |
| 后台标签休眠 | `TabManager.SuspendIdleTabs`（每分钟检查）/ `BrowserTab.SuspendAsync` / `Resume` | 30 分钟阈值；播放音频的不挂起；激活时自动唤醒 |
| 引擎退出后重建 | `BrowserTab.LoseEngine` → `EngineDetached`；`TabManager.ReloadTab` / `TryEnsureEngineAsync` | 状态保留、只重建 WebView2 |
| 标签迁移到新窗口 | `TabManager.ReleaseTab` / `Adopt` + `BrowserWindowManager.MoveTabToNewWindow` | 事件处理器经 `tab.Owner` 路由，迁移后不会回到旧宿主 |
| 打印 / 另存为 / 源代码 | `BrowserTab.Print` / `SavePageAsync`；`MainWindow.ViewSourceOfActivePage` | 运行时过旧时给出提示，不静默失败 |

### 2.3 内部页面（`Assets/ui/` + `Browser/WebMessageRouter.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 新增一个内部页面 | `InternalPageRegistry.Pages` 加一行（**含该页的消息前缀白名单**）+ 建 `Assets/ui/<name>/index.html` | 引用公共资源照旧写 `../common/x` |
| 新增页面可调用的宿主能力 | `WebMessageRouter.DispatchAsync` 加一个 `case` + 给需要的页面加白名单前缀 | 同时更新 §4 契约表；未登记即被拒 |
| 消息鉴权 | `WebMessageRouter.HandleAsync` → `InternalPageRegistry.IsMessageAllowed(args.Source, type)` | 非内部页面直接丢弃不回执；`Broadcast` 只发内部页面 |
| WebUI 侧发请求 | `browser-api.js` 的 `sc.send` / `sc.emit` / `sc.on` | |
| 宿主推事件给页面 | `WebMessageRouter.Broadcast` | 例：`downloads.changed`、`extensions.changed` |
| 取值助手（容错解析） | `GetString` / `GetBool` / `GetInt` / `GetLong` | 字段缺失时返回兜底值 |

### 2.4 数据存储（`Storage/`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 加表/加列 | `BrowserDatabase.Migrate`（`PRAGMA user_version`） | 每版一段升级；不要再往 `CREATE TABLE IF NOT EXISTS` 里塞新列 |
| 历史去重语义 | `HistoryRepository.RecordVisit` | 同 URL 累加 `visit_count` |
| 历史查询/搜索 | `HistoryRepository.Query` / `QueryTopSites` / `QueryVisits` | 有 `*Async` 版本；界面与消息路由只用 Async |
| 书签增删/切换 | `BookmarkRepository.Add` / `Remove` / `Toggle` / `Exists` / `AddFolder` / `Rename` / `Move` / `RemoveById` | 树结构：parent_id / sort_order / kind |
| 下载记录 | `DownloadRepository.Upsert` / `Find` / `MarkInProgressAsFailed` | 启动时把「下载中 / 已暂停」标为失败 |
| 下载暂停 / 继续 / 取消 | `DownloadManager.Pause` / `Resume` / `Cancel`（按 id） | WebView2 对暂停与取消都报 `UserCanceled`，意图要自己记 |
| 「下载前询问位置」 | `DownloadManager.PromptForSavePath` | 取消对话框 = `e.Cancel = true` |

### 2.5 扩展（`Browser/ExtensionService.cs` / `CrxPackage.cs`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 列出扩展 | `ExtensionService.ListAsync` | 平台只给 id/name/isEnabled；其余来自 `ExtensionManifest` 与 `Extensions/index.json` |
| 从目录安装 | `ExtensionService.InstallFromFolderAsync` | 先复制进受管目录 `<id>/<版本>`，再加载 |
| 从 `.crx` 安装 | `ExtensionService.InstallFromCrxAsync` → `CrxPackage.Read`（验签）→ `ExtractTo` | 公钥写回 manifest `key`；失败不落盘 |
| 定位 ZIP 偏移 | `CrxPackage.Read` / `FindZipOffset` | v2：16+key+sig；v3：12+header |
| 扩展 ID / 验签 | `CrxPackage.ComputeExtensionId` / `VerifyCrx3Proof` / `VerifyCrx2Signature` | ID = SHA-256(公钥) 前 16 字节映射成 a–p |
| 商店下载 | `ExtensionStoreClient.DownloadAsync` / `ExtractId` | 支持 Chrome / Edge 详情页链接与纯 ID |
| 扩展图标 | `ExtensionService.TryReadIconDataUrl` | 转 data URL，内部页面加载不了 file:// |
| 扩展弹窗 | `ExtensionPopupWindow.OpenAsync` + `ExtensionApiBridge.PolyfillScript` | `chrome.tabs` 兼容层只对弹窗生效 |
| 解包 | `CrxPackage.ExtractTo` → `ValidateEntries`（先校验再删旧目录） | **ZIP 段要切独立流**，见 §7；上限 `MaxEntries` / `MaxExtractedBytes` |
| 目录名规整 | `CrxPackage.DeriveFolderName` | 不得以 `_` 开头 |

### 2.6 设置 / 主题 / 会话（`Services/`）

| 我要改的东西 | 关键函数 / 锚点 | 备注 |
|---|---|---|
| 加设置项 | `AppSettings` 加属性（**必须给默认值**） + `SettingsService.Normalize`；设置页可改的还要进 `AppSettings.CopyPreferencesFrom` | 旧配置才能平滑升级；设置页保存是「合并偏好字段」不是整体替换 |
| 换肤 | `ThemeService.Apply` / `SwapDictionary` | 替换 `MergedDictionaries[0]` |
| 会话保存/恢复 | `SessionStore.Save` / `Load`；`BrowserWindowManager.SaveSession(closing)` / `EnsureAutoSave`（20s 定期、内容不变不写） | 落 `session.json`（原子写）；关最后一个标签会清会话 |
| 单实例 / 命令行网址 | `App.OnStartup`（`SingleInstance.Acquire` / `TryForward` / `StartListening`）→ `BrowserWindowManager.HandleForwardedArgsAsync`；主窗口自身网址在 `MainWindow.OnLoaded` | 锁/管道名带数据根目录哈希 |
| 下载接管 | `DownloadManager.OnDownloadStarting` / `OnProgress` | 进度按 1% 节流落盘 |

---
