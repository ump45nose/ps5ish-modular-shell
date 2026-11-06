# 素材缓存与配置恢复

脚本随源码交付。`-UserDataRoot` 必须是 Playnite SDK 的 GetPluginUserDataPath 返回目录，不是扩展安装目录。素材维护前关闭 Shell 界面；配置恢复前正常退出 Playnite，避免运行中的设置保存覆盖恢复结果。

0.5.0 起也随安装包交付：从 Shell 设置页打开“素材与配置维护（键鼠）”。独立窗口提供缓存列表、添加本地文件、锁定/解锁、限额清理、快照备份/恢复及索引导入/导出。维护期间 Shell 暂停交互和预览，窗口关闭后重新读取素材。后台文件操作不占用界面线程，提交期间不能强制关闭窗口。

恢复按钮要求正常退出 Playnite；后台恢复脚本也会重新检查进程。此检查不能阻止用户并发重新启动 Playnite，恢复过程中仍应保持它退出。布局按钮位于 Shell 设置页，保存后下次打开生效。素材维护属于键鼠配置流程，不宣称物理手柄维护已验收。

## 配置快照

`scripts/config-snapshot.ps1 -Mode Backup -UserDataRoot <用户目录> -SnapshotPath <新的快照目录>`

保存现有 preview.json、theme.json、navigation.json、assets.json 的原始字节和 SHA-256 清单。只接受 schema 1，拒绝覆盖已有快照，不复制用户媒体、缓存或凭证。

`scripts/config-snapshot.ps1 -Mode Restore -UserDataRoot <用户目录> -SnapshotPath <快照目录>`

全量校验版本、白名单和哈希后，在用户目录 config-snapshots 下保存恢复前副本，再逐文件原子替换。普通异常会恢复已替换文件。只恢复快照包含的文件，不删除后来新增的其他配置。损坏的当前文件也会原样备份。快照未加密，不宜公开分享素材路径。

跨文件恢复不是断电事务：进程中断后可从 before-restore 目录找回原文件。不完整备份保留用于排查，未生成完整 snapshot.json 的目录不能恢复，重试时使用新目录。未来 schema 拒绝恢复，不猜测迁移格式。

## 限额本地缓存

`scripts/cache.ps1 -Mode Add -UserDataRoot <用户目录> -SourcePath <本地媒体绝对路径> -MaxMiB 1024 [-Locked]`

复制本地文件到独立 media-cache，输出可填入 assets.json 的绝对路径，不自动修改素材索引。以 SHA-256 加扩展名去重，记录本地来源和导入 UTC 时间。支持 mp4/mkv/webm/mov/avi/wav/mp3/flac/ogg/m4a；扩展名检查不代表可解码或许可核验。不下载网络媒体。

`scripts/cache.ps1 -Mode Audit -UserDataRoot <用户目录>`

列出登记素材路径、字节数、存在状态、来源、锁定和引用状态。相同内容再次 Add 可加锁；SetLock 模式配合 -CachePath 和 -Locked:$false 可解锁，窗口也提供锁定/解锁按钮。只允许操作已登记缓存，不接受用户源路径。

`scripts/cache.ps1 -Mode Prune -UserDataRoot <用户目录> -MaxMiB 1024`

按导入时间先后清理超额的登记缓存。assets.json 的 music/video 引用和 locked:true 缓存均保留；保护项占满额度时拒绝新增。坏素材索引阻止清理。用户源文件和未登记文件不参与删除；额度仅针对登记媒体，不是整个目录磁盘硬限额。

新增先完成临时复制和哈希校验；淘汰先改名暂存，清单提交后才删除。正常提交失败会恢复原缓存。独占维护锁防止两个工具进程并发修改；不能锁住 Playnite 内部保存，必须先关闭 Shell 界面。暂存期间磁盘峰值可能超额；断电或强制结束留下的 .tmp / .pending-delete 文件不自动清理，需人工核对索引后处理。

## 独立影院布局

用户 theme.json 示例：

```json
{"schema":1,"palette":"ember","layout":"cinema"}
```

layout 可选 ps5ish/cinema，palette 可选 midnight/ember。重新打开 Shell 后应用。影院布局将详情与操作置于封面条上方；换配色保留布局，换布局不改素材索引。损坏或非法布局回退至内置 PS5ish 初始结构。旧 schema 1 配置缺 layout 自动使用 ps5ish，下次保存时带上解析的布局字段；也可使用设置页的布局按钮。

回归：test-cache.ps1 七项、test-config-snapshot.ps1 十一项；另有影院 720p 和 4K/175% 离屏图。设备验收按用户要求延期。

## 0.6.1 恢复故障矩阵（UP-02 / AT-12 局部证据）

普通恢复失败后按提交逆序回滚。已有文件通过暂存、哈希检查和原子替换恢复，本次新建文件删除；单项回滚失败不阻断其余项，错误包含失败文件和恢复前快照位置。保留该快照，关闭占用程序后核对原始文件进行恢复。

| 场景 | 已验证结果 |
| --- | --- |
| 未来清单/配置版本、坏 JSON、缺文件、重复条目、越界、哈希污染 | 写入前拒绝，当前配置不变 |
| 第二个文件被真实共享锁占用，首项原先存在 | 恢复首项原始字节，包括损坏的旧 JSON；手动素材哈希不变 |
| 第二个文件被占用，首项原先不存在 | 撤销本次新建文件，其他配置和素材不变 |
| 正常恢复 | 保留未知字段、无关配置，并保存恢复前快照 |

两种中途失败均验证无暂存残留、恢复前快照完整；PowerShell 7 和 Windows PowerShell 5.1 均通过。schema 仍为 1，未引入新的数据迁移。断电、磁盘耗尽、回滚期间二次占用和并发启动 Playnite 未实测。
