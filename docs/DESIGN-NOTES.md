# 设计记录

这份文档记录 Hookmes 在实现过程中做出的关键决策、以及踩过的平台陷阱。目的是让后来者(包括几个月后的自己)理解"为什么是这样",而不是只看到"就是这样"。

---

## 一、Tab 保活:为什么需要一个自研控件

Avalonia 原生 `TabControl` 切页会卸载可视树。对普通内容这无所谓,但 Hookmes 的标签页里驻留着两类东西:

- **WebView2** — 离开可视树即被销毁,重新挂回来时页面已白屏、CDP 会话已断开
- **PTY 终端** — 同样会被销毁,shell 会话直接死掉

因此 `PersistTabControl` 提供双模式:

| 模式 | 行为 | 适用 |
|---|---|---|
| `Reloadable` | 内容挂在 `PART_SelectedContentHost`,切页卸载/重挂 | 普通面板 |
| `NonReloadable` | 内容挂在 `PART_NonReloadableOverlay` 叠层上**永不卸载**,切页只改显隐 | 浏览器、终端 |

代价是叠层里的控件全部同时存在于可视树,内存占用更高。但相对"每次切页重建整个浏览器会话"的代价,这是划算的。

### 由此衍生的两条约束

**面板折叠只能改尺寸,不能改 `IsVisible`。** 把面板设为不可见等于把里面的 WebView2 从可视树摘下来,前功尽弃。所以 `MainContentView` 折叠面板时是把 `GridLength` 归零并记住原尺寸,视觉效果等价,代价小得多。

**Tab 内容的释放时机只有一个:Tab 关闭。** 切页不释放。因此 `ITabContentReleasable` 的实现方**绝不能**在 `DetachedFromVisualTree` 里调用释放——叠层机制会让控件在切页时短暂离树,那时释放会毁掉还在用的资源。

---

## 二、CDP 通道:为什么手写 vtable

Avalonia 的 WebView 控件只暴露 `ICoreWebView2*` 裸指针(经 `IWindowsWebView2PlatformHandle`),而 `Microsoft.Web.WebView2.Core` 的托管封装没有从裸指针构造的公开途径。两条路:

1. 引入托管封装,想办法绕过构造限制
2. 直接按 vtable 调用 COM 接口

选了第二条。除了能拿到完整能力,还避免了一个实际问题:`Microsoft.Web.WebView2` 包会拖进 WPF 的 `Microsoft.Web.WebView2.Wpf.dll`,与 net10.0 的 `WindowsBase` 产生版本冲突(MSB3277)。移除该包引用后警告归零——**我们本来就一行托管 API 都没用**。

### 槽位序号必须从 SDK 头文件核对

vtable 槽位来自接口方法的声明顺序,前 3 个属于 `IUnknown`,业务方法从 3 起。核对方法:提取 `MIDL_INTERFACE("<iid>")` 到接口结束之间所有 `STDMETHODCALLTYPE` 声明并从 3 开始编号。

**最容易犯的错是漏数 `[propget]` 属性方法。** 它们的声明形如 `virtual /* [propget] */ HRESULT STDMETHODCALLTYPE get_Xxx`,同样占槽位。第一次统计时漏掉 5 个,导致所有序号偏移 2~6 不等。

当前使用的槽位(`ICoreWebView2`,IID `76eceacb-0462-4d94-ac83-423a6793775e`):

| 槽位 | 方法 |
|---|---|
| 3 / 4 / 5 | `get_Settings` / `get_Source` / `Navigate` |
| 27 | `AddScriptToExecuteOnDocumentCreated` |
| 29 / 31 / 43 | `ExecuteScript` / `Reload` / `Stop` |
| 36 | `CallDevToolsProtocolMethod` |
| 38–41 | `get_CanGoBack` / `get_CanGoForward` / `GoBack` / `GoForward` |
| 42 | `GetDevToolsProtocolEventReceiver` |
| 48 / 51 | `get_DocumentTitle` / `OpenDevToolsWindow` |

升级 WebView2 SDK 后应重新核对。

### IID 不能凭记忆写

写错一位的后果是 `QueryInterface` 静默失败——回调永远收不到,而且没有任何报错。实践中确实凭记忆写错过一次(`AddScriptToExecuteOnDocumentCreatedCompletedHandler` 的后半段),所以现在全部从 SDK 头文件提取并在代码注释里标注来源。

### 回调用源生成 COM 互操作

.NET 8 起的 `[GeneratedComInterface]` / `[GeneratedComClass]` 在编译期生成 CCW 与 vtable,不需要手工分配非托管内存,也不走运行时 `IDispatch`,对裁剪与 AOT 安全。托管对象转裸指针用 `ComInterfaceMarshaller<T>.ConvertToUnmanaged`,用完 `Free`。

**回调里绝不能让异常逃逸到原生栈**——那会直接终止进程且没有可用调用栈。所有 `Invoke` 实现一律 try/catch 吞掉并返回 `S_OK`。

### 事件接收器是按事件名创建的

`GetDevToolsProtocolEventReceiver(eventName, out receiver)` 不是一条总线,每个事件名要单独取接收器。因此会话层需要维护"事件名 → 接收器 + handler token"的表,做引用计数(多个订阅者共享一个接收器),并在页面关闭时统一解绑。

---

## 三、时序:先订阅,后导航

第一次接通 CDP 后测网络事件,计数一直是 0。原因是导航同步发生在 CDP 会话建立之后、域启用与事件订阅完成之前——页面加载完了订阅才建好。

**对调试工具来说这是致命的:首屏恰恰是最需要看到的部分。** 现在的顺序固定为:

```
建立 CDP 会话
  → 启用域 (Page / Runtime / Network / Log)
  → 装配 Page Agent (addBinding → 订阅 bindingCalled → 预注入脚本)
  → 订阅 CDP 事件
  → 最后才发起首次导航
```

Page Agent 的预注入同样受此约束:`Page.addScriptToEvaluateOnNewDocument` 只对**之后加载的文档**生效,必须赶在导航之前;而 binding 又要先于脚本存在,否则脚本启动时拿不到回传通道。

---

## 四、Page Agent:执行世界的划分

有一个无法回避的矛盾:**要 hook `fetch` / `XHR` 必须在主世界**——隔离世界有独立的 JS 全局对象,在其中包装这些 API 对页面代码毫无影响。而隔离性又只有隔离世界能提供。

所以 Agent 拆成两部分:

| 部分 | 世界 | 内容 | 理由 |
|---|---|---|---|
| `agent-main` | 主世界 | 网络 / 存储 / 路由 hook | 必须包装页面实际使用的对象 |
| `agent-iso` | 隔离世界 | 录制、选择器生成、元素拾取 | 不需要改写主世界对象,隔离更安全 |

主世界部分的固有风险:页面可以检测到 `fetch.toString()` 异常、可以保存原始引用绕过 hook、也可以反过来篡改 Agent。这**无法根除**。应对是把主世界部分做到最小(只 hook 与上报,不含任何逻辑),并接受降级——hook 失效时 CDP Network 域仍提供完整流量,只是丢失发起调用栈。

### 透明性是硬要求

hook 引发页面行为改变是 bug,不是可接受的副作用。具体要求:

- 保留原函数引用,正确转发 `this` 与全部参数
- 异常原样抛出,不吞不包装
- `toString()` 伪装成原生实现,`name` / `length` 保持一致
- 保持原型链与属性描述符(`WebSocket` 包装后仍要能 `instanceof`、仍要有 `WebSocket.OPEN`)
- XHR 用 `addEventListener('loadend')` 而非覆盖 `onloadend`,避免与页面自己的处理器冲突

### 为什么值得做

CDP 的 Network 域能告诉你"发生了什么请求",但告诉不了你"哪行代码发起的"。Agent 补上这一层:

```
net/fetch → {"url":"data.json",            "stack":"at .../app.js:5:1"}
net/xhr   → {"url":"data.json?via=xhr",    "stack":"at .../app.js:23:5"}
```

调试"这个请求为什么发了两次"时,调用栈才是线索。

---

## 五、并发:两道必须存在的闸门

**`WebViewCreationCoordinator`** — 同一时刻只允许一个 WebView2 初始化。并发初始化会在 WebView2 运行时内部争抢用户数据目录,表现为其中一个永远卡在创建中。带 20 秒看门狗:初始化失败时回调可能永不到达,不设超时会让后续所有创建饿死。

**`UiScriptGate`** — 浏览器视图与 AI 面板是两个 WebView 实例,共用同一条 UI 线程。一方等待脚本/CDP 结果时另一方发起调用会互相阻塞,表现为整个界面挂死。所有 `ExecuteScript` 与 CDP 调用都必须包在闸门内。

**`UiThreadBridge` 一律用 `Dispatcher.Post` + `TaskCompletionSource`,刻意不用 `InvokeAsync`。** 后者在嵌套调用时会死锁,而本应用有大量"UI 线程等待 CDP、CDP 回调又要回 UI 线程"的路径。

### 适配器就绪要事件加轮询双保险

`AdapterCreated` 事件在部分时序下不会触发(例如控件在适配器创建完成之后才挂上处理器)。因此在 150/400/800/1500/3000/6000 ms 这几个点重复探测原生句柄,任一命中即建立会话,内部幂等。宁可多探几次,也不要一个永远白屏的标签页。

---

## 六、平台陷阱清单

### 高 DPI 与窗口尺寸

XAML 里的窗口尺寸是**逻辑像素**。在 125% 缩放的屏幕上,`1440×900` 实际要占 `1800×1125` 物理像素,很容易超出工作区导致窗口显示不全。现在启动时按屏幕工作区换算缩放后再夹一次,并且面板宽度也按窗口比例夹取(侧边最多 30%,底部最多 45%)——因为持久化的尺寸可能来自更大的窗口。

顺带一提:用 DPI-unaware 的进程截图会只拿到窗口左上角的一部分,曾因此误判"右侧面板没渲染"。用 `PrintWindow` 直接从窗口取图更可靠,也不受遮挡影响。

### 文件编码

C# 源文件里的中文注释必须以 UTF-8 保存。用 PowerShell 的 `Set-Content` 修改含中文的文件会按系统 ANSI 码页写出,注释直接变乱码且不可逆。改这类文件要用明确以 UTF-8 写入的工具。

`.gitattributes` 里**不要**设 `working-tree-encoding=UTF-8`——那个属性是给"工作区需要非 UTF-8 编码"的场景用的,我们的文件本来就是 UTF-8,设了只会引入多余转换。

### `file://` 不产生网络事件

Chromium 的 `file://` 不走网络栈,CDP Network 域不报告,`performance.getEntriesByType('resource')` 也返回空。用本地文件验证网络能力会得到"事件泵不工作"的错误结论。测网络相关功能必须用真实 HTTP 服务器。

### Avalonia 细节

- `DispatcherPriority` 在 Avalonia 12 是 struct 而非 enum,**不能作为默认参数值**。需要可选参数时用 `DispatcherPriority?` 加 `?? 默认值`。
- 资源查找 API 是 `TryGetResource(key, themeVariant, out value)`,不是 `TryFindResource`。
- 类不能整体标 `unsafe`——那样 `async` 方法里就不能 `await`(CS4004)。指针操作要收进局部 `unsafe` 块。
- `PathIcon` 走填充渲染。用 `M 0,0 L 10,10 M 10,0 L 0,10` 画叉号会得到一个实心方块,因为路径没有闭合区域。这类图形用字符或闭合路径。
- DataGrid 的固定列宽加起来必须明显小于面板宽度,否则窄面板下后面的列会被整体挤没。让主要列用 `Width="*"` 吃剩余空间。

### C# 细节

- lambda 参数名不要用 `_`。那样它就是参数本身,后面的 `_ = SomeAsync()` 丢弃赋值会被当成给参数赋值,报"无法将 Task 转换为参数类型"。
- 扩展方法必须 `using` 其命名空间,写全限定名不够用。

---

## 七、明确的取舍

| 决定 | 理由 |
|---|---|
| 五区域固定布局,不做可拖拽 Dock 树 | 复杂度与收益不成正比。区域编译期固定,只有 Tab 动态 |
| 布局只持久化面板可见性与选中 Tab | 不存 Tab 列表——Tab 由各模块启动时重新注册,存下来反而会与代码变更冲突 |
| 数据层不引 ORM | 裸 SQLite + 手写 SQL。查询形态简单,ORM 的抽象成本高于收益 |
| `ViewLocator` 用显式字典而非反射约定 | 反射方案在裁剪与 AOT 下会静默失败,字典的错误在编译期就能发现 |
| CDP 负载不做全量强类型建模 | CDP 的形状随 Chromium 版本演进,维护成本远高于收益。只对实际用到的字段做轻量读取 |
| 前端产物提交进仓库 | `PageAgentScript.g.cs` 入库后,常规 `dotnet build` 不需要 Node |
| 模块清单硬编码而非程序集扫描 | 反射发现省下几行代码,换来启动变慢、AOT 不友好、以及"模块为什么没加载"这类难查的问题 |

---

## 八、待偿还的技术债

- **DB 迁移** — 目前尚未落地,规划用 `PRAGMA user_version` + 集中迁移脚本列表,避免迁移逻辑散落到各个 store
- **Agent 消息分片** — 单条消息超过 64 KB 时直接丢弃字段并标注 `truncated`。大 payload(如完整请求体)需要分片重组协议
- **隔离世界 Agent** — `agent-iso` 尚未实现,录制与元素拾取依赖它
- **AI 工具策略闸门** — 已落地默认保守策略、确认 UI、会话授权与显式信任模式；后续可细化按域名和工作区路径授权
- **国际化** — 当前中文硬编码。不做假的语言开关,要做就一次做对
