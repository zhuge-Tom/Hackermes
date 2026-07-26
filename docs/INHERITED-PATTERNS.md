# 从 ZeroFall 继承的设计模式(附源码出处)

参考项目根目录:`G:\Oofo\zerofall`。下表记录实施时可直接查阅的原始实现位置,避免重复踩坑。路径均相对 `G:\Oofo\zerofall\`。

---

## 一、直接移植(改名即用)

### 1. 两段式模块契约
- `src\ZeroFall.Base\IModule.cs:6-10` — 只有 `RegisterServices(IServiceCollection)` 和 `Initialize(IServiceProvider)`
- `src\ZeroFall.App\AppModuleBootstrap.cs:17-41` — 硬编码模块数组,两趟循环(先全部 Register,`BuildServiceProvider` 后再全部 Initialize)
- 模块顺序有语义:Core 提供核心服务 → Dock 提供注册表 → 功能模块依赖两者
- 模板参考 `src\ZeroFall.Terminal\TerminalModule.cs:32-63`

### 2. 事件总线
- `src\ZeroFall.Base\Events\EventBus.cs` — `Dictionary<Type, List<Delegate>>` + lock,**同步派发**(在调用者线程直接跑 handler,`:45-48`),发布前 `ToList()` 拷贝规避重入
- `src\ZeroFall.Base\Events\EventSubscription.cs:26-32` — `SubscribeDisposable` 扩展
- **衍生约定**:任何可能从后台线程发布的事件,订阅方须自行 `Dispatcher.UIThread.CheckAccess()` 再 `Post`。样板见 `src\ZeroFall.Browser\BrowserFeatureRegistrars.cs:87-96`
- 事件命名分层:`*RequestedEvent`(意图,可拒绝)/ `*ChangedEvent`(状态已变)/ `*Event`(事实通知);可取消的用 class 而非 record 以携带可写 `Cancel`(`src\ZeroFall.Platform\Events\PlatformEvents.cs:31-35`)

### 3. ViewModelBase 自动退订
- `src\ZeroFall.Base\Mvvm\ViewModelBase.cs:8-40` — `ObservableObject` + `IDisposable`,`SubscribeEvent<T>()` 把订阅收进 `_subscriptions`,`Dispose` 统一退订
- **这是防 EventBus 强引用泄漏的核心约定**,所有 VM 订阅必须走它

### 4. PersistTabControl —— Tab 保活(最高优先级移植项)
- `src\ZeroFall.Dock\Controls\PersistTabControl.cs`(660 行,类注释在 `:21-27`)
- 解决 Avalonia `TabControl` 切页卸载可视树、导致 WebView2/PTY 被销毁的问题
- 双模式:`Reloadable` 走原生 `PART_SelectedContentHost`;`NonReloadable` 用 `INonReloadableTabShell` 占位 + `PART_NonReloadableOverlay` 叠层保活,切 Tab 只改显隐
- 声明式选择:`src\ZeroFall.Dock\Controls\TabContent.cs` 的 `TabContent.Reloadable/NonReloadable`
- 释放:`ITabContentReleasable` + `src\ZeroFall.Platform\Services\TabContentLifetime.cs`,注释警告"勿在 `DetachedFromVisualTree` 中释放"(`:7-8`)
- **配套陷阱**:底部面板折叠时只改行高不改 `IsVisible`(`src\ZeroFall.Dock\ViewModels\DockLayoutViewModel.cs:566-568`)

### 5. Tab 壳/内容两阶段懒物化
- `src\ZeroFall.Dock\ViewModels\DockLayoutViewModel.cs:140-148` `CreateTabShell` — 启动只建标题+图标,`Content = null`
- `:245-260` `EnsureTabMaterialized` — 选中/可见时才由 `CreateTab()` 工厂构造真 View
- `:347-359` + `:582-611` — `IsDefaultVisible = false` 的注册项留在 `_lazyTabRegistrations`,等 `SwitchDockTabRequestedEvent` 才补壳
- `:89-93` — `ApplyRegistrationsAsync` 每 3 个 Tab 让一帧,避免启动卡顿

### 6. AI 工具源生成器
- 生成器:`src\ZeroFall.AiToolGen\AiToolSourceGenerator.cs`(`IIncrementalGenerator`)
  - `:39-58` 扫描带 `AiToolAttribute` 的方法(按 attribute **名字字符串**匹配,不做符号绑定)
  - `:75,112` 生成 `AiToolRegistration_{ClassName}.Register(registry, serviceProvider)`
  - `:179-189` `MapTypeToJsonSchema` —— **Hookmes 要改进这里**:ZeroFall 遇到非基元类型一律 fallback 成 string
  - `:163-170` Required 规则:有 `[ToolParam(Required=false)]` 或有默认值即非必填
  - `:191-291` `BuildExecutor` 生成执行器闭包:`GetRequiredService` → 逐参数绑定 → 调用 → 包装 `ToolCallResult`
- 特性:`src\ZeroFall.Base\AiTools\AiToolAttribute.cs`
- Schema 输出:`src\ZeroFall.Base\AiTools\ToolDefinition.cs:13-80` —— 手写字符串拼 JSON(零反射,AOT 友好),参数按 Ordinal 排序保证请求体稳定(利于 prompt cache)
- 三条注册通道汇入同一 registry:生成器 / `RegisterFromCatalog`(手写 schema、需确认的工具)/ `RegisterOpenAiToolJson`(MCP 动态 schema),见 `src\ZeroFall.Base\AiTools\AiToolRegistry.cs:14-33`
- 引用方式:`OutputItemType="Analyzer" ReferenceOutputAssembly="false"`(`src\ZeroFall.Terminal\ZeroFall.Terminal.csproj:20`)

### 7. Web 前端烧进 C#
- `src\ZeroFall.AiPanel\ai-chat-web\vite.config.js:6` — `viteSingleFile()` + `assetsInlineLimit: 1e8` + `cssCodeSplit:false` + `format:'iife'`
- `src\ZeroFall.AiPanel\ai-chat-web\generate.mjs:6-31` — 读 `dist/index.html`,剥 `type="module"`/`crossorigin`,把 `<script>` 移到 `</body>` 前,写成 C# **raw string literal**(用 8 个引号作分隔符,`:19`)
- 产物 `src\ZeroFall.AiPanel\Views\AiChatHtml.g.cs` **提交进 Git** → 构建机无需 Node
- 运行时 `src\ZeroFall.AiPanel\Views\AiChatWebView.cs:449` `NavigateToString(...)`,失败降级为写临时文件再 `Navigate(fileUri)`(`:460-489`)

### 8. 设置持久化
- `src\ZeroFall.Platform\Services\SettingsServiceImpl.cs`
  - `:169-185` 三级候选目录:`%LocalAppData%` → `%AppData%` → `%Temp%`
  - `:139-153` 写入:`VerifyWritable` 探针 → 备份 `.bak` → 写 `.tmp` → `File.Move(overwrite)` 原子替换
  - `:59-75` 读取失败自动 fallback 到 `.bak`
  - `:190-213` `NormalizeAndMigrate` 在 Load/Save 两侧都跑 —— **"迁移逻辑放在序列化边界"是个好约定**
- 源生成序列化上下文 `src\ZeroFall.Platform\Serialization\AppSettingsJsonContext.cs`(AOT 友好,无反射)

### 9. 设置界面宿主
- `src\ZeroFall.Settings\Helpers\SettingsTabsHost.cs`
  - `:102-122` 按需构建(SelectionChanged 时才 `CreateView()`)
  - `:124-147` 关闭即释放(逐个 `Dispose()` VM 并置空 `DataContext`,注释直言为避免 EventBus 强引用泄漏)
  - `:84-99` 保存契约 `ISettingsSaveable { TrySave(); LastSaveError }`
- `src\ZeroFall.Settings\Helpers\SettingsBindingHelper.cs:12-19` — 靠 `topLevel.Focus()` 触发 LostFocus 提交待编辑绑定,**巧妙的小技巧**
- 注意 ZeroFall 的保存按钮只保存当前选中页(`:67`),Hookmes 应改为保存全部

### 10. 图标防御式取值
- `src\ZeroFall.Platform\Services\IconHelper.cs:9-13` — 从 `Application.Current.Resources` 取 `StreamGeometry`,注册处只写字符串 Key
- `:16-18` `GetBrowserIcon` 依次尝试 4 个 key,兜住 Semi 12 的破坏性改名 —— 值得复用

### 11. DOM → Markdown
- `src\ZeroFall.HtmlToMarkdown\` 全仓唯一零依赖纯净库
- `DomToMarkdownConverter.cs:6-29` 类注释是关键洞察:**不解析 HTML 字符串,直接消费 CDP `DOM.getDocument` 的 `JsonElement` 节点树**。理由——走 HTML 解析器会反转义实体导致标签注入(实例:百度把 CSS 藏在 `<textarea>` 文本节点里)。顺带拿到 AOT 友好与 iframe/shadow DOM 穿透
- `DomNode.cs:112-126` — `JsonElement` 的 readonly struct 适配器,统一 CDP 扁平 `attributes:[k1,v1,...]` 数组、`children`、pierce 模式的 `contentDocument`
- `HiddenElementDetector.cs:8` — 注释说明为性能**故意不调 `DOM.getBoxModel`**,改用 style 属性启发式
- `HtmlToMarkdownOptions.cs` — `SkipTags` / `MaxOutputCharacters` / `MaxTableRows`(防 token 爆炸)/ `HtmlUrlPolicy.WebOnly()` 默认禁 `data:` 与 `javascript:`

---

## 二、参考实现(Hookmes 需要改动)

### 12. WebView2 生命周期与并发闸门
这块是 ZeroFall 踩坑最多的地方,**必须理解后再动**:

- `src\ZeroFall.Platform\Services\WebView2CreationCoordinator.cs:41` — 全局互斥 `WaitForInitAsync()`,同一时刻只允许一个 WebView2 初始化,`ArmInitRelease` 是 20s 看门狗
- `src\ZeroFall.Base\Diagnostics\BrowserUiGate.cs:12` — 全局 `SemaphoreSlim(1,1)`,因为浏览器 WebView 与 AI 聊天 WebView **共用 UI 线程,交叉调用会挂死**
- `src\ZeroFall.Browser\Views\BrowserTabView.axaml.cs`
  - `:101-112` 创建门禁:`StartupPerformance.IsLayoutReady` + 宿主 Bounds > 8px
  - `:581-589` 适配器就绪靠**轮询 + 事件双保险**(150/400/800/1500/3000/6000ms),因为 `AdapterCreated` 事件不可靠
  - `:265-276` `ResolveTabViewModel()` 双路解析 —— TabControl 模板会用 Dock 标签项 VM 覆盖 DataContext,页面 VM 需额外存一份在 `Tag` 上。**易踩的坑**
  - `:239-263` `EnvironmentRequested` 事件里设 `UserDataFolder` 与 `AdditionalBrowserArguments`
  - `:570` 取原生句柄:`TryGetPlatformHandle() is IWindowsWebView2PlatformHandle { CoreWebView2: not 0 }`
- `src\ZeroFall.Platform\Services\UiThreadBridge.cs:10` — 一律 `Dispatcher.UIThread.Post` + `TaskCompletionSource`,**刻意不用 `InvokeAsync` 以避免死锁**

### 13. CDP 调用层(Hookmes 要大幅扩展)
- `src\ZeroFall.Browser\ComInterop\WebView2ComVTable.cs` — 手写 vtable 槽位:`:83` `CallDevToolsProtocolMethod`(槽 36)、`:93` `ExecuteScript`(槽 29)、`:127` `OpenDevToolsWindow`(槽 51)
- `src\ZeroFall.Browser\ComInterop\WebView2NativeWrapper.cs`
  - `:127` `AttachEvents()` 挂 `DocumentTitleChanged` / `WebResourceResponseReceived` / `WebResourceRequested` / `ServerCertificateErrorDetected` / `FaviconChanged`
  - `:425` `ExecuteScriptAsync`,默认 10s 超时
  - `:199-231` 关 SmartScreen 信誉检查
- `src\ZeroFall.Browser\Services\CdpBridgeImpl.cs`
  - `:14` 单例 + `tabId → wrapper` 字典
  - `:159-171` `RunOnUiThreadWithScriptGateAsync` —— 所有 CDP 调用必经的两道闸
  - `:16` 逃生阀:环境变量可完全禁用原生 CDP
  - `:173` `CaptureScreenshotAsync` → `Page.captureScreenshot`
- `src\ZeroFall.Browser\Services\CdpPageContentExtractor.cs` — `DOM.enable` → `getDocument`(depth=-1, pierce=true)→ 喂 `DomToMarkdownConverter`。**纯 CDP DOM 路径,不注入页面 JS**,专为规避反调试站设计
- `src\ZeroFall.Browser\Services\CdpEvaluateParser.cs` — 解析 `Runtime.evaluate` 返回值与协议错误
- `src\ZeroFall.Browser\Services\CdpDocumentLoadWaiter.cs` — 文档就绪等待

**Hookmes 必须新增而 ZeroFall 完全没有的**(均为现成能力,非技术未知):

| 能力 | 说明 |
|---|---|
| `GetDevToolsProtocolEventReceiver` | **WebView2 官方 API**(`ICoreWebView2_11` 起)。ZeroFall 未使用,故无 vtable 槽位可抄,需自行确认槽位序号。receiver **按事件名逐个创建**,handler 收到 `ParameterObjectAsJson` + `SessionId`,需要引用计数管理避免重复订阅 |
| `Page.addScriptToEvaluateOnNewDocument` | 文档级预注入。走 CDP 而非 WebView2 的 `AddScriptToExecuteOnDocumentCreated`——后者与 `NavigateWithWebResourceRequest` 同用时不生效(已知平台缺陷),且不支持 `worldName` 参数 |
| `Runtime.addBinding` + `Runtime.bindingCalled` | 页面→宿主通道。单次调用有长度限制,大 payload 需分片重组 |
| `Input.dispatchMouseEvent` / `dispatchKeyEvent` / `insertText` | 真实输入事件。与 JS `.click()` 的区别是能触发完整事件序列与浏览器默认行为 |
| `Overlay.highlightNode` / `setInspectMode` | 元素拾取。注意 ZeroFall 为性能故意避开 `DOM.getBoxModel`(`HiddenElementDetector.cs:8`),但拾取器必须用它 |
| `Fetch` 域 | 请求拦截与 Mock。这是取代 ZeroFall 整套 Fluxzy MITM 代理的方案——零证书安装 |
| `Page.createIsolatedWorld` | 隔离执行上下文。**注意**:网络 hook 必须在主世界才有效,只有录制/拾取类逻辑能放隔离世界。详见 `ARCHITECTURE.md` 第五节 |

> 注意 CDP 的一个特性:WebView2 **按调用顺序派发但可能乱序完成**。每个请求必须自带 id 并用 `TaskCompletionSource` 配对。

### 14. AI 流式渲染
- `src\ZeroFall.AiPanel\Services\MarkdigBlockStreamer.cs:28-42` — SSE delta 进 buffer,**只有遇到换行才**把完整 Markdown 块渲染成 HTML 块吐出,未成块的尾巴作为 `tailMarkdown` 原样下发
- 前端 `ai-chat-web\src\components\ChatMessageItem.vue:300-301` — `v-for block ... v-html="block.html"` + `<div class="stream-tail">{{ tailMarkdown }}</div>`
- **"稳定块用 HTML,活跃尾用纯文本"** —— 避免流式过程中反复重排 DOM,也避免半截 Markdown 语法闪烁
- 流结束后后台线程全量重渲染一次再 `replaceBlocks` 覆盖(`AiChatWebView.cs:1290-1347`)

### 15. WebView 消息协议
- C# → JS 不用 `PostWebMessage`,而是拼脚本调全局桥(`AiChatWebView.cs:2754-2757`):
  ```js
  (function(){var c=window.zerofallChat;if(!c)return 'no-bridge';c.receive({json});return 'ok';})()
  ```
  返回 `'no-bridge'` 时命令自动重新入队(`:2765-2766`)
- 就绪探测:轮询 `window.aiChatReady` + `.chat-root` DOM,最多 60 次 × 100ms(`:499`)
- JS → C#:`invokeCSharpAction(JSON.stringify({...}))`,宿主侧 `OnWebMessageReceived`(`:621-740`)
- 脚本体积限流 `MaxInlineScriptJsonChars = 384KB`(`:67`),超限在构建期切成多条命令(`:1630`),单条消息超限则降级为 `deferred` 壳、前端按需 hydrate
- 串行分发队列 + generation 版本号(`:2667-2724`),会话切换时 `_dispatchGeneration++` 废掉在途命令

### 16. AI 上下文管理
- `src\ZeroFall.AiPanel\Services\ChatSessionApiPayloadBuilder.cs:20-57` — **API 载荷从 SQLite 重查而非从 UI 内存拼**。这使撤销、压缩、子 Agent、只读会话都成为同一份数据的不同投影
- `src\ZeroFall.AiPanel\Services\ChatContextCompressionService.cs:238-370` — UI 气泡 → OpenAI 消息的归并规则(reasoning-only 气泡并入下一条;正文紧跟工具调用时并成同一条 assistant 的 content preamble;连续工具调用合成一条 `tool_calls[]` + N 条 `role:"tool"`)
- `ToolResultContextProjection.ProjectForApi` `:362-367` — 大工具输出替换成 `@tool_result:{messageId}` 引用,全文存 runtime store,按模型上下文长度决定投喂量
- `ChatMessageTokenEstimator` — token 估算带缓存,避免为估算而构建整棵 JSON 树

### 17. OpenAI 兼容 API 的现实兼容处理
`src\ZeroFall.AiPanel\ViewModels\AiPanelViewModel.cs:3109-3483` 这段值得整体细读,处理了大量网关差异:
- `:3577-3585` 兼容 `data: {}` / `data:{}` / 大小写 / 首尾空白
- `:3132-3136` **非 SSE 兜底** —— 某些网关声明 `stream=true` 却返回整块 JSON
- `ChatCompletionStreamText.AppendSuffix:12-38` — 某些网关每帧发全量 content 而非 delta,只追加未见过的后缀
- `:3416-3422` tool_calls 用 `Dictionary<int, ToolCallBuilder>` 按 index 聚合,**流完全结束后**才汇总(`finish_reason` 可能早于 arguments 末片到达)
- `:3497-3523` **运行时能力探测缓存** —— 首次因 `stream_options` 报 400/422 则自动去掉该字段重试一次,并记住该端点不支持
- `ChatCompletionRequestParams.ApplyThinking:12-21` — 同时下发 `enable_thinking`(Qwen)和 `thinking:{type:"enabled"}`(DeepSeek)

### 18. MCP 桥接
- `src\ZeroFall.AiPanel\Services\McpClientConnectHelper.cs:15-59` — transport 支持 `stdio` / `http` / `sse` / `streamable-http`
- `:94-111` HTTP 系传输前把应用代理写进 `HTTP_PROXY`/`HTTPS_PROXY` 环境变量 —— 因为 SDK 的 transport 不接受外部 HttpClient
- `src\ZeroFall.AiPanel\Services\McpAiToolBridge.cs`
  - `:102` 工具名重整为 `mcp__{serverSlug}__{toolName}`,重名加后缀
  - `:140-175` **直接透传 MCP 的 `tool.JsonSchema` 原始 JSON** 作为 `function.parameters`,不做二次映射 → MCP 工具可表达比 `[AiTool]` 生成器更复杂的 schema
  - `:45-67` 刷新时先 `UnregisterPrefix("mcp__")` 清空旧的

### 19. 终端
- `src\ZeroFall.Terminal\ViewModels\TerminalViewModel.cs:55-120` — shell 选择:Windows 走 `%ComSpec%`(含 WOW64 `Sysnative` 兜底),交互参数按 shell 分流(`bash --login -i` / `cmd /K` / `pwsh -NoLogo -NoExit`)
- `src\ZeroFall.Terminal\ViewModels\TerminalHostViewModel.cs:177-204` — 每会话一个 `TerminalControl` 实例 + 一个 Dock 内层 Tab,标 `NonReloadable`(否则 PTY 被杀)
- `:55-59` **反向注入模式** —— 瞬态 VM 在构造时把自己挂到 4 个单例服务上,让 AI 工具(单例)能触达 UI 树
- `src\ZeroFall.Terminal\Services\TerminalTranscriptService.cs`
  - `:951-979` 两张表。**行模型是核心**:不存原始 PTY 字节流,存规范化的"行",每行带 `kind`(CommandInput/Output)、`phase`(Idle/Executing)、`command_id` → AI 可直接 `WHERE command_id = N` 取某次命令输出
  - `:95-119` 写入先 `TerminalAnsiText.Strip` 去 ANSI,再切完整行,支持 `RewindLastLine`(处理 `\r` 覆盖)
  - `:806-812` 批量落盘 + 400ms 去抖 + UPSERT
  - `:177-231` `ReplaceTailFromScreen` 定期用 XTerm buffer 末 32 行覆盖 transcript 尾部,修正 diff 捕获偏差
- `src\ZeroFall.Terminal\Services\TerminalCommandWait.cs:63-105` — **命令结束检测的多路判据**,比固定 sleep 或纯正则可靠得多:
  - `_cmd_end_` 哨兵标记(`:18`),且排除命令行自身的 echo 回显(`:108-120`)
  - 提示符正则,Windows 默认 `(?:[A-Za-z]:[/\\][^>\r\n]*>|PS [^>\r\n]+>)\s*$`(`:21-22`)
  - phase 状态机:见过 `Executing` 后转 `Idle`
  - `LooksLikeFullScreenTui` 避免 vim/top 里误判提示符
  - 硬上限 30s;返回值带 `secondsSinceLastOutput` 让模型自己决定是否再 poll
- `src\ZeroFall.Terminal\Services\TerminalBufferChangeCapture.cs:7-9` — **重要警示**:注释直言"官方 Iciclecreek 无 PTY 原始输出事件",作者用 `BufferChanged` 事件 + 全量 buffer 快照做**字符串前缀 diff** 来近似输出流。这是整条"AI 读终端"链路的数据源头,也是大量补偿逻辑存在的根因。Hookmes 若对终端可靠性要求更高,应考虑直接封装 ConPTY 而非沿用此妥协

### 20. 表格与大数据量
- `src\ZeroFall.Base\Data\IDataProvider.cs:34-42` — 三种显示模式:`VirtualScroll`(滚动接近尾部时增量 `LoadMore`)/ `UserPaged`(显式分页)/ `LiveCollection`(内存环形缓冲,上限裁剪)
- 核心抽象是 `GetPageAsync(offset, limit)`,SQLite/MySQL/内存三种实现
- `src\ZeroFall.DataTable\ViewModels\DataTableViewModel.cs:345-347` — 预加载阈值 50 行
- `:309-312` LiveCollection 超上限丢弃最旧
- `src\ZeroFall.DataTable\Views\DataGridScrollChrome.cs:12-34` — 纯 workaround:Semi 主题在模板内部打开 `ScrollBar.AllowAutoHide`,只设外层 ScrollViewer 无效,必须遍历可视树逐个关掉,且需"延迟多拍"才稳
- 流量表的实践(可借鉴给网络面板):固定容量环形窗口 3000 条 + SQL 侧列投影(列表查询不拉 body/BLOB)+ 选中行才惰性补水 body + 行 evict 后主动 `ReleaseEntryPayload()` 清缓存 + 批量入 UI(25/批,队列积压时切 100/批 burst 模式)

### 21. 语法高亮
- `src\ZeroFall.SqlEditor\CodeSyntaxHighlighting.cs`
  - `:62` 亮色直接用 AvaloniaEdit 内置定义
  - `:68-84` + `BuildXshd:169-195` **暗色自己拼 XSHD XML**(内置浅色规则在深色背景不可读)
  - 按 `{d|l}:{language}` 双键缓存,主题切换时 `InvalidateCache()`
  - `:86-88` 一个跨语言复用的 `UrlRule` 片段
- 注意 ZeroFall 的 AvaloniaEdit 用得很浅:**未使用** `CompletionWindow` / `SearchPanel` / `FoldingManager` / TextMate。Hookmes 的 JS 脚本编辑器需要补上补全与查找

### 22. 启动性能
- `src\ZeroFall.App\App.axaml.cs:31-135` 的分阶段装配:
  1. 同步阶段**只 new 一个空 MainWindow 并 `Show()`**(带 loading 遮罩)
  2. `YieldUiFramesAsync(3)` 让空窗先绘制
  3. **DI 容器在 `Task.Run` 后台线程构建**
  4. UI 线程注入 DataContext + `InjectMainContent`
  5. Dock 布局注册 → 物化
  6. 关遮罩 + `StartupPerformance.MarkLayoutReady()`
  7. WebView2 创建延后调度
  8. 300ms 后以 `DispatcherPriority.Background` 恢复上次项目
- `src\ZeroFall.Platform\Services\StartupPerformance.cs` — `YieldUiFrameAsync` / `RunAfterDelay` / `RunOnUiIdle` / `RunLastOnUiThread`(三层嵌套 Idle 用于 WebView 等重操作)
- `src\ZeroFall.App\Program.cs:16-35` — 三个全局异常钩子;`App.axaml.cs:49-50` 特判 COMException `0x8007139F` 直接吞掉

### 23. 错误处理三通道
| 场景 | 机制 | 出处 |
|---|---|---|
| 后台/非阻塞提示 | `StatusMessageEvent` → StatusBar(全仓 52 处) | `AiPanelViewModel.cs:2245` |
| 表单内联错误 | VM 暴露 `HasError`/`ErrorMessage` | `SqlEditorView.axaml:32-35` |
| 需用户决策 | `AppDialogService.ConfirmAsync/PromptAsync`(Ursa) | `src\ZeroFall.Dock\Services\AppDialogService.cs:27-70` |

ZeroFall **没有 Toast/Notification 系统**,StatusBar 就是替代。内容加载失败的约定是**降级返回 `TextBlock` 而非抛异常**(`ContentCreationService.cs:69,97,118,220`),保证 Tab 一定能开出来。

### 24. 其他小约定
- `GlobalUsings.cs` 每模块一份,把 Platform 的 Registries/Models/Services/Events + Base 的 Events/Mvvm 全局引入,显著减少 using 噪音
- `AvaloniaUseCompiledBindingsByDefault=true` 全开,动态绑定处局部 `x:CompileBindings="False"`
- `IDockTabToolPanelProvider`(`src\ZeroFall.Platform\Registries\IRegistries.cs:83-86`)— Tab 内容可向 Dock 宿主"上交"一个工具栏控件,宿主统一挂载/卸载(避免同一控件双父级)
- Content 区 Tab 关闭不由 Panel 自己处理(`DockPanelViewModel.cs:96-97` 提前 return),交由 `ContentPanelViewModel` 的订阅链统一处理 —— 因为浏览器需额外释放 CDP/WebView
- `ViewLocator` 是手写 `Dictionary<Type, Func<Control>>`(`src\ZeroFall.App\ViewLocator.cs:15-33`),AOT 友好但导致 App 硬依赖所有模块的 View/VM 类型。**Hookmes 可改进**:让各模块 `Initialize` 时向 `IViewRegistry` 自注册,既解耦又保住无反射

---

## 三、明确不继承 / 需修正

| 项 | ZeroFall 现状 | 出处 | Hookmes 决策 |
|---|---|---|---|
| 中央包管理 | 无。`Directory.Build.props` 仅 3 行,版本号在各 csproj 重复 8~10 次 | `Directory.Build.props` | 用 `Directory.Packages.props` |
| AI 安全闸门 | **完全没有**。AI 可无审批跑任意 shell、ssh、输密码;`MaxToolRounds=100` 且异常自动回喂继续 | `src\ZeroFall.Terminal\Tools\TerminalAiToolService.cs:43` | `IToolPolicyGate` 在 `AiToolUiDispatcher.ExecuteAsync` 这个唯一收口处强制策略。原作者也认为此处成本很低 |
| API Key 存储 | 全部明文存 JSON,全仓 `ProtectedData|DPAPI|Encrypt|Aes` 零命中 | `src\ZeroFall.Platform\Models\AppSettings.cs:36-52,128-144` | Windows DPAPI(`ProtectedData.Protect` + `CurrentUser`) |
| 日志 | 自写静态类 + 散落 `Debug.WriteLine` | `src\ZeroFall.Base\Diagnostics\AppDiagnostics.cs` | `IAppLogger` 抽象 + 轻量文件 sink,保留"日志绝不影响行为"的原则 |
| 设置变更通知 | `AppSettingsSavedEvent` 全局广播却只有 1 个订阅者;各模块实际靠专用事件,模型不统一 | `SettingsServiceImpl.cs:96` | 统一走强类型分节变更事件,新增设置项不易漏接线 |
| DB 迁移 | 无版本号,`CREATE TABLE IF NOT EXISTS` + `PRAGMA table_info` 补列,逻辑散在 9 个 store | `TrafficArchiveService.cs:537-596` 等 | `PRAGMA user_version` + 集中迁移脚本列表 |
| `ICommandService` | `ExecuteAsync` 是 **50ms 轮询等待**,`RunCommandAsync` 有反射 `DynamicInvoke` 分支,与全仓 AOT 取向相悖,且使用面很窄 | `src\ZeroFall.Base\Commands\CommandService.cs:90-93,171-175` | 不移植。需要长任务编排时用标准 `Task` + `IProgress<T>` + `CancellationToken` |
| 国际化 | 完全没有。`GeneralSettings.Language` 字段存在且 UI 有下拉,但**读了不用** | `GeneralSettingsViewModel.cs:45,67` | 初期同样不做,但字符串集中放置,不做假的语言开关 |
| Intruder Pitchfork | 实现与 BatteringRam 完全相同,语义未实现(疑似未完成) | `HttpIntruderEngine.cs:87-97` | 不涉及 |
| 行 diff | 逐行按索引对齐的朴素比较,一行插入会导致后续全部标 Different | `HttpLineDiff.Compare` | Hookmes 的 `dom_diff` 需用 LCS/Myers |

---

## 四、参考项目里的编码事故(提醒)

- `src\ZeroFall.Traffic\Capture\TrafficCaptureRecord.cs:3` 的 XML 注释是乱码 —— 文件被以非 UTF-8 编码保存过,中文注释已损坏。同类问题见 `AppSettings.cs:192`
- Hookmes 所有源文件统一 **UTF-8 with BOM**(C# 中文注释在 Windows 工具链下最稳),并在 `.gitattributes` 中固定
