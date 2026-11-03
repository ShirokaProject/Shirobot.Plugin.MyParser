# Shirobot.Plugin.MyParser
、
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

配置项可在插件配置界面或 `config.toml` 中查看和修改

## 构建

需要 .NET 10 SDK，并初始化 SharpMP4 子模块：

```bash
git submodule update --init --recursive
dotnet build MyParser.slnx -c Release -p:CopyPluginToHost=false
dotnet publish src/MyParser/Shirobot.Plugin.MyParser.csproj -c Release -p:CopyPluginToHost=false -o release/Shirobot.Plugin.MyParser
```

## 说明

本项目主要用于个人学习和实验，随缘维护；第三方平台接口和风控变化可能导致解析失效。请仅处理自己拥有权利或已获授权的内容，遵守平台规则及适用法律，不用于侵权、绕过访问限制或公开代下服务。

许可证：[Apache License 2.0](./LICENSE)。

配置通过宿主统一接口应用；保存后等待应用完成。修改 config.toml 也由宿主监听并应用，无需重新加载插件。
