# Jeby Player 1.0.0-beta.2

适用于 Windows 10 / 11 x64 的 Emby 桌面播放器。

## 本次更新

- 启动文件统一命名为 `JebyPlayer.exe`。
- 更新首页截图和安装说明。
- 部署检查同时识别旧版与新版进程，设置和登录信息保持兼容。

## 下载与安装

下载 [Windows 应用包](https://github.com/blackriki/jeby-player/releases/download/v1.0.0-beta.2/JebyPlayer-1.0.0-beta.2-win-x64-selfcontained.zip)，完整解压到新目录，双击 **`JebyPlayer.exe`** 启动。

应用包自带 .NET 和 MPV，无需额外安装运行时。从 beta.1 升级时，请退出旧版后启动新版。`SHA256SUMS.txt` 用于校验下载文件。

## 界面预览

### 首页

![首页](images/home.png)

### 电影详情

![电影详情](images/movie-details.png)

### 电视剧详情

![电视剧详情](images/series-details.png)

### 播放页

![播放页](images/player.png)

## 兼容性与反馈

当前支持 Emby，暂不支持 Jellyfin。问题反馈请提交至 [Issues](https://github.com/blackriki/jeby-player/issues)，附应用版本、系统版本和复现步骤。

项目代码采用 GPL-3.0-or-later，第三方组件遵循各自许可。本版本沿用 beta.1 的 MPV 运行时，其 [完整对应源码](https://github.com/blackriki/jeby-player/releases/download/v1.0.0-beta.1/JebyPlayer-1.0.0-beta.1-mpv-corresponding-sources.zip) 可独立下载，日常使用无需下载。
