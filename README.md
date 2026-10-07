# Shirobot.Plugin.MyParser

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

## 安装

直接在Shirobot插件市场安装 / 从Release下载zip解压dll放入plugins插件目录。

兼容要求：ShiroBot 宿主最低版本为 0.9.8，插件使用 ShiroBot.SDK 0.9.8（API 0.9.2）。


## 使用

直接发送支持的平台链接，插件会按自动解析开关和config.toml配置处理。

## 配置

完整默认配置见 [config.example.toml](./config.example.toml)，按通用设置和各平台集中排列。已有配置的顺序由宿主保留，可参照示例整理并保留自己的值。

抖音动态图片会用 SharpMP4 循环并合成配乐；静图生成视频以及不支持的媒体回退需要本机安装 `ffmpeg` 和 `ffprobe`。

解析视频需要现在cookies/xxx.txt中配置好cookie。然后通过 ShiroBot 配置界面或 `config.toml` 调整自动解析、视频大小上限、请求超时和媒体发送策略等参数，修改后自动重载。

## 构建

需要 .NET 10 SDK，并初始化 SharpMP4 子模块：

```bash
git submodule update --init --recursive
dotnet build MyParser.sln -c Release -p:CopyPluginToHost=false
dotnet publish MyParser/Shirobot.Plugin.MyParser.csproj -c Release -p:CopyPluginToHost=false -o release/Shirobot.Plugin.MyParser
```

源码按主插件（`MyParser/`）、共享层（`MyParser.Sharing/`）和平台模块（`MyParser.Provider.*/`）组织。主插件构建时包含各平台源码，发布时合并依赖。

## 说明

本项目主要用于个人学习和实验，随缘维护；第三方平台接口和风控变化可能导致解析失效。请仅处理自己拥有权利或已获授权的内容，遵守平台规则及适用法律，不用于侵权、绕过访问限制或公开代下服务。

本项目与第三方平台无隶属或背书关系。许可证：[Apache License 2.0](./LICENSE)。
