# 三色奖杯与 85 英寸 4K 自动素材系统

用户确认：三色使用 PA 当前游戏兼容计数；白金优先已解锁 Capstone，无 Capstone 时采用 IsCompleted；已安装优先，其余按需；自动缓存不设总量上限。

## 执行状态

- 奖杯修复已独立构建部署，实机 BF6 为金32/银11/铜0，CK3 为金114/银15/铜1；上下文改为 ModernTheme.SelectedGameId，避免误用全局 SelectedGame。
- 白金条件的有/无/未加载 Capstone 与错误游戏 ID 已通过自动回归；真实已白金游戏、扫光和减少动效实机用例仍待完成。
- 奖杯阶段日志：artifacts/validation-trophies-4k-20260907.log；实机截图：artifacts/live-trophies-4k-20260907。
- 奖杯阶段包 SHA256：DB5AD23E410CBF328511FE4F19B5CF50519EF510FAB67E9699BA0BB738B558D9。
- 奖杯阶段回退备份：C:\Users\17290\AppData\Roaming\Playnite\ModularShell-backups\library-20260907-151552-df84fbaaf61d478ea38c28f7cc5f9698。
- 自动素材服务、旧缓存质量门槛、实际4K解码及管理窗口已实现并部署；工程交付完成，完整实机退出条件尚未全部满足。以下记录取代此前“正在实现”状态。

## 质量与兼容约定

背景首选3840×2160，最低1920×1080；排序为4K 16:9、1440p 16:9、合格Hero 2x、1080p 16:9。最多五张去重壁纸。Hero 3840×1240不是4K全屏壁纸，普通1920×620拒绝。

预告首选2160p，最低1080p；只使用Steam max/高质量HLS或可核验官方YouTube来源。下载完成必须探测真实参数，不升频、不上采样冒充高清。Logo使用官方透明原图。音乐优先本机或官方原声，缺失继续默认曲。

旧人工索引保留，自动清单单独存储；锁定素材不覆盖但仍经过门槛。原低清文件保留回退，不能继续自动播放。失败不阻塞浏览和启动，不允许把低清Playnite原背景作为隐式绕过。

## 自动素材实现与使用

设置中的“自动素材 · 85英寸4K”窗口提供启停、队列状态、来源与实测尺寸、当前游戏重新获取、锁定/解锁和自动缓存清理。库就绪后按最近游玩的已安装游戏补齐，其他游戏稳定选中后获取。当前请求优先，最多两个下载任务；游戏运行暂停后台。切换只取消界面等待，已安装游戏任务可继续缓存。图片更新合并刷新，不重启音乐或重建奖杯。

联网与选源位于 LocalAssets 插件；自动状态独立保存在用户数据 automatic-assets/state-v1.json，单写入者、临时文件验收后原子提交。保留 assets.json 和用户原文件；显式解锁保留原索引自定义字段及备份。低清旧缓存撤出候选，锁定低清项显示不达标。清理仅处理未锁定自动文件；磁盘不足停止新下载，不靠删除用户内容腾空间。网络故障退避重试，已有合格素材继续使用。

Steam 使用 path_full、Hero 2x、官方 Logo 原图与 max/HLS 预告；读取本机 Steam 素材缓存和安装清单，自动寻找独立 OST。官方 YouTube 候选须通过官网/商店链接、频道及游戏身份核验，不确定时保留缺失。独立 yt-dlp、Node、ffmpeg/ffprobe 固定版本与散列，隐藏运行并使用进程作业管理取消，不依赖开发机 Python。视频保留源编码封装，记录音轨、编码、帧率和码率。

壁纸按屏幕物理像素解码，最高3840宽；Logo不放大低清源图解码。视频取消固定720p缓冲，依据可见媒体尺寸协商输出，修正编码填充尺寸导致的4K误缩。游戏最低1080p门槛不套用于Emby正片。无音轨预告保留默认音乐；有音轨时按原声接管设置处理。

## 验证证据

所有构建固定 artifacts 串行执行，未切换构建目录。最终完整构建日志 [validation-auto-media-4k-20260907.log](../artifacts/validation-auto-media-4k-20260907.log) 共183条PASS；另补人工锁定兼容测试，16条专项PASS见 [validation-auto-media-locks-20260907.log](../artifacts/validation-auto-media-locks-20260907.log)。专项测试与完整构建存在重叠，不将两者相加计作独立用例数。

- 自动测试覆盖低清拒绝、真实图头与损坏文件、1080p接受、4K排序、无音轨、人工锁定保护、无人工索引的本机主菜单原声发现，以及100次重复排队、当前抢占、两个并发任务、暂停与关闭。x86/x64真实4K文件均输出3840×2160并取得帧；无音轨4K继续默认音乐。
- [空缓存实测](../artifacts/automatic-live-20260907/quality-report.txt)：服务自行发现和下载BF6、CK3素材，获得合格背景、Hero2x、Logo及1080p预告；没有为每款游戏人工指定下载URL。这是本轮较早服务版本的证据，后续增强另作回归。
- 最终安装版实机已显示仁王3自动预告，输出记录1920×1080；切换鬼武者后对应预告和奖杯同步更新，三色从3/25/23变成0/0/2。实际输出见 [视频记录](../artifacts/live-auto-media-20260907/game-video-output.json) 和 [壁纸记录](../artifacts/live-auto-media-20260907/game-wallpaper-output.json)。
- [首十款清单](../artifacts/automatic-quality-20260907/README.md) 共90项实测记录，包含拒绝项和留档项，不等于90项合格素材。完整URL、散列、获取时间及媒体参数在同目录CSV/JSON；通过 scripts/export-auto-assets.ps1 可重新导出，不触发下载。
- [仁王3截图](../artifacts/live-auto-media-20260907/nioh3-auto-preview.jpg)、[鬼武者截图](../artifacts/live-auto-media-20260907/onimusha-auto-preview.jpg)、[真实窗口采样录像](../artifacts/live-auto-media-20260907/sampled-preview.mp4)。录像为12帧、低帧率、无声采样，只证明真实画面变化，不能验证800ms扫光或听感。

## 部署与恢复

最终包 [ModularShell-0.8.0.pext](../artifacts/ModularShell-0.8.0.pext)，SHA256：`2EABF505AAA3EAB170E8F161C87223D7A999656E87DD0BCD076A06B5023566E7`。2026-09-07 16:17:44完成部署，698个扩展文件及46个主题文件校验；回执见 artifacts/installation-receipt.json。16:19正常重启后日志确认 Modular Shell 0.8.0、PA 3.1.3及全屏窗口装载。没有启动用户游戏或更改影视观看进度。

本阶段回退备份：`C:\Users\17290\AppData\Roaming\Playnite\ModularShell-backups\library-20260907-161739-879b36d7a112468888acd91260f34dd5`。恢复前正常退出Playnite，按备份清单恢复扩展和主题；自动缓存与用户原文件独立保留。奖杯阶段安装包另存 artifacts/releases/trophies-20260907.pext，独立备份见前文。本轮未执行回退演练，不把“存在备份”当作恢复验收通过。

## 明确未通过项与下一步

- 真实已白金游戏、Capstone实际变化、800ms扫光和减少动效尚无完整实机证据；逻辑测试不能替代该项。
- Steam请求存在间歇超时/SSL失败，YouTube实测TLS EOF；尚未证明官方YouTube 4K预告与官方网络OST成功自动落盘。没有改动系统代理或认证。无可靠原声保持默认曲；已有Europa V、Victoria 3人工本地关联不算本轮自动发现成果。
- 首批不是全体4K；Hero2x只算宽屏备选，合格1080p预告按规则接受。原生4K实际输出已在测试源验证，尚未在这批自动下载的真实4K游戏预告上实机验证。
- 鬼武者自动选中的合格1080p官方Steam预告含德语字幕，尚未加入中文/英文语言优先。
- 100次自动排队测试不等于100次DS5实机切换。真实连续切换、断连重连、新素材管理窗手柄操作、30分钟目标电视观感/听感，以及限流/磁盘不足故障实机用例仍待验收。
- 最终宿主启动仍报告缺少 gamecontrollerdb.txt。Shell与PA装载成功，但不能据此宣称本次控制器全链路重新通过；该宿主映射错误保留为独立待核验项。

下一轮优先完成可靠网络条件下的官方4K/OST自动获取实测，再做白金与目标电视体验验收。未将整个计划或“游戏库可用”退出条件标为完成。

## 16:40 网络修复后复测

用户告知网络已修复后，以独立空验收缓存重新运行现有自动服务，选择SDK最近已安装的BF6和CK3。两款均在Steam appdetails请求遇到“基础连接已经关闭：发送时发生错误”，没有新增网络素材；程序正常退出，用户正式缓存未改写。证据见 artifacts/automatic-network-retry-20260907/quality-report.txt。

YouTube已经恢复搜索和视频格式读取：固定版本yt-dlp成功检索CK3预告，并读取发布预告xjn66Cl3pMA的1080p视频及独立音轨列表。该探测不等于自动官方来源核验、下载入库或4K验收完成；没有把搜索候选手工写入正式索引。日志见 artifacts/youtube-network-retry-20260907.log 与 artifacts/youtube-format-retry-20260907.log。

独立curl对Steam同一HTTPS接口的两次复测仍报schannel握手失败、HTTP 000；YouTube主页返回200。因此本次已消除“所有YouTube请求仍TLS失败”的旧结论，但Steam元数据链路仍阻止自动补齐。未修改系统网络、证书校验或程序代码。

2026-09-07 18:06 更新：网络恢复后的真实 4K 自动获取、预告优先语言、本地启屏/主题曲和可选 Emby 配置已部署。当前证据与未验收项以 [experience-20260907.md](experience-20260907.md) 为准；旧网络失败记录保留为历史，不再代表当前连通状态。
