> 2026-09-11：默认影视入口已改为“影视 → 服务图标 → 全屏 Emby 网页”，旧原生浏览说明为历史实现。当前说明见 [影视网页重写](media-web-20260911.md)。

# Emby 浏览 0.7.0

Emby 是独立 `hubView` 插件，不创建伪游戏。游戏首页顶栏“影视”打开原生 WPF 海报墙：继续观看、最近观看、最近添加与影视库。库每页 30 项，电视剧进入季/单集，电影和单集打开简介与续播位置。Enter 打开所选、Escape 返回；物理 DS5 验收仍延期。

## 本地连接

当前用户提供的服务器已验证并建立本地连接。密码未落盘，令牌以 Windows CurrentUser DPAPI 保存到 SDK 用户目录下 `emby/connection.json`；密文附加熵绑定服务器、ServerId、UserId。改服务器需重新登录，不能直接改地址复用旧令牌。连接和缓存不随安装包分发，也不纳入通用配置快照。

需要重新登录时，在本机 PowerShell 执行（不要将密码写进脚本）：

```powershell
$server = Read-Host 'Emby HTTPS 地址'
$username = Read-Host 'Emby 用户名'
$password = Read-Host 'Emby 密码' -AsSecureString
.\scripts\connect-emby.ps1 -Server $server -Username $username -Password $password -UserDataRoot "$env:APPDATA\Playnite\ExtensionsData\f13fdbe6-52ba-4198-8bea-457be1a609cf"
Remove-Variable password
```

连接脚本禁止重定向，只允许 HTTPS。插件用认证头读取数据/图片，不将令牌放入图片 URL；客户端 TLS 设置不改 Playnite 的全局选项，仍执行系统证书验证。错误界面只显示固定诊断，不转发响应正文。401/403 提示重登，不能以离线缓存掩盖权限失效。

## 分区、缓存与生命周期

- 最近观看使用服务器最近播放排序，再保留有播放次数、续播位置、已看或最后播放日期的项。服务器未提供 LastPlayedDate 时仍可显示未完播记录。过滤后数量不是全库历史总数，不显示伪总数；下一页取决于服务器原始分页。
- 列表/详情网络优先，故障回退最后有效缓存并标记离线；离线进度可能已变化。海报缓存一天，过期尝试网络、失败回退旧图。
- 缓存按服务器和账号隔离；写入时按目录文件总量检查 64 MiB，满额停止写入，当前版本无 LRU 淘汰。该额度不是外部进程写入或并发写入下的硬磁盘配额。
- 每页 30 项，海报最多并发 4 个；单响应分别限制列表 8 MiB、详情 2 MiB、海报 3 MiB。请求 15 秒超时。离开、换页、进入详情取消旧请求；返回详情补齐未加载海报。
- 服务器部分图片返回 HTTP 500 时保留文字卡片，页面不反复弹错。

## 验证与剩余工作

.NET Framework 只读实连接已验证四类分区、影视库前两页不重复、详情 ID 一致和认证海报获取，原生海报墙离屏渲染通过。日志 `artifacts/emby-live-0.6.0.log` 不含账号、令牌与媒体名；离屏图可能包含私人媒体名称，仅保留本机，不打包。

独立自动测试覆盖 ID、身份绑定、HTTPS、取消、历史语义、详情断网回退和海报缓存。0.7.0 已增加服务端搜索/类型筛选、共享预览和正片播放，当前能力与真实验证见 playback.md。当前只读验证没有更新观看状态。三组设备验收继续延期。

参考：[Emby 用户认证](https://dev.emby.media/doc/restapi/User-Authentication.html)、[用户媒体库 API](https://dev.emby.media/reference/RestAPI/UserLibraryService.html)。

## 0.7.0 搜索与素材

搜索词和电影/剧集/单集筛选在服务器执行，参数参与分页和缓存键。剧集子项按季号/集号排序。搜索当前支持键盘文本输入；不宣称物理手柄文字输入已完整实现。

页面通过 IPageMedia 复用宿主协调器，手动 assets.json 的完整引用优先；Emby 引用为 provider=emby、sourceInstance=服务器/账号隔离哈希、itemId=Emby ID。无手动素材时查询公开 ThemeMedia，单个素材最多 32 MiB、下载 15 秒有界等待，完整下载后才原子写入并播放。来源记录保存媒体 ID 与 UTC 时间，不保存凭证。离开/失焦取消下载；主题素材获取从不调用观看状态上报。

网络主题素材与图片/元数据共用原 64 MiB 缓存额度，没有 LRU，满额回退静态。缓存没有自动删除用户手动资产；来源更新不替换已缓存文件。当前动态来源支持 Emby，不声称已经集成游戏素材站点。