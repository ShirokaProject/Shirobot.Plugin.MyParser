# 配置热重载检查

检查范围：本地 ShiroBot 宿主源码及 MyParser。修复宿主源码后，需要更新运行中的宿主并重启一次；随后配置保存自动应用。已将修复后的宿主安装到 `.shirobot-dev/host/osx-arm64/`，并通过 Rider 重新启动。原下载版可执行文件保存在 `.host-backups/downloaded-1.0.0/ShiroBot`。

## 宿主

| 参数 | 保存后的行为 |
| --- | --- |
| `avalonia_theme` | 下一次使用 `RenderTheme.Auto` 的渲染跟随宿主主题。宿主设为 `Auto` 时才按时间切换。已发送的 PNG 不变。 |
| `enable_log`、`showid` | 即时应用。 |
| `owner_list`、`admin_list`、`plugin_routes` | 即时更新权限及后续事件路由。 |
| `github_proxy` | 已修复文件监听器漏更新 Updater 的问题；下一次使用该代理的更新请求读取新值。 |
| `host_update_repository` | 下一次检查宿主更新读取新值，监听器同步活动配置。 |
| `api.token` | 已补齐手工改文件时的令牌刷新。运行中不能清空令牌；日志不输出新令牌。 |
| `protocols`、`disable_console_input` | 手工改文件需重启。Dashboard 的适配器启停操作会额外更新运行状态。 |
| `api.enable`、`api.enable_dashboard`、`api.listen_urls`、`api.public_base_url` | 服务、端口、路由及链接地址在启动时创建，需重启。 |

主题原因为宿主的 `ApplyRenderTheme` 将每次渲染传入的 `RenderTheme.Auto` 直接解释为按时间选择主题，从而覆盖已经热重载的 `avalonia_theme = "Light"`。已改为继承宿主设置。显式指定 Light/Dark 的第三方插件仍可覆盖宿主主题；MyParser 的 B 站专栏和抖音评论原来写死主题，现已统一使用 Auto。

验证覆盖：真实配置文件原子替换及普通保存、PNG 像素随 Light/Dark 切换、显式主题覆盖后 Auto 恢复宿主设置、权限及路由更新、API 令牌更新、GitHub 代理设置和清空。

## MyParser

插件热重载回调会把新配置写入原配置对象，保留现有提供商对该对象的引用。下列设置在后续请求或相关操作中读取：

- Cookie：B 站、抖音、网易云音乐、小黑盒、腾讯元宝；保存后验证并更新运行时凭据。
- 平台启用、自动解析、封面、歌词、评论、直播回溯等开关和数量限制。
- 开发模式、视频发送、文件上传、引用回复、错误提示及提示模板。
- 文件 URI 协议、大小限制、画质和编码偏好、下载并发数、日志、Silk、ffmpeg 路径、发送后清理及清理延迟。

仍存在的网络配置限制：

| 参数 | 当前限制 |
| --- | --- |
| `request_timeout_seconds` | B 站、抖音、小黑盒、视频号、X、网易云的解析客户端在创建时缓存超时；保存不会重建客户端，需要重载 MyParser。YouTube、下载探测等按请求读取。抖音/小黑盒/X 上限 60 秒，B 站 120 秒，网易云/YouTube 300 秒，与配置页统一 300 秒上限不完全一致。 |
| `http_proxy` | 视频和图片下载、YouTube 后续操作读取新值；X 的解析客户端及代理状态在创建时缓存，需要重载 MyParser。B 站/抖音/小黑盒/网易云/视频号的解析 API 客户端未接入这个代理参数，重载也不会使这些 API 使用它。 |

这些网络问题属于客户端的配置应用范围和生命周期问题，本次没有修改其传输实现。修改插件 TOML 后出现“配置已热重载”日志，只能证明配置对象更新，不能证明已创建的网络客户端重建。
