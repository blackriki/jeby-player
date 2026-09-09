# Jeby Player 1.0.0-beta.1

首个公开测试版本。适用于 Windows 10 / 11 x64，连接自己的 Emby 服务器即可使用。

## 下载与安装

下载 **[Windows 应用包（约 153 MB）](https://github.com/blackriki/jeby-player/releases/download/v1.0.0-beta.1/JebyPlayer-1.0.0-beta.1-win-x64-selfcontained.zip)**，完整解压后运行 `EmbyPlayer.App.exe`，输入服务器地址并登录。

应用包自带 .NET 和 MPV，无需另装运行时。`mpv-corresponding-sources.zip` 是播放内核及依赖的源码附件，日常使用无需下载。`SHA256SUMS.txt` 用于校验下载文件。

## 主要功能

- 媒体库浏览、搜索、排序与筛选。
- 继续观看、最近添加、收藏、稍后观看和播放队列。
- 电影与电视剧详情、演职人员、艺术图和类似作品。
- 播放进度同步、字幕与音轨切换、默认语言偏好和本地字幕导入。
- 全屏、小窗、倍速、长按加速、快捷键和进度条画面预览。

## 界面预览

### 首页

![首页](images/home.png)

### 电影详情

![电影详情](images/movie-details.png)

### 电视剧详情

![电视剧详情](images/series-details.png)

### 播放页

![播放页](images/player.png)

## 兼容性

当前支持 Emby，暂不支持 Jellyfin。媒体详情、章节和画质选项取决于服务器与影片；首次在本机生成预览图时可能需要等待加载。

## 反馈与许可

遇到问题请通过 [Issues](https://github.com/blackriki/jeby-player/issues) 提交应用版本、系统版本、服务器版本和复现步骤。分享日志前请移除令牌、密码等敏感信息。

项目代码采用 GPL-3.0-or-later，第三方组件保留各自许可。Jeby Player 是独立第三方客户端，不提供影视内容、服务器账号或订阅。
