# MyParser.Provider.WeixinChannels

微信视频号 provider，负责 `weixin.qq.com/sph/...` 视频号分享链接解析、卡片渲染和视频下载发送。

## 支持能力

- 识别微信视频号分享短链：
  - `https://weixin.qq.com/sph/<id>`
- 通过腾讯元宝网页接口解析分享链接。
- 获取视频号详情：
  - 标题 / 描述
  - 作者名称
  - 作者头像
  - 视频封面
  - 视频播放地址
  - 点赞 / 收藏 / 评论 / 转发文案
  - 发布时间
- 渲染微信视频号卡片。
- 下载视频并发送 `VideoSegment`。
- 支持失败时上传视频文件 fallback，具体由主插件配置控制。

## 使用方式

直接发送或使用 `#parse`：

```text
https://weixin.qq.com/sph/<分享短链ID>
#parse https://weixin.qq.com/sph/<分享短链ID>
```

插件会：

1. 解析分享链接。
2. 渲染视频号卡片。
3. 下载视频。
4. 发送视频消息。

## Cookie 要求

微信视频号分享链接本身不直接暴露完整视频详情和可下载视频 URL。当前 provider 采用轻量解析链路：

```text
weixin.qq.com/sph/...
  -> 腾讯元宝 get_parse_result
  -> channels.weixin.qq.com finder-preview get_feed_info
  -> 视频详情 / 视频 URL
```

因此需要腾讯元宝网页 Cookie。

推荐使用私信命令写入 Cookie：

```text
#wx-cookie <腾讯元宝网页 Cookie>
```

示例：

```text
#wx-cookie hy_user=...; uid=...; xxx=...
```

也支持带 `Cookie:` 前缀：

```text
#wx-cookie Cookie: hy_user=...; uid=...; xxx=...
```

命令限制：

- 仅允许机器人 Owner/Admin 使用。
- 仅允许私信机器人使用。
- 不要在群聊中发送 Cookie。

写入后 Cookie 会保存到：

```text
plugins/Shirobot.Plugin.MyParser/cookies/weixinchannels-yuanbao.txt
```

也可以手动编辑该文件。主插件会监听 Cookie 文件变化并热重载。

## 配置项

常用配置：

```json
{
  "AutoParseWeixinChannelsLinks": true,
  "SendVideoSegment": true,
  "UploadVideoAsFile": false,
  "UploadVideoAsFileOnlyOnVideoSendFailure": true,
  "FileProtocol": "File"
}
```

说明：

- `AutoParseWeixinChannelsLinks`：是否自动解析聊天中的视频号分享链接。
- `SendVideoSegment`：是否下载并发送视频。
- `UploadVideoAsFile`：是否额外上传为群/私聊文件。
- `UploadVideoAsFileOnlyOnVideoSendFailure`：`VideoSegment` 失败后再上传文件。
- `FileProtocol`：视频发送 URI 协议：`File` / `Base64` / `Http`。

## Cookie 获取建议

如果网页禁用 F12，可以尝试：

1. 浏览器菜单打开开发者工具：
   - Chrome / Edge：右上角三点 -> 更多工具 -> 开发者工具。
   - 快捷键：`Ctrl + Shift + I` 或 `Ctrl + Shift + J`。
2. 登录：

```text
https://yuanbao.tencent.com/
```

3. Network 面板中找：

```text
yuanbao.tencent.com/api/...
```

4. 复制请求头里的完整 Cookie。
5. 私信机器人：

```text
#wx-cookie <Cookie>
```

也可以使用浏览器 Cookie 管理界面或 Cookie 导出扩展导出 `yuanbao.tencent.com` 的 Cookie。

> Cookie 等同登录态，请不要发送到群聊、不要提交到 git、不要分享给他人。

## 实现边界

provider 只负责微信视频号平台差异：

- URL 识别。
- 元宝接口请求头。
- 视频号接口请求头。
- 响应 DTO。
- 视频详情字段映射。
- 卡片 View / ViewModel。

通用能力交给主插件：

- 图片下载与 base64 构造。
- 视频下载。
- `VideoSegment` URI 构造。
- 文件上传 fallback。
- 临时文件清理。

## 已知限制

- 腾讯元宝 Cookie 失效后需要重新写入。
- 分享链接解析依赖腾讯元宝接口和 `channels.weixin.qq.com` 接口，接口或风控变化可能导致解析失败。
- 视频播放地址通常有时效性，不建议长期缓存为外部可用链接。
- 当前不接入完整微信 PC 代理 / 证书 / 拦截方案，只支持复制 `weixin.qq.com/sph/...` 链接解析。

## 构建

单独构建：

```bash
dotnet build MyParser.Provider.WeixinChannels/MyParser.Provider.WeixinChannels.csproj -c Release -p:CopyPluginToHost=false
```

构建整个 solution：

```bash
dotnet build MyParser.sln -c Release -p:CopyPluginToHost=false -p:BuildMyParserProvidersOnMainPluginBuild=false
```

产物：

```text
MyParser.Provider.WeixinChannels/bin/Release/net10.0/myparser-provider-weixinchannels.dll
```

部署到：

```text
plugins/Shirobot.Plugin.MyParser/provider/myparser-provider-weixinchannels.dll
```
