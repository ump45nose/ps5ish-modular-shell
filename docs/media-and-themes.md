# 主题与音乐接入

## 0.8.0 音画修正（当前行为）

默认音乐为 Dreams Become Real — Kevin MacLeod，CC BY 4.0。构建从作者站点获取原始 MP3 并验证固定 SHA-256，署名见 third-party/THEME-MUSIC.md，设置页同时显示署名。原低频合成默认曲已退出打包，界面音效仍由 generate-audio.py 生成。

专属音乐遵循用户选择：优先游戏原声，无可用来源继续默认曲。prepare-game-media.py 仅从已确认的游戏独立 MP3 原声目录导入，目前匹配 Europa Universalis V 和 Victoria 3；保留人工锁定及现有音乐，记录来源路径和散列。只引用用户安装文件，不解密资源包、不将预告音轨冒充 OST、不随主题分发游戏音乐。

首次设置默认启用预告原声接管；旧配置明确关闭时仍保留关闭。自动预告片尾恢复当前默认或专属音乐，取消与离开不恢复旧游戏音乐。壁纸、过渡层、视频层使用 Uniform 并固定在逻辑画布内，完整显示且允许留边；视频后端仍固定输出 1280×720，非 16:9 视频比例尚未实机验证。

以下按版本记录历史接口，旧的“缺少视频”“无默认音乐”等限制不代表当前状态；当前实机证据见 library-live-0.8.0.md。

## 主题

`themes/layout.ps5ish.xaml` 分为顶栏、横向虚拟化封面条、详情与操作区、扩展分区。它依据 PS5ish 布局语言建立，目前是初始布局，不代表原主题视觉复刻验收完成；未复制未知许可的字体、音效或品牌图片。

开发者可修改这个可信本地 XAML 包，但必须保留 ThemeComposer.Validate 列出的控件名称和类型。错误布局回退到程序集内副本。XAML 不含可选插件 CLR 引用、账号、网络查询、素材目录或媒体定时器。

配色使用 `palette.midnight.json`、`palette.ember.json`，只支持 schema 1 的五个颜色字段。先完整校验再应用，错误包不会留下半套配色。用户选择保存到 SDK 返回的用户目录 `theme.json`，原子替换时保留 `.bak`。安全模式不读取外部主题。

布局以 1280×720 逻辑画布等比缩放，已检查 720p/4K、两套配色、空库/500 条测试数据。此证据不替代真实电视 DPI、刷新率、手柄和帧率验证。

## 本地音乐

复制 `examples/assets.json` 到 **GetPluginUserDataPath 返回的扩展用户目录**，命名为 `assets.json`。不要放在主题或安装目录。填入实际游戏 GUID 和本地音乐路径，再重新打开 Shell 界面。

- provider：`playnite`；sourceInstance：`local`。
- itemId：Playnite GUID，不能填 Steam App ID。
- music：本地纯音频绝对路径；不读取网络流或 UNC 路径。
- volume：0～1，缺省 0.35，0 表示静音。
- 已实测 PCM WAV；其他格式取决于本机解码器，尚未逐格式验收。

缺配置、缺素材或移除 LocalMusic 时保持静态。坏文件/超时结束对应预览，不阻止游戏启动。索引只引用用户文件，不复制、下载或删除素材；换主题不改索引。

宿主稳定停留 200 ms 后准备，成功后 700 ms 淡入。切换条目、失去前台、控件隐藏、游戏启动和卸载均取消并释放。多个视图只保留一个 MusicPreview 音乐所有者。返回后只有可见前台界面才重新计算预览。

LocalMusic.dll 独立构建，只引用共享契约与 WPF。禁用可修改其 plugin.json 或移除包并重启；手动改过文件后，更新脚本的哈希保护会拒绝覆盖。

## 验证限制

17 项新增回归覆盖主题回退、预设备份、迟到结果、100 次切换、多视图所有权，以及真实 PCM 的 MediaOpened、静音播放调用、释放后文件可独占打开、取消和缺失文件。

测试素材为生成的静音 WAV，不代表真实游戏音乐听感验收。尚无视频后端、首帧可靠信号、两种视频声音模式和主动完整预告，这些继续属于 M0-C/M1。

技术依据：[Microsoft MediaPlayer 文档](https://learn.microsoft.com/en-us/dotnet/api/system.windows.media.mediaplayer)。

## 0.4.0 视频、声音和设置

视频字段已加入 examples/assets.json：video 为本地绝对路径；videoVolume 缺省 0.5；trailerAudio 缺省 false。音乐和视频均不接受 UNC。支持格式取决于锁定的 LibVLC；当前实测中文路径 H.264/AAC MP4。

自动模式稳定停留 2.5 秒后准备视频，准备全程静音且保留背景。后台通过 LibVLC display 回调确认完整首帧，UI 首先复制到 WriteableBitmap，再用 400 ms 淡入，最高约 30 次/秒复制最新帧。8 秒仍无首帧则回退。当前输出固定 1280×720、软件解码，未验证 4K 电视性能。

氛围模式：音乐继续，视频静音。原声模式：音乐在 300 ms 内淡出并释放，再打开视频原声。主动“观看预告”立即准备，隐藏首页，Space/A 暂停，Escape/B 返回，播放结束恢复首页。切换、后台、隐藏、会话和离开取消所有媒体。

设置页提供自动视频、音乐、预告原声、减少动效、夜间音量及手柄开关；保存成功后写入用户目录 preview.json，保留 .bak。取消不会应用编辑。关闭自动视频仍可主动观看预告。减少动效取消视频淡入，夜间音量乘以 0.25。

布局槽位契约现为 2，增加首页、全部游戏、详情、设置覆盖层和视频层。旧布局自动回退至嵌入副本。手柄映射为 XInput：方向/左摇杆导航，A 确认，B 返回，X 预告，Y 设置；仅当前台可见且无游戏会话时轮询。已有纯状态测试，尚未做物理手柄验收。

之前“尚无视频”的描述仅是 0.3.0 历史状态。当前 25 项 WPF 检查和 6 项真实视频检查覆盖新行为。VideoLAN 依赖锁定、来源与许可见 third-party/NOTICE.md，公开发布源码归档尚待完成。

## 素材维护工具与独立来源

LocalAssets.dll 已从 Playnite 宿主拆出，只引用 Contracts 与序列化框架。宿主通过 IAssetProvider 查询，路径仍为 SDK 用户目录下 assets.json。缺失或禁用该包时可静态浏览。

scripts/assets.ps1 提供 Audit / Export / Import，必须显式传入 -UserDataRoot；导入导出另传 -IndexPath。导入合并完整内容引用，保留已有 locked:true 条目、未知来源元数据和原索引 .bak；不移动或删除用户媒体。导出拒绝覆盖已有目标。导入失败保持原索引。Audit 的“文件存在”不是解码或许可验证。

组件样式位于 components.controls.xaml，布局只引用稳定样式名；设计变量位于 metrics.comfort.json。组件只允许 ActionStyle、NavigationStyle、OptionStyle 三个槽位；坏包回退内置组件。配色切换只替换配色字典，不清除其他组件。media timing 仍由宿主控制，设计变量只控制展示尺寸和视频淡入时长。

DS5 输入使用 Playnite SDK 公共按钮事件，收到 SDK 输入后不再并行轮询 XInput；同一状态机负责边沿和长按重复。设备断开清空保留按键。物理 DS5 验收见 acceptance.md。

## 0.7.0 组合包

theme.json（schema 1）增加 motion=soft/brisk 和 ambience=ocean/dusk，旧配置缺字段时取 soft/ocean。设置页独立切换；更换布局或配色保留这两个字段，不修改 assets.json。动效作用于背景、标题、页面和视频淡入，减少动效直接显示。氛围包由程序化渐变与符号组成，不包含未知授权图片/音效；尚非完整界面音效与图标体系。

themes/motion.*.json 与 ambience.*.json 仅提供受限数据字段，64 KiB 上限，整组校验后替换资源字典。包损坏保留现有组合，安全模式不读取外部包。主题不决定音乐/视频启动时序。
