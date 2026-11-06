# 本地视频运行时

VideoLAN.LibVLC.Windows 3.0.23.1，作者 VideoLAN。NuGet 元数据声明 LGPL-2.1-or-later，许可全文见 LibVLC-LGPL-2.1.txt。本项目按宿主位数动态加载未经修改的 x64 或 x86 DLL，用户可以替换兼容运行时。

- 官方二进制：https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1
- 打包工程及构建说明：https://code.videolan.org/videolan/libvlc-nuget
- VLC 源代码：https://code.videolan.org/videolan/vlc/-/tree/3.0.23
- 依赖构建说明和补丁：https://code.videolan.org/videolan/vlc/-/tree/3.0.23/contrib
- 下载锁定和 SHA-256：scripts/restore-video.ps1。

当前安装包为本机开发验收产物，尚未对外发布。对外发布前须归档与二进制对应的完整源码、构建依赖及各组件许可，不能以本说明替代源代码交付。

# 自动素材工具

- yt-dlp 2026.08.19：官方独立 Windows 可执行文件，来源 https://github.com/yt-dlp/yt-dlp/releases/tag/2026.08.19 ，许可见 yt-dlp-LICENSE.txt。
- FFmpeg / ffprobe 8.1.2 Gyan full build：来源 https://www.gyan.dev/ffmpeg/builds/ ，未经修改，许可见 FFmpeg-LICENSE.txt；源码 https://github.com/FFmpeg/FFmpeg/tree/n8.1.2 。
- Node.js 24.18.0：来源 https://nodejs.org/dist/v24.18.0/ ，许可和第三方声明见 Node-LICENSE.txt。仅供 yt-dlp 官方提取器使用。
- 二进制精确散列：media-tools.lock.json；构建、打包和首次工具调用分别验证，不自动更新。

缓存中的游戏媒体属于其商店、发行商及原作者，不随安装包分发。每项来源与探测证据保存在用户目录 automatic-assets/state-v1.json。
