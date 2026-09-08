# 图标与素材来源记录

本记录说明公开源码中的静态素材及其来源状态。服务器提供的海报、背景和媒体截图不属于应用内置素材。

| 素材 | 维护位置与来源状态 |
| --- | --- |
| 实心动作图标 | [Icons.xaml](../src/EmbyPlayer.UI/Resources/Icons.xaml) 保存实际矢量几何。收藏、时钟、设置、播放及待播等图标具有项目内绘制与修改记录；基本几何形状不声明为项目独占设计。 |
| 应用图标 | [app-icon.png](../src/EmbyPlayer.App/Assets/app-icon.png) 和 [app-icon.ico](../src/EmbyPlayer.App/Assets/app-icon.ico)。窗口和程序使用 ICO，关于页面复用 PNG；原始设计来源仍待补充确认。 |
| 首页媒体库图标 | `src/EmbyPlayer.UI/Assets/HomeIcons/` 中的五张 PNG；原始设计来源仍待补充确认。 |
| 其他 XAML 图标 | 与动作图标共同保存在 `Icons.xaml`；没有已核实来源的路径不标注外部作者或具体许可。 |

缺少元数据不能证明素材侵权，也不能作为授权证明。来源确认或素材替换后，应同步更新本记录与 [第三方清单](THIRD_PARTY_NOTICES.md)。
