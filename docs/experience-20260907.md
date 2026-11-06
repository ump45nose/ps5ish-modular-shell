# 4K 预告、启屏与可选影视组件交付

2026-09-07 18:06。本轮已构建、备份部署并正常重启 Playnite。网络恢复后重新运行自动获取，验证真实下载和实际输出，不沿用此前仅 1080p 的结论。

## 使用入口

- 设置 → 自动素材 · 85 英寸 4K：新增预告优先语言，支持简中、繁中、英语、日语、德语、法语及不限。分辨率优先，同清晰度按语言排序；缺少首选语言可回退英语或未知语言，不生成字幕，也不把未知语言标成中文。更改后保存并重新排队获取。
- 设置 → 启屏动画 / 主题曲：选择本地视频和音频，保存后使用；可以恢复默认。仅引用用户文件，不上传、不移动原文件。启屏每次启动一次，确认或返回可跳过，失焦取消，减少动效时跳过。默认使用自绘文字淡入淡出；本地视频保持比例。
- 设置 → 额外组件 · 影视库：启用 Emby，配置 HTTPS 服务器、登录账号，并选择该账号可见的影视目录。关闭后隐藏影视入口；影视数据不加入 Playnite 游戏库。目录绑定服务器和用户，换账号不沿用旧目录。

本机已完成登录并启用影视组件，读取到 12 个服务器目录，当前默认范围是全部可见影视目录。令牌使用 Windows 当前用户 DPAPI 保存，不在源码和交付记录保存密码。

## 真实验证

自动获取服务自行发现 Battlefield 6 官方频道预告和 Slay the Spire 2 的 Mega Crit 官方预告，两者文件实测均为 3840×2160。没有为各游戏人工下载指定文件。Slay the Spire 2 官方视频使用 AV1；其他游戏没有合格 4K 来源时仍可能为 1080p 或 1296p，不将其标为 4K。

- Battlefield 6 来源：https://www.youtube.com/watch?v=pgNCgJG0vnY
- Slay the Spire 2 来源：https://www.youtube.com/watch?v=PW22jwFNxU8
- Playnite 实际背景视频输出 3840×2160：[宿主输出记录](../artifacts/live-experience-20260907/game-video-output.json)。Slay the Spire 2 也在真实网格选择后观察到同尺寸输出；独立持久化宿主记录以 Battlefield 6 为准。
- 正式视频后端读取真实下载的 4K 文件，首帧及后续帧递增通过：[输出证据](../artifacts/real-trailer-4k-output-20260907.json)。
- 本地 4K 启屏画面、跳过释放与配置重读 3 项通过：[专项日志](../artifacts/validation-startup-live-20260907.log)。该项使用隔离空库，不代替电视观感验收。
- 18:04 在正常重启后的 Playnite 全屏点击影视，实际显示“已连接 · 本页 30 项 / 共 304 项”，海报及内容加载成功。本轮未播放正片、未写观看进度。

Emby 在独立测试成功而宿主失败的原因是 Playnite 自带 System.Net.Http 对 SslProtocols 设置抛出 PlatformNotSupportedException。统一 HTTP handler 在支持时设置 TLS 1.2，不支持时沿用系统协议；保留证书校验和重定向限制，未关闭 TLS 验证。登录、API 与播放转发共用此兼容处理。

## 构建与交付证据

- 完整串行构建 194 条 PASS：[日志](../artifacts/validation-experience-20260907.log)。最后 HTTP 兼容修复另通过 Emby、播放回归及真实服务器 8 项检查：[专项日志](../artifacts/validation-emby-host-fix.log)。
- 首批十款游戏实测清单包含 184 条记录，含低清拒绝与留档项，不代表 184 项均合格：[清单](../artifacts/automatic-quality-experience-20260907/README.md)。缺原声继续默认 BGM；清单中的缺 music 或图片 404 不等于已经成功的 4K 预告不可用。
- [启屏设置截图](../artifacts/live-experience-20260907/startup-settings.jpg)；[语言设置截图](../artifacts/live-experience-20260907/language-settings.jpg)。
- 安装包：`artifacts/ModularShell-0.8.0.pext`，SHA-256：`5F81170979CA6443900750D56CA21E32503681B709DF22FE102F9F60EDFC8763`。
- 扩展 698 个文件重新核验，散列不一致 0；部署时主题 46 个相对路径校验通过。[最终核验](../artifacts/experience-final-verification.json)、[安装回执](../artifacts/installation-receipt.json)。
- 本轮最终回退备份：`C:\Users\17290\AppData\Roaming\Playnite\ModularShell-backups\library-20260907-180930-a1d43de2b68d4fdabf3be03f23438d0e`。恢复时先正常退出 Playnite，按安装回执恢复对应扩展与主题；不覆盖用户媒体原文件。

## 保留未验收项

目标电视连续 30 分钟浏览、此次新增配置窗口的物理 DS5 操作、用户所选本地动画/音乐的电视观感与扬声器听感仍未验收。并非每款游戏都有官方 4K 或可靠专属原声。PS5 主题细节和整个 M6 退出条件不因本轮功能验证而统一标为完成。

18:09 补修：目录 DTO 使用字段，WPF DisplayMemberPath 无法显示名称；改为显式 ComboBoxItem 内容并保留目录 ID。插件串行构建与 Emby 回归通过，重新备份部署；日志 artifacts/validation-emby-directory-fix.log。


18:11 最终实机复核：下拉框正确显示‘全部可见影视目录’，状态为已连接、12 个目录，见 [配置截图](../artifacts/live-experience-20260907/emby-settings.jpg)。重启后再次选择 Slay the Spire 2，实际输出 3840×2160 的独立记录已保存：[Slay the Spire 2 输出](../artifacts/live-experience-20260907/slay2-video-output.json)。当前保留组件设置窗口供用户选择目录。
