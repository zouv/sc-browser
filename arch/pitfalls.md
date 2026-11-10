# 易踩的坑 / 设计取舍（分册）

> 由 ARCHITECTURE.md §7 拆出；路由入口见 ARCHITECTURE §0.5。

## 7. 易踩的坑 / 设计取舍

- **地址栏「编辑态」不能用「获得焦点」判定**：启动时会自动聚焦地址栏，若那时即算编辑态，
  首个页面加载完成后写回地址栏会被挡掉，结果地址栏恒为空。改为「用户敲下会改变文本的键」才置位
  （`AddressBar_PreviewKeyDown`）。
- **每标签一个 WebView2 是刻意的取舍**：换来切换零延迟与页面状态保留，代价是内存随标签数增长
  （与 Chrome 进程模型一致）。不要为了省内存改成单 WebView2 反复导航——会丢状态且切换卡顿。
- **airspace（空气墙）**：WebView2 是 HWND 控件，任何 WPF 元素都无法绘制在其之上。
  因此**浮层必须走独立顶层窗口**（主菜单用 `ContextMenu` 正是这个原因）。
- **链接悬停气泡是点击穿透的顶层窗口**（airspace）：必须设 `WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`，
  否则会吃掉链接点击、抢焦点；实现见 `NativeMethods.MakeClickThrough` + `Views/LinkStatusWindow`。
  **不要改回布局里的底部条**——那样悬停时会压缩网页视口。
- **标签压缩宽度**：`BrowserTabButtonStyle.MinWidth` 必须为 0；`TabStripMetrics.ResolveTabWidth` 按
  `TabScroller` 视口**扣除「+」新建按钮宽度**后均分并 `Math.Floor` 扣间距，否则总宽会撑出 `ScrollableWidth > 0`。
- **新标签插入位置**：所有 `NewWindowRequested`（Ctrl+单击后台、普通左键 `target=_blank` / `window.open` 前台）
  都不能每次插「发起标签 + 1」（顺序会反转）；新标签记 `Opener`，沿 `Opener == 发起标签` 的连续段向右跳过
  （`TabInsertion.RightOfOpener`）。`NewWindowRequested` 不带修饰键，`Ctrl` 只能同步读 `Keyboard.Modifiers` 判断。
- **ZIP 段必须切到独立流再解包**：中央目录偏移相对归档起点，从非零偏移起读会报 "End Of Central Directory ... 不对应"。
- **`CoreWebView2BrowsingDataKinds` 没有 `None`**：用 `(CoreWebView2BrowsingDataKinds)0` 作为空集起点。
- **`TotalBytesToReceive` 是 `ulong?`、`BytesReceived` 是 `long`**：算进度前先统一成 `long`。
- **MVVM 源生成器命名冲突**：`[ObservableProperty] _activeTab` 会生成 `OnActiveTabChanged` 部分方法钩子，
  自己再写同名方法会 CS0111；本项目把自定义方法命名为 `HandleActiveTabChanged`。
- **XML 注释里不能出现连续 `--`**：XAML 注释中的装饰线要用 `====`。
- **`IsKeyboardFocusWithin` 是只读依赖属性**：不能在 XAML 里绑定写回，只能走代码。
- **缩放带必须内缩**：WebView2 会吃掉落在它身上的鼠标事件，所以根布局留了 `Margin="2,0,2,2"`
  的内缩带（`MainWindow.xaml`），改布局时别把它吃掉；**最大化时归零**（`ApplyWindowedFrame`），
  否则白让出视口、宽页面会冒出滚动条。
- **任何网页都能 `chrome.webview.postMessage`**：WebView2 给所有顶层文档注入消息桥，并非只给内部页面。
  宿主能力必须按 `args.Source` 鉴权（`InternalPageRegistry.IsMessageAllowed`），页面传来的路径一律不信（下载按 id 查库）。
  内部页面因此用自定义 scheme `browser://<页面>/`（每页一个来源）而不是 `file://`；也不要换成 `https` 虚拟主机映射——
  扩展内容脚本的 `<all_urls>` 会匹配到它，扩展就能注入内部页面借道越权。
- **`CoreWebView2EnvironmentOptions.CustomSchemeRegistrations` 是只读且默认 null**：只能经构造函数
  `new CoreWebView2EnvironmentOptions(customSchemeRegistrations: …)` 传入，`.Add` 会空引用。
  资源过滤器优先用带 `RequestSourceKinds` 的重载，旧运行时退回旧重载（`BrowserTab.ServeInternalPages`）。
- **防目录穿越要「目录 + 分隔符」前缀比较**（`PathSafety.IsUnderDirectory`）：只比目录本身，
  `…\foo-evil\x` 也以 `…\foo` 开头，同级同前缀目录会被放行。
- **标签状态与引擎分离（惰性恢复）**：`BrowserTab` 构造不创建 WebView2，`View` 在首次 `EnsureInitializedAsync`
  时才创建并触发 `EngineAttached`（宿主据此挂树）。会话恢复只立即加载激活标签，其余激活时才加载。
  **`View` / `Core` 可能为 `null`**，导航与缩放会记成「待处理」；挂树必须在 `EnsureCoreWebView2Async` 之前，
  所以只能由 `EngineAttached` 触发。顺带好处：后台标签不再重复写历史。
- **`ClearValue(VisibilityProperty)` 会把 XAML 绑定一起清掉**：全屏收起错误条 / 书签栏时不能给它们赋本地值，
  否则绑定失效、退出全屏后一直可见。正确做法是 `MainWindow.ChromeVisible` 依赖属性 + `TextAndChromeVisibility` 多值绑定。
- **先移除活跃项再发变更通知**：`DownloadManager.OnProgress` 完成时若先 `Changed` 再 `_active.TryRemove`，
  界面在通知里读到的 `ActiveCount` 还是旧值，工具栏下载指示器就永远不消失。
- **全屏用 Win32 铺满显示器**：`WindowState=Maximized` 只铺工作区，不能当全屏用。`NativeMethods.FillMonitor`
  按显示器完整矩形 `SetWindowPos`；退出时要把 `Left/Top/Width/Height` 设回去（Win32 挪过窗口后 WPF 值已不同步），
  并把 `WindowChrome.CaptionHeight` 归零，否则点网页顶部会变成拖窗口。
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
  1. **最大化会被撑出屏幕**：系统按「带边框窗口」算最大化尺寸，溢出屏幕若干像素、边缘内容被裁掉。
     在 `WM_GETMINMAXINFO` 里钉到显示器工作区（`NativeMethods.ClampMaximizedToWorkArea`），别用补 Margin 的写法。
  2. **标题栏区域内点击会变成拖窗口**：`CaptionHeight` 覆盖的标签条里，按钮/标签必须标
     `IsHitTestVisibleInChrome="True"`（见 `Controls.xaml`）。可用 `WM_NCHITTEST` 的返回值验证。
- **绑定的路径取不到值时，目标属性会退回类型默认值**：`Visibility` 的默认值是 **Visible**，所以
  `ActiveTab` 为 `null` 的瞬间（新窗口首个标签还没建好）红条会闪一下。凡是绑定控制显隐的地方，
  都要显式写 `FallbackValue=Collapsed`（必要时加 `TargetNullValue`）。
- **WebView2 的默认底色也要跟着主题走**：`WebView2.DefaultBackgroundColor` 默认是**白色**，
  深色主题下新建标签/新窗口会在页面画出来之前先闪一下白底。见
  `WebTheme.DefaultBackground` + `TabManager.ApplyWebTheme`（与颜色方案一起设）。
- **标签拖拽是实时状态机，不是「松手结算」**：`MainWindow.Tabs.cs` 的 `DragPhase` 只有两态——
  `InStrip`（在标签条内重排）与 `Detached`（已分窗、窗口跟着鼠标走）。
  指针**离开标签条立即分窗**、**进入另一个窗口的标签条立即合并**；合并后回到 `InStrip`，
  因此不松手也能连续「分窗 → 合并 → 再分窗」。要点：
  1. **单标签窗口不能把拖标签写成 `DragMove()`**——那等于拖窗口，永远走不到分窗 / 合并。
     移动窗口用标签条空白处（WindowChrome 的标题栏命中区）。
  2. **交接（hand-off）要重新捕获鼠标**：标签迁到另一个窗口后，标签按钮会被重新生成；
     `MoveTabToWindow` / `MoveTabToNewWindow` 在 Adopt 后 `UpdateLayout()`，再由
     `TakeOverStripDrag` / `TakeOverDetachedDrag` 找到新按钮并 `CaptureMouse`，否则后续
     MouseMove / MouseUp 全丢。**交接前必须置 `_handOffPending`**：同一次移动会被
     「按钮 Preview」和「窗口冒泡」各处理一次，不拦住会一次拖出建出两个窗口。
  3. **跨窗口命中测试不要拿 WPF DIP 直接相加**：统一 `PointToScreen` 得物理屏幕点，
     目标窗口再用 `PointFromScreen` 换回自己的 DIP（`ContainsTabStripPoint` / `TabIndexAtScreenPoint`）。
     「是否离开标签条」必须**宽高都判断**（`TabReorder.IsInStrip`，垂直容差 15px 对齐 Chrome 的
     `kVerticalDetachMagnetism`）。
  4. **Detached 跟随窗口要用 `GetCursorPos` 重新换算**，不能拿 `e.GetPosition(this)` 的差值：
     同一移动被预览/冒泡处理两次时，后者会把窗口位移叠加两次，越拖越偏。
     分窗摆放再用「标签按钮在窗口里的实际左边缘」反向校正一次，保证指针落在同一抓取点。
  5. **换位要在模型移动后 `UpdateLayout()` 让槽位几何立即落地，再算渲染偏移**（`UpdateDragPosition`）：
     `ObservableCollection.Move` 只**同步**重排面板 Children，Arrange 要等布局帧；
     不落地就读槽位，会得到「新顺序 + 旧坐标」的错位几何，标签宽度不等时每次换位都跳一下再跳回
     （用户报告的飘闪根因；`TabStripLayoutTests` 机械验证了这两个前提）。
     偏移公式 = 指针X − 抓取偏移 − 被拖标签**落地后所在槽位**的左边缘；槽位几何必须取
     panel 子元素（容器）的偏移——元素是 DataTemplate 里的 Button，它相对 ContentPresenter
     的偏移恒约 0，取它算会飞。
  6. **其他标签的让位是补偿衰减动画**（`TabStripSlideAnimator`，对齐 Chrome 的 BoundsAnimator）：
     布局槽位即时变化，每个容器挂 `TranslateTransform` 补偿 =「旧补偿 + 旧位置 − 新位置」
     （`TabReorder.SlideCompensation`），按 τ≈66ms 指数衰减到 0 后摘变换；
     中途再换位把残余补偿叠进去即可自然重定向。条内松手同样用补偿衰减做「滑回槽位」（`BeginGlide`）。
     换位带 16px 水平滞后（Chrome `kHorizontalMoveThreshold`），防指针停在槽位边界时来回抖动。
  7. **标签溢出时拖到条两端自动滚动**（`ScrollStripTowardsElement`，判据对齐 Chrome 的
     `TabDragScrollHandler`：被拖标签视觉边缘越过 `TabScroller` 视口边缘就滚）。
     滚动改变 panel 坐标映射，滚完要把滚动增量加回指针的 panel 坐标再算重排——
     偏移公式里滚动量自洽抵消，标签仍恒贴鼠标。
- **WebView2 SDK（1.0.4258）缺两个 Chrome 类能力**：① `CoreWebView2.StartDownloadAsync` 不存在——
  「重新下载」用**后台标签导航**触发下载接管，`DownloadStarted` 事件在下载开始后关掉临时标签
  （15 秒无下载则留给用户）；② `CoreWebView2Profile` 没有按时间范围的清除——清除浏览数据对话框的
  时间范围只作用于历史（仓储 `ClearSince`），WebView 数据一律全清（对话框里如实提示）。
- **代码里打开的右键菜单必须延迟到输入事件处理完再弹**：在 `MouseRightButtonUp` 里同步
  `menu.IsOpen = true`，弹出层会在同一输入事件处理期间抢鼠标捕获——菜单看得见、点不动
  （工具栏「自定义工具栏」实际踩过）。统一走 `MainWindow.Toolbar.cs` 的 `OpenContextMenu`
  （`Dispatcher.BeginInvoke(Input 优先级)`），书签栏右键菜单同理。`BrowserMenuItemStyle`
  模板必须给 `IsChecked` 画对勾，否则勾了也看不出变化。
- **书签栏的可见性是双条件绑定**：`IsBookmarksBarVisible`（设置开关或新标签页）∧ `ChromeVisible`（全屏）。
  两个条件用 MultiBinding（`BooleansAndToVisibility`）——全屏逻辑绝不能给 Visibility 赋本地值，
  否则把绑定清掉（§7 的 `ClearValue` 坑），退出全屏后书签栏永远可见。
- **导航失败提示要延迟落地**：WebView2 冷启动、被后续导航取代的请求，常在「失败」后几毫秒内就成功；
  立刻挂红条的表现是「闪一下」（新开窗口最容易看到）。`BrowserTab.ReportNavigationError` 的做法是
  延迟 700ms + 版本号作废（期间任何新导航/成功导航都会作废它），把 `OperationCanceled` 直接排除，
  并且**标签首次导航失败会自动重试一次**（冷启动偶发失败重试即好，真实故障重试仍失败、照常报错）。
- **深色「影响网页」要顺着站点走**：把 `CoreWebView2Profile.PreferredColorScheme` 设成 Dark/Light/Auto，
  让页面的 `prefers-color-scheme` 随应用主题变化即可——声明了深色样式的站点自动变深，没声明的保持原样。
  **不要**用 CDP 强制反色：会把排版、图片、图表拧坏。内部页面（`browser://*`）的 `common/base.css`
  用的是同一个媒体查询，因此一并跟随，无需另推消息。
- **全应用只有一个 WebView2 profile**：颜色方案是 profile 级设置，设一次即影响所有标签，
  但新标签仍会显式再设一次（见 `TabManager.ApplyWebTheme`），避免依赖「之前一定设过」的隐含前提。
- **页内查找要兼容旧 WebView2 运行时**：`CoreWebView2.Find` 需要 `ICoreWebView2_28`，
  本机 WebView2 131 没有该接口，访问会抛 `NotImplementedException`。`BrowserTab` 因此在
  原生不可用（或启动失败）时退回 JS：用 `TreeWalker` 收集文本节点的匹配区间，再用
  `CSS.highlights` 高亮，状态挂在 `window.__scFind` 上。JS 路径只覆盖主文档、匹配不跨文本节点，
  是旧运行时的降级方案；升级运行时后会自动走原生路径。**不要为了「只留一条实现」删掉兜底。**
  配套行为：Ctrl+F 首次打开时把页面选区预填进搜索框（`BrowserTab.GetSelectedTextAsync`，上限 150 字符，
  Chrome 同款）；JS 路径还会把匹配位置按文档比例画成滚动条右侧的刻度条（原生 Find 没有这个能力）。
- **输入接管与页内查找的边界处理**（滚轮 / JSON 查看器 / Range 滚动）见 [`arch/flows.md`](arch/flows.md) §3.8。
