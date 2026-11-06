# PS5 样式回退重做：2026-09-07

用户否定首轮 theme-polish 的视觉方向。本轮已回退其主题文件，再依据 [PS5ish](https://github.com/davidkgriggs/PS5ish) 的实际截图和 WPF 布局重做，并安装到真实 Playnite 全屏。当前是可 review 的重做版，尚未标记为完全复刻或用户视觉验收通过。

## 回退边界

被否定源码留在 `artifacts/style-rejected-20260907/`。主题文件恢复自样式轮前备份 `library-20260907-140359-99c0284b4dc44e5c95fc5b214ee1e831/shell-previous/themes` 后重做。其余源码按样式范围替换；保留已修复的 DS5 输入链、扬声器、Uniform 完整画面显示及媒体取消协调。

## 重做结果

- 首页使用独立 HomeCarouselPanel：小方卡顶部对齐，选中卡放大到 1.5 倍并固定位置，横条在切换时滑动；140 ms 位移与缩放可从当前动画续接。移除呼吸闪烁；减少动效直接定位。
- 标题位于选中卡右下，Logo 位于左下主操作上方，无 Logo 时显示大标题。主启动与圆形更多、手动预告保持真实操作能力。
- 游戏库改为“你的游戏 / 已安装 / 收藏”独立下划线标签，六列虚拟封面网格；保留平台、排序、搜索及手柄键盘。封面 162 逻辑像素、行高 228，白色焦点框只围封面。
- 奖杯撤掉纵向大百分比和进度条，改为右下通用奖杯图形、完成度与已获得两列。计数列支持缩小长数字。使用 PA 公开真实计数；没有等级来源时不编造金银铜或白金。依据 [PA Modern Bindings](https://github.com/justin-delano/PlayniteAchievements/wiki/Modern-Bindings) 适配。
- 音效撤掉玻璃和弦，改为原创短空气瞬态与滑音，移动 / 确认 / 返回约 85 / 170 / 140 ms。发现 LocalMusic 原本将提示音也循环播放，短提示在释放前可能重复；新增可选 IMusicLoopControl，使提示音单次播放，BGM 默认循环不变。
- 横条移动时静止鼠标不再触发连锁悬停选择；移动音限频及确认/返回去重继续保留。

上游只读参考在 `artifacts/upstream-ps5ish-readonly2/`。采用其布局关系进行独立实现，没有直接加入无功能 PS Store/PSN 按钮或复制原机音频。

## 验证

固定 artifacts 串行构建，159 条 PASS，日志 `artifacts/validation-style-reset-20260907.log`。包含首页固定锚点、卡片不重叠、500 条库布局与已有媒体回归。

真实全屏重启后扩展与主题装载。实机核对 13 款库、全部/已安装标签、返回首页、单次右键 Battlefield 6 → CK3、选中锚点、缺封面与缺 Logo 回退。BF6 显示 81%、43/53；CK3 刷新为 69%、130/188。复核时发现三位数计数被裁切，扩大计数列并增加只缩小 Viewbox，重新构建部署后确认完整显示。

最终截图：

![首页](../artifacts/live-style-reset-20260907/home.jpg)

![游戏库](../artifacts/live-style-reset-20260907/library.jpg)

截图是本机 Playnite 真实窗口，不是原型渲染。未完成动态录屏、主观音色验收或本版 DS5 全链路重测。黑色 Logo 在复杂预告背景上的对比仍值得继续校准；当前图片不构成逐像素一致结论。原计划的真实游戏启动→退出返回及 30 分钟电视连续浏览仍不由本轮构建替代。

## 交付与恢复

- 安装包：`artifacts/ModularShell-0.8.0.pext`
- SHA256：`5BADCA69EF57AAD238A0772E4F3410A7ED0E9056CCA6D18AEED1A313B1D9B260`
- 安装校验：690 个扩展文件、46 个主题文件，回执 `artifacts/installation-receipt.json`。
- 最终安装前备份：`C:\Users\17290\AppData\Roaming\Playnite\ModularShell-backups\library-20260907-144156-689b2a71b5844bfe8fb075c92ff0b3ff`。
- 部署日志：`artifacts/deploy-style-reset-20260907.log`。备份保留原目录，恢复时先正常退出 Playnite，按回执对应 shell/theme 目标恢复，禁止在运行时覆盖程序集。
