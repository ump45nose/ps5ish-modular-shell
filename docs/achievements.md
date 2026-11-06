# M3：Playnite Achievements 适配

2026-09-06 核验官方公开源码和主题文档，上游 HEAD 为 `24b1bcab770277a645ef93f52795823739e0ae0e`。本机未安装 PA；该提交只是接口参考基线，不是已通过运行验收的 PA 版本。

## 当前交付

`plugins/Achievements/Achievements.dll` 是独立可选能力包，只引用 Shell.Contracts 和 WPF。宿主与主题不引用 PA 程序集，也不读取 PA 私有数据库、缓存或账号配置。

平台桥通过公开 `IPlayniteAPI.Addons.Plugins` 查找已装载的扩展，由 `Plugin.GetGameViewControl` 创建控件，随后设置 `PluginUserControl.GameContext`。PA 的 ThemeControlBase 确实继承这个 SDK 基类。官方 ID 与控件名只出现在适配插件，通用宿主只处理 `IContextViewProvider`。

有 PA 且当前条目属于 Playnite 时，游戏详情增加奖杯入口。默认仅请求 `AchievementCompactUnlockedList`；用户明确点击“显示全部成就（可能包含隐藏内容）”后才请求 `AchievementDataGrid`。更换失败时保留旧控件。返回、换条目和离开时移出可视树，让 PA 的 Unloaded 释放其订阅。PA 主题集成设置可能将控件折叠，必须在真实 PA 环境验收。

当前是实验性公开控件适配，不是完成的自有奖杯数据契约或 PS5ish 奖杯视觉复刻。账号与刷新仍由 PA 管理；不会自动安装 PA、登录 Steam 或发送刷新命令。默认页不展示未解锁内容，完整列表的隐藏规则与排序由 PA 控件及用户选择共同决定。

## 通知所有权

本模块不订阅解锁事件、不新增通知窗口、不另发音效。当前唯一通知输出仍是用户已有的 PA 配置，音效按 PA 官方设计由 UniPlaySong 承担。Shell 设置页和奖杯页明确显示这一归属；不自动更改其他扩展配置。

PA 公开源码有 `AchievementUnlocked` 静态事件和 `RequestSingleGameRefreshAsync(Guid)`，但没有独立版本化的跨插件领域合同。此轮不需要额外通知，因此不建立无用订阅。自有去重、通知皮肤和音效切换尚未实现，AT-10 保持未通过，不能用“未新增通知”冒充历史导入/重复事件测试完成。

## 已验证与待验证

- 自动测试：缺依赖收起、命名空间边界、默认已解锁请求、明确展开完整列表、第三方失败保留旧页、模块释放、程序集依赖隔离。
- 宿主回归：模拟 PA 控件的按需创建、进入、返回移除与卸载清理；截图明确标注模拟数据。
- 待真实 PA：账号/Steam 映射、真实成就和时间、主题控件开关、全屏焦点、隐藏成就、数据刷新、历史导入和重复事件通知。真实 Playnite 装载仍按用户要求延期。
- 下一切片：在可用 PA 环境锁定发行版本；完成可替换的奖杯模板和唯一通知路径验收，之后才能宣布 M3 完成。

## 官方证据

- [主题控件名称与集成开关](https://github.com/justin-delano/PlayniteAchievements/wiki/Theme-Bindings)
- [通知与 UniPlaySong 音效职责](https://github.com/justin-delano/PlayniteAchievements/wiki/Notifications)
- [插件公开控件工厂与事件](https://github.com/justin-delano/PlayniteAchievements/blob/24b1bcab770277a645ef93f52795823739e0ae0e/source/PlayniteAchievementsPlugin.cs)
- [公开 SDK 控件基类与上下文实现](https://github.com/justin-delano/PlayniteAchievements/blob/24b1bcab770277a645ef93f52795823739e0ae0e/source/Views/ThemeIntegration/Base/ThemeControlBase.cs)

0.7.0 新增能力包内独立 trophies.xaml 外观字典，仅创建奖杯页时加载，提供边框、配色与间距；缺失保持 PA 默认控件。未增加第二条通知路径，也未把外观完成计为 AT-10 通过。

