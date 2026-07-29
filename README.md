<div align="center">

<p><strong><span style="font-size: 2.2em;">Shirobot.Plugin.MyParser</span></strong></p>

<p><img src="./Assets/icon.png" alt="Shirobot.Plugin.MyParser icon" width="260" /></p>

<p><em>一个面向 <a href="https://github.com/ShirokaProject/ShiroBot">ShiroBot</a> 的多平台内容解析与转发插件。</em></p>

</div>

## 简介

Shirobot.Plugin.MyParser 是 [ShiroBot](https://github.com/ShirokaProject/ShiroBot) 的多平台内容解析插件。

当前支持抖音、Bilibili、小红书、小黑盒、网易云音乐和微信视频号等平台的内容解析、卡片渲染、媒体发送、登录态 / Cookie 管理与自动解析流程。

> 维护说明：本项目主要服务个人使用和学习验证，属于随缘维护项目。第三方平台页面结构、接口、风控策略随时可能变化，因此不保证长期稳定、实时适配或及时修复。

## 支持平台

| 平台 | Provider | 文档 |
| --- | --- | --- |
| 抖音 | `MyParser.Provider.Douyin` | [MyParser.Provider.Douyin/README.MD](./MyParser.Provider.Douyin/README.MD) |
| Bilibili | `MyParser.Provider.BiliBili` | [MyParser.Provider.BiliBili/README.MD](./MyParser.Provider.BiliBili/README.MD) |
| 小红书 | `MyParser.Provider.Xiaohongshu` | [MyParser.Provider.Xiaohongshu/README.MD](./MyParser.Provider.Xiaohongshu/README.MD) |
| 小黑盒 | `MyParser.Provider.Heybox` | [MyParser.Provider.Heybox/README.MD](./MyParser.Provider.Heybox/README.MD) |
| 网易云音乐 | `MyParser.Provider.NetEaseCloudMusic` | [MyParser.Provider.NetEaseCloudMusic/README.MD](./MyParser.Provider.NetEaseCloudMusic/README.MD) |
| 微信视频号 | `MyParser.Provider.WeixinChannels` | [MyParser.Provider.WeixinChannels/README.MD](./MyParser.Provider.WeixinChannels/README.MD) |

## 部署

插件目录建议保持如下结构：

```text
plugins/Shirobot.Plugin.MyParser/
  Shirobot.Plugin.MyParser.dll
  myparser-sharing.dll
  QrCodeGenerator.dll
  config.toml                    # 可由 ShiroBot/插件运行时生成或维护
  cookies/                       # 本地 Cookie 文件目录，不要提交到仓库
    bilibili.txt
    douyin.txt
    xiaohongshu.txt
    netease.txt
    heybox.txt
    weixinchannels-yuanbao.txt
  provider/
    myparser-provider-bilibili.dll
    myparser-provider-douyin.dll
    myparser-provider-xiaohongshu.dll
    myparser-provider-heybox.dll
    myparser-provider-neteasecloudmusic.dll
    myparser-provider-weixinchannels.dll
```

部署：

1. 从 [ShiroBot](https://github.com/ShirokaProject/ShiroBot) 仓库下载或构建 ShiroBot 宿主，并先运行一次，让宿主生成 `plugins/` 插件环境。
2. 构建本项目 Release 版本，或下载 GitHub Release 中的 `Shirobot.Plugin.MyParser.zip`。
3. 将 MyParser 主插件文件放到 ShiroBot 生成的插件目录：`plugins/Shirobot.Plugin.MyParser/`。
4. 将 provider dll 放到：`plugins/Shirobot.Plugin.MyParser/provider/`。
5. 在 ShiroBot 配置中启用插件。
6. 按需配置命令前缀、下载目录、超时、登录态、sign 服务和媒体发送策略。
7. 重启或热重载 ShiroBot。

启动时插件会打印 provider 能力概览。如果日志提示未发现 provider module，请优先检查：

- `plugins/Shirobot.Plugin.MyParser/provider/` 是否存在 `myparser-provider-*.dll`。
- `myparser-sharing.dll` 是否与 provider dll 来自同一次构建。
- 是否有旧的 ShiroBot 进程或调试器锁住插件 dll，导致文件没有被成功覆盖。

## 通用功能

- 自动识别聊天中的受支持平台链接。
- 支持手动命令触发：`#parse <链接>`。
- 支持 provider 模块化加载：主插件运行时从 `provider/` 子目录加载 `myparser-provider-*.dll`。
- 支持需要登录态的解析场景，Cookie 文件支持热重载。
- 支持视频 / 音频下载、本地处理、QQ 消息发送和文件发送 fallback。
- 支持 Avalonia 卡片、长图和封面图片渲染。
- 支持合并转发正文与图片。
- 支持消息处理状态表情：开始、完成、失败。
- 支持处理进度日志、质量选择和本地临时文件清理。

## 常用命令

```text
#parser
#parse <链接>
```

平台专用命令请查看对应 provider 文档，例如：

- Bilibili：`#bili-login`、`#bili-cookie-check`
- 抖音：`#douyin-cookie-check`
- 小红书：`#xhs-login`、`#xhs-cookie-check`
- 网易云音乐：`#wyy <歌名/歌手>`、`#wyy-login`、`#wyy-cookie-check`
- 微信视频号：复制 `https://weixin.qq.com/sph/...` 链接，或使用 `#parse <链接>`
- 微信视频号 Cookie：`#wx-cookie <腾讯元宝网页 Cookie>`

说明：登录和 Cookie 检查命令涉及账号凭据，仅允许机器人 Owner/Admin 通过私信触发；群聊和临时会话不会执行。

## 配置

插件通过 [ShiroBot](https://github.com/ShirokaProject/ShiroBot) 配置上下文读取 `MyParserConfig`。常用配置示例：

```json
{
  "FileProtocol": "File",
  "MaxVideoDownloadMegabytes": 1024,
  "FfmpegPath": "",
  "AutoParseDouyinLinks": true,
  "AutoParseBilibiliLinks": true,
  "BilibiliFetchComments": true,
  "BilibiliCommentCount": 10,
  "AutoParseXiaohongshuLinks": false,
  "AutoParseNetEaseCloudMusicLinks": true,
  "EnableNetEaseCloudMusic": true,
  "SendNetEaseCloudMusicIntroCard": true,
  "SendNetEaseCloudMusicLyricCard": true,
  "AutoParseWeixinChannelsLinks": true,
  "EnableHeybox": true,
  "AutoParseHeyboxLinks": true,
  "ParseCommandPrefix": "#parse",
  "XiaohongshuSignServerUrl": "",
  "XiaohongshuSignServerToken": "",
  "XiaohongshuFetchComments": true,
  "XiaohongshuCommentCount": 10,
  "RequestTimeoutSeconds": 15,
  "QuoteReply": false,
  "SendVideoSegment": true,
  "UploadVideoAsFile": false,
  "UploadVideoAsFileOnlyOnVideoSendFailure": true,
  "DeleteLocalVideoAfterSend": true,
  "DeleteLocalVideoDelaySeconds": 0,
  "AutoFallbackQualityBySize": true,
  "LogDownloadProgress": true,
  "ParallelDownloadThreads": 16,
  "SendNetEaseMobileBestRecord": false
}
```

说明：`FfmpegPath` 仅在 SharpMP4 不支持当前媒体时作为回退使用。旧配置项如 `SendVideoAsFile`、`SendVideoSegmentAsBase64`、`VideoSegmentBase64MaxMegabytes`、`AllowLanAccessToLocalVideoHttpServer`、`MaxImagesToShow` 已不再作为当前开发流程推荐配置；视频发送 URI 协议统一使用 `FileProtocol`，可选 `File` / `Base64` / `Http`。

平台配置细节请查看各 provider README。微信视频号解析依赖腾讯元宝 Cookie，推荐通过 Owner/Admin 私信机器人发送 `#wx-cookie <Cookie>` 写入 `cookies/weixinchannels-yuanbao.txt`，也可手动编辑该文件。

## 项目结构

当前采用“主插件 + Provider 模块 + 共享抽象层”的拆分结构：

```text
MyParser.sln
Directory.Packages.props              # 集中管理 NuGet 包版本
Directory.Build.targets               # 本地 Debug 构建/复制辅助逻辑

MyParser/                             # 主插件项目：Shirobot.Plugin.MyParser
  MyParserPlugin.cs                   # 插件入口、provider 发现与调度
  ProviderHostServices.cs             # 向 provider 暴露宿主能力
  LocalVideoHttpServer.cs             # 本地媒体 HTTP 服务
  MessageHandling/Common/             # 主插件通用消息/图片/并发工具
  Services/                           # 下载器、进度日志等宿主通用服务
  Utility/                            # 宿主侧工具函数

MyParser.Sharing/                     # provider 共享抽象层，输出 myparser-sharing.dll
  Parsing/                            # provider contract、DTO、base class、注册表
  PluginConfig.cs                     # 插件配置模型
  Utility/MyParserRuntime.cs          # 轻量运行时状态工具

MyParser.Provider.BiliBili/           # Bilibili provider，输出 myparser-provider-bilibili.dll
MyParser.Provider.Douyin/             # 抖音 provider，输出 myparser-provider-douyin.dll
MyParser.Provider.Xiaohongshu/        # 小红书 provider，输出 myparser-provider-xiaohongshu.dll
MyParser.Provider.Heybox/             # 小黑盒 provider，输出 myparser-provider-heybox.dll
MyParser.Provider.NetEaseCloudMusic/  # 网易云音乐 provider，输出 myparser-provider-neteasecloudmusic.dll
MyParser.Provider.WeixinChannels/     # 微信视频号 provider，输出 myparser-provider-weixinchannels.dll
```

主插件不直接引用具体 provider 实现；运行时会从插件目录下的 `provider/` 子目录加载 `myparser-provider-*.dll`，并通过 `IMyParserProviderModule` 发现各平台能力。

### 模块职责

- `MyParser`：保留 ShiroBot 插件入口、消息调度、下载、上传、渲染、本地 HTTP、缓存和清理等宿主能力。
- `MyParser.Sharing`：只放 provider contract、DTO、轻量 base class 和纯工具，不承载主插件具体业务实现。
- `MyParser.Provider.*`：只负责平台差异，包括 URL 识别、请求头、平台解析、平台模型、登录/Cookie 抽象、平台消息处理、平台卡片 View/ViewModel 等。具体下载、转码、SILK、图片获取、VideoSegment URI 构造等通用能力统一通过 `IProviderHostServices` 交给主插件。

## Provider 开发流程

新增或维护 provider 时，建议按下面的流程处理。

### 1. 创建 provider 项目

provider 项目统一放在仓库根目录，命名为：

```text
MyParser.Provider.<Platform>/
```

项目文件建议保持以下约定：

```xml
<PropertyGroup>
  <TargetFramework>net10.0</TargetFramework>
  <Nullable>enable</Nullable>
  <ImplicitUsings>enable</ImplicitUsings>
  <AssemblyName>myparser-provider-<platform></AssemblyName>
  <RootNamespace>MyParser.Provider.<Platform></RootNamespace>
</PropertyGroup>

<ItemGroup>
  <ProjectReference Include="..\MyParser.Sharing\MyParser.Sharing.csproj" />
</ItemGroup>
```

如 provider 需要 Avalonia 卡片渲染，再引用：

```xml
<PackageReference Include="ShiroBot.AvaloniaSdk" />
```

如 provider 需要二维码能力，再引用：

```xml
<PackageReference Include="Net.Codecrete.QrCodeGenerator" />
```

包版本统一在 `Directory.Packages.props` 中维护，provider 项目里不要单独写版本号。

### 2. 实现 provider module

每个 provider 需要提供一个实现 `IMyParserProviderModule` 的 module 类型，推荐继承 `MyParserProviderModuleBase`：

```csharp
public sealed class ExampleProviderModule : MyParserProviderModuleBase
{
    public override string Id => "example";
    public override string DisplayName => "Example";

    public override IReadOnlyList<IParseProvider> CreateProviders(PluginConfig config)
    {
        return [new ExampleParseProvider(config)];
    }
}
```

如果 provider 需要接管消息发送流程，实现 `IProviderMessageHandlerFactory`，并返回平台自己的 `IProviderMessageHandler`。

运行时主插件会扫描 `provider/myparser-provider-*.dll`，自动创建 module，不需要在主插件中手动 `new` provider 类型。

### 3. 实现解析与平台差异

provider 内部只放平台相关逻辑，常见目录如下：

```text
Parsing/          # URL 识别、ParseProvider、Parser
Models/           # 平台响应 DTO 和解析结果模型
Infrastructure/   # 平台 HTTP client、headers、签名等基础设施
Services/         # 平台解析服务
MessageHandling/  # 平台消息处理入口
MessageHandling/Impl/ # 较大的 partial handler 实现
Utilities/        # 平台专用纯工具
Views/            # Avalonia View 和 ViewModel
```

### 4. 使用 HostServices，不反向依赖主插件实现

provider 不应直接引用主插件中的具体实现，也不要在主插件里直接引用 provider 具体类型。需要宿主能力时，通过 `IProviderHostServices` 使用：

- 回复文本、发送图片、发送 reaction。
- 下载、Range 下载、进度日志和缓存。
- 音视频分离下载、SharpMP4 合并、fMP4 直播回溯拼接、网易云 MP3 纯托管解码与 SILK Record 构造；不支持的媒体格式回退 ffmpeg。
- 上传本地视频 / 音频文件。
- 远程图片获取与图片解码。
- VideoSegment URI 构造，包括 `File` / `Base64` / `Http` 策略。
- 并发保序选择工具。
- 临时文件清理策略。

如果发现多个 provider 重复实现相同宿主能力，优先把能力下放到 `MyParser/ProviderHostServices.cs` 或共享 contract，而不是复制到各 provider。

### 5. 保持 Sharing 轻量

`MyParser.Sharing` 只能放 provider 需要共享的抽象和轻量工具，例如：

- provider contract / interface
- DTO / record
- base class
- 纯工具方法

不要把下载器、本地 HTTP 服务、上传实现、渲染实现、消息发送实现等宿主业务放进 `MyParser.Sharing`。

### 6. 接入 solution、构建和运行时复制

新增公开 provider 后，需要同步更新：

- `MyParser.sln`：把 provider 项目加入 `Providers` solution folder。
- `Directory.Build.targets`：把 provider csproj 加入 `MyParserProviderProject`，方便本地 Debug 构建时自动复制。
- `.github/workflows/build.yml`：把 provider 路径和产物检查加入 CI。
- `.github/workflows/release.yml`：把 provider dll 加入 release 包。

本地调试时，`Directory.Build.local.props` 中配置好 `HostPluginDir` 后，构建主插件或 provider 会把产物复制到：

```text
plugins/Shirobot.Plugin.MyParser/
plugins/Shirobot.Plugin.MyParser/provider/
```

### 7. 验证 checklist

提交前建议至少检查：

```bash
dotnet build MyParser.sln -c Release -p:CopyPluginToHost=false -p:BuildMyParserProvidersOnMainPluginBuild=false
```

并确认：

- 主插件不直接引用 provider 具体类型。
- provider 只引用 `MyParser.Sharing` 和必要 NuGet 包，不引用主插件项目。
- provider dll 名称符合 `myparser-provider-*.dll`。
- 启动日志能看到 provider module 和 provider 能力概览。
- 没有提交 Cookie、Token、`Directory.Build.local.props`、`bin/`、`obj/` 等本地文件。
- 如果提交 `launchSettings.json`，确认其中路径是团队约定可用路径，或改为不提交并使用本机 Rider 配置。

## 构建

还原与构建整个 solution：

```bash
dotnet restore MyParser.sln
dotnet build MyParser.sln -c Release -p:CopyPluginToHost=false -p:BuildMyParserProvidersOnMainPluginBuild=false
```

也可以单独构建主插件或某个 provider：

```bash
dotnet build MyParser/Shirobot.Plugin.MyParser.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.BiliBili/MyParser.Provider.BiliBili.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.Douyin/MyParser.Provider.Douyin.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.Xiaohongshu/MyParser.Provider.Xiaohongshu.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.Heybox/MyParser.Provider.Heybox.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.NetEaseCloudMusic/MyParser.Provider.NetEaseCloudMusic.csproj -c Release -p:CopyPluginToHost=false
dotnet build MyParser.Provider.WeixinChannels/MyParser.Provider.WeixinChannels.csproj -c Release -p:CopyPluginToHost=false
```

Release 构建产物主要位于：

```text
MyParser/bin/Release/net10.0/Shirobot.Plugin.MyParser.dll
MyParser/bin/Release/net10.0/myparser-sharing.dll
MyParser.Provider.BiliBili/bin/Release/net10.0/myparser-provider-bilibili.dll
MyParser.Provider.Douyin/bin/Release/net10.0/myparser-provider-douyin.dll
MyParser.Provider.Xiaohongshu/bin/Release/net10.0/myparser-provider-xiaohongshu.dll
MyParser.Provider.Heybox/bin/Release/net10.0/myparser-provider-heybox.dll
MyParser.Provider.NetEaseCloudMusic/bin/Release/net10.0/myparser-provider-neteasecloudmusic.dll
MyParser.Provider.WeixinChannels/bin/Release/net10.0/myparser-provider-weixinchannels.dll
```

### 本地调试配置

`Directory.Build.local.props` 用于把 Debug 构建产物直接输出到 ShiroBot 插件目录，属于本机私有配置，不应提交到仓库。

示例：

```xml
<Project>
  <PropertyGroup>
    <HostPluginDir>C:\Path\To\ShiroBot\bin\Debug\net10.0\plugins\Shirobot.Plugin.MyParser\</HostPluginDir>
  </PropertyGroup>
</Project>
```

配置好后，普通 Debug 构建会直接生成到宿主插件目录：

```bash
dotnet build -c Debug
```

### Rider 调试配置

主插件和 provider 都可以使用 `Properties/launchSettings.json` 创建 Rider / .NET 启动配置。当前本机调试约定指向真实 ShiroBot 宿主：

```json
{
  "$schema": "https://json.schemastore.org/launchsettings.json",
  "profiles": {
    "Run Host": {
      "commandName": "Executable",
      "executablePath": "C:\Path\To\ShiroBot\bin\Debug\net10.0\ShiroBot.exe",
      "workingDirectory": "C:\Path\To\ShiroBot\bin\Debug\net10.0",
      "commandLineArgs": ""
    }
  }
}
```

调试 provider 时推荐流程：

1. 确认 `Directory.Build.local.props` 中的 `HostPluginDir` 指向同一个 ShiroBot Debug 输出目录下的 `plugins/Shirobot.Plugin.MyParser/`。
2. 在 Rider 中选择对应 provider 或主插件的 `Run Host` 配置。
3. 先构建 provider；`Directory.Build.targets` 会复制 provider dll 到 `provider/`，并在需要时更新主插件。
4. 启动 `Run Host`，断点可打在 provider 的 Parser / MessageHandler / ViewModel 中。

如果新增 provider 希望在 Rider 中直接运行宿主，可以复制上述 `launchSettings.json` 到该 provider 的 `Properties/` 目录，并按本机路径调整 `executablePath` / `workingDirectory`。

## 热重载

插件运行时会监听以下文件变化并自动重载：

- 插件目录下的 `config.toml`：更新普通运行配置，例如自动解析开关、下载策略、sign 服务等。
- 插件目录下的 Cookie 文件：更新各平台 Cookie。

当前常用 Cookie 文件：

```text
cookies/bilibili.txt
cookies/douyin.txt
cookies/xiaohongshu.txt
cookies/netease.txt
cookies/heybox.txt
cookies/weixinchannels-yuanbao.txt
```

Cookie 文件清空或删除后，对应平台登录态会立即清空；文件重新创建或写入有效 Cookie 后会自动生效。

> 固定命令不走配置项。`#parse` 前缀支持随配置热重载。

## 许可证

本项目使用 Apache License 2.0。
详见 [LICENSE](./LICENSE)。

## 合规与免责声明

本项目仅用于技术学习、技术交流、插件开发学习和个人实验，不构成法律意见。使用者必须自行确认使用行为具备合法授权，并自行承担由使用行为产生的全部责任。

本项目与抖音、Bilibili、小红书、小黑盒、网易云音乐或其他第三方内容平台、权利方、服务提供方均不存在从属、合作、授权或背书关系。本项目不提供、托管、索引、售卖或分发任何第三方内容，也不保证可访问、获取或处理任何第三方内容。

本项目的使用必须遵守以下边界：

- **不得用于侵权用途**：不得下载、复制、传播、分发、展示或保存未获授权的第三方内容。
- **不得用于规避技术保护措施**：不得绕过 DRM、付费墙、会员限制、验证码、访问控制、加密保护或其他技术保护措施。
- **不得作为公开代下服务**：不得将本项目部署为面向公众的解析、下载、转存、分发或镜像服务。
- **不得批量抓取或滥用请求**：不得用于高频请求、批量采集、镜像站点、数据售卖，或任何可能影响第三方服务稳定性的行为。
- **不得移除权利信息**：不得删除、隐藏或篡改版权声明、作者信息、水印、来源标识或其他权利管理信息。
- **不得提交敏感信息**：不得将 Cookie、Token、个人账号、群号、聊天记录、本地配置或其他敏感信息提交到公开仓库。
- **必须遵守第三方规则**：使用者必须自行阅读并遵守目标网站的用户协议、robots 规则、版权政策、隐私政策和当地法律法规。
- **必须仅处理合法授权内容**：使用者只能处理自己拥有权利、已获授权、许可条款允许或法律允许合理使用的内容。

因使用者自行部署、修改、分发、公开服务化或以其他方式使用本项目而产生的任何版权、合同、隐私、数据安全、账号安全、服务条款或其他法律责任，均由使用者自行承担。

如权利人认为本项目中的说明、示例或实现方式存在侵权风险，可以通过 GitHub Issues 联系本项目维护者处理；如认为存在需要正式处理的侵权内容，也可以按照 GitHub 的 DMCA 流程向 GitHub 提交通知。
