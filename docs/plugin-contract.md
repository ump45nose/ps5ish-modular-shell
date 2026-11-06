# 插件清单 2

共享程序集保持 hostContract 1；清单升级为 schema 2，旧清单会给出明确版本诊断。三个内置包已迁移。

```json
{"schema":2,"hostContract":1,"id":"preview.example","version":"1.0.0","entry":"Example.dll","type":"Example.Module","enabled":true,"capabilities":["music"],"dependencies":[]}
```

- id 在目录内唯一；version 为可解析版本号；入口只允许本包根目录 DLL。
- capabilities 目前支持 music / video，均须实现共享接口。无能力的贡献插件填空数组。相同能力的第二提供者隔离，避免不确定选择。
- dependencies 按包 ID 指定依赖，拓扑加载；缺失、禁用、初始化失败或循环均阻止消费者注册。
- Initialize 和 GetSections 在后台线程运行，不得访问 WPF，不得提前播放媒体。成功完整枚举后才注册贡献和能力。
- 每个包默认最多等待 2 秒。超时后首页继续，迟到实例立即释放且不注册。宿主离开时同样禁止迟到回流。
- 超时不会强杀托管线程，也无法阻止恶意 DLL、原生崩溃或无限分配。只支持可信代码；强隔离需要独立进程，尚未实现。
- UI 使用 LoadAsync；同步 Load 只供独立无界面验证。能力调用仍由 UI 线程发出。当前禁用/升级按重启生效，不支持运行中替换程序集。

安全模式完全跳过插件目录。维护插件包前备份用户修改；update.ps1 会拒绝覆盖不符合安装记录的文件。

## 0.4.1 补充

- 新增 assets 能力（IAssetProvider）与初始化前的 IConfiguredModule.Configure(ModuleContext)，用户数据目录由 SDK 宿主传入。核心不解析素材索引。
- 清单/宿主版本仍为 2/1，属于共享接口的添加，不改变现有 music/video 签名。
- settings 页面显示 GetDiagnostics 返回的稳定快照，避免后台加载时枚举可变集合。

## 0.5.0 通用扩展页

- 新增 contextView 能力，对应 IContextViewProvider：Title、Diagnostic、CanDisplay(GameItem)、CreateView(GameItem)。当前同类能力仍只有一个提供者，后续多页面目录需另行扩展，不能假设已支持多个页面插件共存。
- ModuleContext.PlatformViews 提供 IPlatformViews 平台公开控件桥，只有 UI 线程可调用；Initialize 仍不能创建 WPF。对象返回值由 WPF 宿主验证为 FrameworkElement，不将 SDK 或成就类型加入 Contracts/Core。
- 可信页面移出可视树时必须清理自身订阅。宿主负责返回、切换和卸载时移除控件；不可捕获的原生故障依然不受此机制隔离。
- 适配程序集按需消费公开平台插件，不自行 Assembly.Load PA。缺平台能力或安全模式均保持基础游戏浏览。
- 布局可选新增 LayoutChoice、Maintenance、ContextPageAction 按钮，不破坏既有契约 2 必需槽位。

## 0.6.0 独立内容页

- 新增 hubView 能力 IHubViewProvider（Title/CreateView），同类提供者仍限一个。主题可选 HubAction；宿主不引用 Emby 实现类型。
- 页面可选 IPageInput.HandleInput(action)，优先处理自己的返回/确认/方向操作；未处理交宿主。键盘 Escape 同样优先返回子页。
- 页面 Unloaded 必须取消请求和禁止迟到回流；初始化仍不联网、不创建 WPF。Emby 只依赖共享契约和系统库，可独立移除。

## 0.7.0 媒体服务与正片

ModuleContext.Media 提供 IPageMedia（Preview、PreviewResolved、Cancel、Playback）。独立页使用宿主现有预览协调规则；IPageActivity 接收前台/游戏会话变化。新增 playback 清单能力对应 IMediaPlaybackBackend，返回有定位和轨道选择的 IMediaPlaybackSession；它继承已有像素会话但不承载 Emby 状态字段。观看状态仍由 Emby 插件控制，主题与 Core 不依赖 Emby。

LocalVideo 同时实现 video/playback，共用唯一解码器。Emby 仅消费 Contracts，不依赖 LocalVideo 实现程序集；没有 playback 能力时详情禁用正片按钮。IPageMedia 是本轮新引入接口，相关本轮消费者需一同更新。宿主契约仍为 1，原有 IVideoSession 签名未变。

## 0.8.0 单次界面音效

新增可选接口 `IMusicLoopControl.SetLooping(bool)`，不修改原 `IMusicSession` 签名。LocalMusic 会话默认循环供背景音乐使用；宿主播放界面提示音时，在 Prepare 后、Play 前设置 false，片尾不再重播。第三方提供者可以逐步实现此接口；未实现时仍保持旧行为，不保证其提示音单次播放。

## 2026-09-11 多服务影视目录

`hubView` 现在允许多个提供者，使用 `GetHubProviders()` 按注册顺序取得快照。其他能力仍拒绝冲突；旧单服务读取保留首个实例。新增可选 `IHubAppearance` 提供品牌颜色与图标几何，新增 `IPageExit` 请求宿主退出。主题新增两个命名服务卡槽位，旧主题通过内置模板补齐。独立服务页面全屏显示，不写入游戏库。
