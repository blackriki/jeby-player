# 界面素材清单

本清单列出应用内置图标及其维护位置。第三方组件的来源与许可见 [第三方组件](THIRD_PARTY_NOTICES.md)。

| 素材 | 文件 | 用途 |
| --- | --- | --- |
| 应用图标 | [app-icon.ico](../src/EmbyPlayer.App/Assets/app-icon.ico) | 程序文件和窗口图标 |
| 应用图标高清图 | [app-icon.png](../src/EmbyPlayer.App/Assets/app-icon.png) | 关于页面，通过资源链接复用 |
| 媒体库图标 | `src/EmbyPlayer.UI/Assets/HomeIcons/` | 电影、剧集、动画、合集和播放列表入口 |
| 矢量动作图标 | [Icons.xaml](../src/EmbyPlayer.UI/Resources/Icons.xaml) | 播放、收藏、稍后观看、待播、设置及其他界面操作 |

动作图标以 WPF 几何资源定义，相同功能共用同一资源。收藏、时钟、设置、播放和待播等实心图标包含项目内绘制的几何路径。

海报、艺术图、演员图片和播放画面来自连接的媒体服务器，不属于应用内置图标。文档中的产品截图见 [截图清单](images/README.md)。
