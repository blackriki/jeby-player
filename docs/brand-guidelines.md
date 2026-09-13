# Jeby Player 品牌规范

## 名称与介绍

- 产品名称：**Jeby Player**。
- 中文介绍：**适用于 Windows 的 Emby 桌面播放器**。
- 英文介绍：**An Emby desktop player for Windows.**
- 项目仓库：[blackriki/jeby-player](https://github.com/blackriki/jeby-player)。
- 下载页面：[Releases](https://github.com/blackriki/jeby-player/releases)。

产品名称保留空格和大小写。当前版本支持 Emby，Jellyfin 支持列入后续计划。Jeby Player 是独立第三方客户端，与 Emby 或 Jellyfin 没有官方隶属关系。

## 应用标识

窗口标题、关于页面、文件产品属性和对外文档统一使用 Jeby Player。

启动文件为 `JebyPlayer.exe`。工程、命名空间、设置和凭据存储继续使用 `EmbyPlayer` 标识，以兼容既有版本。Emby 在界面和协议中表示所连接的服务器类型。

## 视觉规范

界面采用深色背景与绿色强调色，优先展示媒体内容。动作图标以简洁实心风格为主，相同功能复用同一图标资源，尺寸按所在控件统一。

程序和窗口使用 `src/EmbyPlayer.App/Assets/app-icon.ico`；关于页面通过资源链接复用 `app-icon.png` 高清图。其他图标的位置与用途见 [素材清单](ASSET_PROVENANCE.md)。

## 许可与内容说明

项目自有代码采用 GNU GPL 第 3 版或后续版本，SPDX 标识为 `GPL-3.0-or-later`，条款见 [LICENSE](../LICENSE)。第三方组件保留各自的许可和署名。

客户端连接使用者自己的媒体服务器，不提供影片、服务器账号或订阅内容。
