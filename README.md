# Shirobot.Plugin.MyParser

当前发布：`v0.6.10`。本版本使用 SDK `1.0.0`，需要宿主 `1.0.0` 的临时目录 API。

<p align="center"><img src="./Assets/icon.png" alt="MyParser" width="160" /></p>

[ShiroBot](https://github.com/ShirokaProject/ShiroBot) 多平台内容解析插件，支持自动识别链接、卡片渲染、视频 / 音频发送和 Cookie 热重载。

## 支持平台

| 平台 | 支持内容 |
| --- | --- |
| Bilibili | 视频、分 P、番剧、专栏 / 图文、直播 |
| 抖音 | 视频、图集、LivePhoto、图文配乐视频 |
| 小黑盒 | 帖子、文章 |
| 网易云音乐 | 歌曲链接、搜索、歌词卡片、QQ 语音 |
| 微信视频号 | `weixin.qq.com/sph/...` 分享链接 |
| YouTube | watch、youtu.be、shorts、live 视频链接，AV1 MP4 + AAC |
| X (Twitter) | 推文视频、动图、图片集、纯文本（fxTwitter 直连优先，官方 Syndication 兜底） |

## 安装

直接在Shirobot插件市场安装 / 从Release下载zip解压dll放入plugins插件目录。

兼容要求：ShiroBot 宿主最低版本为 1.0.0，插件使用 ShiroBot.SDK 1.0.0。


## 使用

直接发送支持的平台链接，插件会按自动解析开关和config.toml配置处理。解析成功后按配置发送卡片、媒体或正文，不再额外发送“解析完成”的文字摘要和内部属性列表。

## 配置

配置项可在插件配置界面或 `config.toml` 中查看和修改；修改后自动重载。

所有媒体下载、图片落盘、直播分片和转码中间文件均通过宿主 `CreateTempDirectory` API 创建独立临时目录，创建后保留 6 小时，由宿主自动清理；插件不再启动清扫或发送后删除临时文件。旧配置 `delete_local_video_after_send`、`delete_local_video_delay_seconds` 不再使用。

抖音动态图片会用 SharpMP4 循环并合成配乐；静图生成视频以及不支持的媒体回退需要本机安装 `ffmpeg` 和 `ffprobe`。
多张图文会优先使用 QQ 合并转发，其他平台会按图片顺序作为普通消息发送；包含动态图片时发送 Live Photo 视频，配乐合成失败则单独发送音乐。

通过 ShiroBot 配置界面的 **Cookie** 分组填写各平台请求头 `Cookie:` 后面的完整值，保存后立即生效。也可以编辑 `config.toml`：

```toml
bilibili_cookie = ""
douyin_cookie = ""
net_ease_cloud_music_cookie = ""
heybox_cookie = ""
weixin_channels_yuanbao_cookie = ""
```

网页默认隐藏 Cookie；配置文件中仍为明文，请勿公开或分享。首次启动会将旧 `cookies/*.txt` 中的非空值迁入对应的空配置项，保留原文件。之后只读取 TOML；主动清空 Cookie 后不会重新导入旧文件。迁移标记保存在 `data/.cookie-toml-imported-v1`，不要删除。

**从旧版更新时，先升级包含 Cookie 文件保护修复的宿主，再更新插件。** 旧宿主可能按旧安装清单删除 Cookie 文件；仅取消发布空文件无法修复已安装的旧版。已经被覆盖为空的 Cookie 需要从备份恢复或重新获取。

自动解析、视频大小上限、请求超时和媒体发送策略等参数同样在配置界面或 `config.toml` 中调整，修改后自动重载。

X (Twitter) 的解析、封面与视频下载共用 `http_proxy`；在无法直连 x.com / twimg 的网络下必须配置可用代理，否则解析或视频下载会超时/失败。

## 构建

需要 .NET 10 SDK，并初始化 SharpMP4 子模块：

```bash
git submodule update --init --recursive
dotnet build MyParser.slnx -c Release -p:CopyPluginToHost=false
dotnet publish src/MyParser/Shirobot.Plugin.MyParser.csproj -c Release -p:CopyPluginToHost=false -o release/Shirobot.Plugin.MyParser
```

源码、共享层、平台模块和 SharpMP4 子模块统一放在 `src/` 下；测试项目保留在 `Tests/`。主插件构建时包含各平台源码，发布时合并依赖。

## 说明

本项目主要用于个人学习和实验，随缘维护；第三方平台接口和风控变化可能导致解析失效。请仅处理自己拥有权利或已获授权的内容，遵守平台规则及适用法律，不用于侵权、绕过访问限制或公开代下服务。

本项目与第三方平台无隶属或背书关系。许可证：[Apache License 2.0](./LICENSE)。

配置通过宿主统一接口应用；保存后等待应用完成。修改 config.toml 也由宿主监听并应用，无需重新加载插件。

## 本地开发与调试

源码项目位于 `src/MyParser/`，共享代码和 Provider 项目也放在 `src/`；
开发脚本统一放在根目录 `scripts/`。打开 `MyParser.slnx`，使用 **Debug** 配置。
首次先构建主插件项目，再选择 **ShiroBot** 运行配置，直接点 Run 或 Debug。
Debug 构建会准备宿主并复制 DLL / PDB，Release 仍使用分发打包。

在仓库根目录也可以运行：

```bash
sh scripts/dev.sh --prepare  # 只构建并准备宿主
sh scripts/dev.sh --no-console  # 构建并启动
```

Windows 使用 `powershell -ExecutionPolicy Bypass -File scripts/dev.ps1 --no-console`。
开发宿主和配置位于 `.shirobot-dev/host/<RID>/`，新配置默认端口 7003。
已有配置和插件数据保留。未发布的 SDK 版本需要先准备同版本的本地宿主。

AXAML 预览在 Debug 开启：先构建，再在 Rider 预览器中选择主插件项目。
不要为设计预览修改宿主运行配置。
若点击 Debug 仍运行旧插件，确认启动前有 Build，并在 Rider 的
Toolset and Build 设置中关闭 Use ReSharper Build，确保 MSBuild 执行复制步骤。

内存管理：`max_pending_parse_tasks` 可在配置后台调整，默认 64，允许 1–4096；达到上限时跳过新解析请求，已在执行的任务继续完成。解析冷却和有限时禁言记录每 5 秒清理过期项。已完成的下载缓存最多保留 512 条、最长 6 小时，并定期移除已删除文件；正在下载的任务不会被缓存清理中断。表情去重最多 4096 条、保留 10 分钟，按机器人实例和平台隔离。SharpMP4 的内部中间存储关闭后立即删除，投递媒体仍由宿主临时目录策略管理。诊断工具见 [内存测试说明](Tests/MemoryRetentionVerification/README.md)。

### 内存资源回收

卡片封面最长边限制为 1600 物理像素；JPEG 等支持缩小解码的格式在解码阶段降采样。PNG 等格式可能仍需解码原始像素。头像优先缩小解码，保留中心裁剪与透明度。解析与 Silk 编码不增加串行或并发限制；`max_pending_parse_tasks` 仅保留之前的任务积压保护。

处理结束后释放位图、临时封装文件和工作目录索引；有期限的缓存自动清理。进程 RSS 同时包含 .NET 堆保留空间、原生分配和加载的库，不能等同于存活对象大小。长音频 PCM 使用临时文件映射，并在任务结束释放。

### 音频编码内存与取消

MP3/AAC 转 Silk 使用固定缓冲解码器，把 24 kHz 单声道 PCM 写入私有临时文件，并通过只读内存映射交给 Silk 编码器。任务结束释放映射并删除 PCM 中间文件，避免整段浮点/PCM 数组留在托管堆里等待 GC；Silk 数据包直接写文件。PCM 临时磁盘空间约为每秒 48 KB，5 分钟约 14.4 MB。常见小端平台无整段托管 PCM 数组；大端平台需要额外转换，未在当前 macOS 测试中覆盖。未新增并发限制。

每次编码输出检查取消，取消/失败立即清理私有工作目录和不完整输出；成功输出仍按宿主保留策略管理。原生 AAC 解码遇到当前库不支持的帧时，使用已配置或 PATH 中已有的 ffmpeg 回退为 PCM，然后由 SilkCodec.NET 编码；未找到 ffmpeg 时返回明确错误。回退 stderr 保留末尾 4096 字符，取消或超时会结束并等待子进程退出。

媒体处理优先 SharpMP4：普通合轨、循环视频和支持的音频解码均先走原生路径，失败后使用 ffmpeg 回退。静态图片生成视频等 SharpMP4 尚不支持的功能仍使用 ffmpeg；Silk 编码由 SilkCodec.NET 完成。
