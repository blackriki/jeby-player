# Jeby Player

**适用于 Windows 的 Emby 桌面播放器**

Jeby Player 使用原生 WPF 界面和 MPV 播放内核，连接你的 Emby 服务器，在桌面上浏览媒体库、管理片单并观看电影和剧集。

[下载 Windows 测试版](https://github.com/blackriki/jeby-player/releases/tag/v1.0.0-beta.1) · [问题反馈](https://github.com/blackriki/jeby-player/issues) · [更新记录](CHANGELOG.md)

## 安装与使用

支持 **Windows 10 / 11 x64**，需要可访问的 Emby 服务器和有效账号。

1. 在发布页下载 `JebyPlayer-1.0.0-beta.1-win-x64-selfcontained.zip`。
2. 完整解压，运行 `EmbyPlayer.App.exe`。
3. 输入服务器地址并登录，选择影片开始播放。

下载包自带 .NET 和 MPV，无需额外安装运行时。请保留解压目录中的依赖文件。发布页的 `mpv-corresponding-sources.zip` 是播放内核及依赖的源码附件，日常使用无需下载。

当前版本为 **1.0.0-beta.1**，支持 Emby，暂不支持 Jellyfin。

## 功能

- **媒体浏览**：首页推荐、继续观看、最近添加，以及媒体库搜索、排序和筛选。
- **电影与剧集详情**：作品介绍、季与单集、演职人员、艺术图和类似作品。
- **个人片单**：收藏、稍后观看、播放队列和最近使用的服务器。
- **播放控制**：进度同步、画质选择、倍速、长按加速、全屏与小窗播放。
- **字幕与音轨**：默认语言偏好、音轨切换、本地字幕导入、字幕时间和位置调整。
- **进度预览**：鼠标悬停查看时间与画面，支持服务器预览图和本机按需生成。

媒体信息、章节及可用画质取决于服务器和影片。本机首次生成预览图时，加载速度受视频和网络影响。

## 界面预览

### 首页

![首页：媒体库、继续观看和最近添加](docs/images/home.png)

### 电影详情

![电影详情：作品介绍与演职人员](docs/images/movie-details.png)

### 电视剧详情

![电视剧详情：季与单集](docs/images/series-details.png)

### 播放页

![播放页：进度条与播放控制](docs/images/player.png)

## 问题反馈

请通过 [GitHub Issues](https://github.com/blackriki/jeby-player/issues) 提交问题，附上应用版本、Windows 版本、Emby Server 版本和复现步骤。诊断日志可从设置中导出；提交前请移除令牌、密码等敏感信息。

## 开发

技术栈：C#、.NET 8、WPF、MVVM、MPV。

构建需要 Windows、[global.json](global.json) 指定的 .NET SDK，以及用于运行和测试的 .NET 8 Desktop Runtime。

```powershell
dotnet restore EmbyPlayer.sln --locked-mode --disable-parallel
dotnet build EmbyPlayer.sln --no-restore
.\scripts\run-app.ps1
```

播放功能还需要与 [运行时清单](src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json) 匹配的 MPV 原生 DLL，放置于 `src/EmbyPlayer.App/runtimes/win-x64/native/`。构建、测试和打包说明见 [开发与发布](docs/PUBLIC_RELEASE.md)。

## 许可

项目代码采用 [GPL-3.0-or-later](LICENSE)。第三方组件遵循各自许可，详见 [第三方组件](docs/THIRD_PARTY_NOTICES.md)。

Jeby Player 是独立第三方客户端，与 Emby 或 Jellyfin 无官方隶属关系。应用不提供影视内容、服务器账号或订阅。
