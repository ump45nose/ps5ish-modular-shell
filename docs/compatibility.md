# 版本、上游与兼容证据

核验日期：2026-09-05。

| 项目 | 锁定值 | 验证级别 |
| --- | --- | --- |
| Playnite | 10.56.0.23531 | 本机 Playnite.dll 文件/产品版本 |
| Playnite SDK | 6.16.0.0 | 本机 SDK 文件与真实签名编译 |
| SDK SHA-256 | DF100B5AFA9E42DD65AC94DFB00C91C55EC8D35CD5D2C0325ACF2C84F2FE7448 | 本机文件 |
| Framework 编译器 | 4.8.9221.0 | 本机编译和测试进程 |
| PS5ish | 5.0.3 / ThemeApiVersion 2.3.0 | 上游 theme.yaml |
| PS5ish commit | c0659d82058f22b98d4df0545a38904c8a2530fa | 上游浅克隆固定提交 |

公开 SDK 的 GenericPlugin、GetGameViewControl、PluginUserControl、Database.Games、GetFullFilePath、StartGame 和会话事件均已由本机编译确认签名。尚未把编译结果视为真实进程装载证据。

## PS5ish 源码核查

仓库 LICENSE 为 MIT，版权归 davidkgriggs（2021）。当前安装包未复制上游代码或二进制资源；未来采用代码时附带许可。仓库中的品牌字体、图片与音效需逐项记录来源，不能仅凭根许可证推断所有外部素材均已获授权。

原主题不是可直接套用的新宿主皮肤：

- `source/Views/Main.xaml:46` 开始的主视图分三行；顶栏 95 高、底栏 100 高，存在针对宽屏的固定尺寸。
- `source/Views/Main.xaml:116` 附近把 SelectedGame.Id 拼入 ExtraMetadata 私有目录以获取微预告。
- `source/Views/Main.xaml:30`、125、175、2521、2636 附近有多个自行播放或循环的 MediaElement。
- `source/Views/Main.xaml:2294`、2299、2302 注入 SuccessStory 控件；DKGThemeModifier 也影响多处布局和焦点。
- `source/Constants.xaml:9-11` 保存 ExtraMetadata 相对目录及旧绝对目录示例。

上述关键路径与 SuccessStory 注入已抽查原文。后续先分离设计变量、卡片样式、布局，再替换素材与媒体控制；不能直接搬运整个 Main.xaml 并声称满足 TH-09/TH-10。

## 下一步兼容验证

1. 在 Playnite 正常重新启动后确认扩展装载日志与菜单出现。
2. 从菜单打开真实游戏库；不导入样例到用户库。
3. 自定义主题通过 `<ContentControl x:Name="ModularShell_Browser" />` 引用控件；需真实全屏主题验证。
4. 原 PS5ish 保留为只读参考，不直接覆盖用户 Default 主题。

来源：[Playnite 插件 API](https://api.playnite.link/docs/api/Playnite.SDK.Plugins.Plugin.html)、[自定义控件](https://api.playnite.link/docs/tutorials/extensions/customUiIntegration.html)、[PS5ish 固定提交](https://github.com/davidkgriggs/PS5ish/tree/c0659d82058f22b98d4df0545a38904c8a2530fa)。
