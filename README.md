# Shirobot.Plugin.MyParser

<p align="center"><img src="./Assets/icon.png" alt="MyParser" width="480" /></p>

[ShiroBot](https://github.com/ShirokaProject/ShiroBot) 的多平台链接解析插件，自动识别链接并发送卡片、视频或音频。

## 支持平台

Bilibili、抖音、小黑盒、网易云音乐、微信视频号、YouTube、X (Twitter)。

## 安装与使用

本项目遵循 [ShiroBot 插件开发规范](https://docs.shiroka.org/plugin/)，安装、构建、部署、调试与发布请参考官方文档。要求宿主 1.0.0 及以上；构建前需先执行 `git submodule update --init --recursive` 拉取 SharpMP4 子模块。

装好后直接发送支持的平台链接即可自动解析，设置见 `config.toml`，保存后由宿主自动应用。

## 说明

本项目用于个人学习和实验，随缘维护；第三方平台接口变化可能导致解析失效。请仅处理自己拥有权利或已获授权的内容，并遵守平台规则及适用法律。

## 贡献者

感谢每一位为本项目贡献代码、反馈问题的朋友。

<a href="https://github.com/ShirokaProject/Shirobot.Plugin.MyParser/graphs/contributors">
  <img src="https://contrib.rocks/image?repo=ShirokaProject/Shirobot.Plugin.MyParser" alt="贡献者" />
</a>

## 协议

[Apache License 2.0](./LICENSE)
