> 2026-09-11：默认影视入口已改为“影视 → 服务图标 → 全屏 Emby 网页”，旧原生浏览说明为历史实现。当前说明见 [影视网页重写](docs/media-web-20260911.md)。

# Modular Shell 0.8.0 本机开发版

工程已推进到 M6 独立快捷面板首版：游戏首页/详情/设置、可组合主题、本地及 Emby 素材预览、独立成就适配、原生影视浏览与正片播放、个人笔记面板。

2026-09-07 首轮样式已按用户要求回退重做并部署：固定选中位横条、紧凑奖杯、独立标签网格和单次导航音效。159 条构建检查通过；实机截图及未验收项见[回退重做记录](docs/theme-reset-20260907.md)。

真实全屏已装载，用户确认 DS5 方向重复修复、成就验收通过，并接受同规格显示器上的 4K60。独立网格库、默认 BGM、壁纸和界面音效已接线，真实屏幕键盘搜索与筛选通过；九款官方预告已缓存，补齐 32 位视频运行时后，Battlefield 6 与 Slay the Spire 2 真实主页自动播放通过。Slay the Spire 2 的游戏启动验收因云存档无法同步而取消，未验证运行后返回。见 [当前实机记录](docs/library-live-0.8.0.md)，不能据工程回归宣告游戏库全部可用。

- [开发顺序](docs/DEVELOPMENT.md)与[实施证据](docs/implementation.md)
- [素材、配置恢复](docs/maintenance.md)与[主题预览](docs/media-and-themes.md)
- [成就适配](docs/achievements.md)、[影视浏览](docs/emby.md)、[正片播放](docs/playback.md)
- [独立快捷面板](docs/quick-panel.md)、[插件契约](docs/plugin-contract.md)、[验收清单](docs/acceptance.md)

## 构建与安装

```powershell
cd P:\WorkSpace\ps5ish-modular-shell
.\build.ps1
.\scripts\update.ps1 -ExtensionsPath "$env:APPDATA\Playnite\Extensions"
```

构建目录固定 artifacts，使用本机 .NET Framework 4.8 编译器、Playnite SDK 和 ffmpeg；原生依赖锁定恢复并校验 SHA-256。build.ps1 -PluginOnly 可单独构建能力包。当前包为 artifacts/ModularShell-0.8.0.pext；完整全屏部署在正常退出 Playnite 后使用 scripts/deploy-library.ps1，包含扩展与独立主题的备份和校验。

更新脚本校验原安装、备份后替换，不自动重启 Playnite或启动游戏；已装入进程的扩展或本地修改会阻止更新。最终安装范围见 artifacts/installation-receipt.json。SDK 用户目录创建空文件 safe-mode 可在重新进入时跳过可选插件，卸载默认保留用户数据。

## 使用入口

Playnite 主菜单 Modular Shell → 打开验证界面。影视详情的“播放 / 续播正片”使用服务器最新进度，支持普通直传片源、暂停、定位、内嵌音轨和字幕。HDR、透传、转码及直播未纳入已验证范围。按用户授权实测《神雕侠侣》第 1 集，最终留下约 3:10.692 的续播进度。

同一菜单 → 启动独立快捷面板；默认 Ctrl+Alt+Space，面板内可换成 Ctrl+Alt+F8/F9/F10。个人笔记按游戏 GUID 保存，追踪目标为手动记录。未添加系统自启动；宿主退出后面板释放热键。输入隔离依赖普通前台路由，后台 Raw Input 游戏仍需单独验收。

## 验证与交付边界

此前默认构建183条PASS，包含隔离协议、32/64位真实4K本地解码、预告片尾恢复音乐、独立面板进程、笔记和离屏布局；日志 artifacts/validation-auto-media-4k-20260907.log。补充人工锁定与原声发现专项回归见 artifacts/validation-auto-media-locks-20260907.log。另有此前真实Emby播放证据；默认构建不访问真实账号、不写观看状态。

此前 30 分钟静音解码长测对应 0.4.1 共享引擎；本轮原生接口扩展只做针对性回归，没有把旧曲线视为本轮完整长测。公开发布所需的原生对应源码归档仍待完成，依赖许可见 third-party/NOTICE.md。本包仅本机开发使用，未对外发布。
本机已完成0.8.0备份更新及正常重启，扩展698个文件、独立主题46个文件校验通过；安装散列及回退位置见 artifacts/installation-receipt.json。

默认背景音乐已替换为 Kevin MacLeod 的 Dreams Become Real（CC BY 4.0，署名见 third-party/THEME-MUSIC.md），不再使用低频合成循环。专属音乐优先引用已安装游戏的独立原声，目前接入 Europa Universalis V 与 Victoria 3；无可用原声继续默认曲。预告原声默认接管，自动预告结束恢复当前音乐。背景和视频按逻辑画布等比完整显示；实际扬声器听感和 PS5 主题细节仍待验收。

设置中打开“自动素材 · 85英寸4K”可管理自动获取、队列、来源尺寸、重新获取、锁定和清理。已安装游戏自动补齐，其他游戏稳定选中后获取；低清素材不进入正式背景/预告，无可靠原声继续默认BGM。官方网络获取尚有超时及TLS失败，完整实现、实测质量清单和未通过项见 [交付记录](docs/automatic-media-4k.md)。



2026-09-07 最新交付：真实 4K 官方预告已自动下载并验证 3840×2160 输出；设置新增预告优先语言、启屏动画 / 主题曲本地文件、额外组件 · 影视库。Emby 已在正常重启后的 Playnite 加载真实内容。完整构建 194 条 PASS，最后宿主 HTTP 兼容修复另有专项回归；详见 [本轮交付与操作说明](docs/experience-20260907.md)。


2026-09-08 主题更新已部署：设置 → PS5 主题 · 动效与布局，可配置粒子、焦点呼吸、壁纸转场、奖杯展示、海报密度及五类本地音效。198 条构建 PASS；真实奖杯列表与配置保存已核验，详见 [交付与验收记录](docs/theme-experience-20260908.md)。
