# 第三方组件

Jeby Player 的自有代码采用 [GPL-3.0-or-later](../LICENSE)。第三方组件遵循各自的许可证，原文与署名随发布包提供。

## MPV 播放内核

当前版本使用 MSYS2 的 MPV 0.41.0-7（Windows x64）。运行时包含 132 个 DLL；依赖版本、文件哈希及 493 项许可文件记录见 [运行时清单](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json)。发布包中的许可文件位于 `third-party/mpv/`。

| 项目 | 值 |
| --- | --- |
| 上游包 | [mingw-w64-x86_64-mpv-0.41.0-7-any.pkg.tar.zst](https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-mpv-0.41.0-7-any.pkg.tar.zst) |
| 构建配方 | [msys2/MINGW-packages](https://github.com/msys2/MINGW-packages/tree/052099e63e69816e35b05f28c852a5209c4dd1e0) |
| 主文件 | `libmpv-2.dll`，版本 `v0.41.0`，3,010,315 字节 |
| 主文件 SHA256 | `808744f489a235d390020b4b1baba956b2427a6ca20c4e3846a1c33851a9da25` |
| 上游包 SHA256 | `4b958c3a705bbf196b3efd6f2c5809c9f5c59e4ff0fa4f4dc03deaccbe16eba2` |

### 对应源码

[Releases](https://github.com/blackriki/jeby-player/releases) 提供独立的 MPV 对应源码附件，包含依赖源码及构建材料。使用播放器无需下载或解压该附件。

- 文件：`JebyPlayer-1.0.0-beta.1-mpv-corresponding-sources.zip`
- 大小：1,609,619,309 字节
- SHA256：`00b5fd1972d5776abeb23b174bca9960e3b6f637261c8270aa39ffa3364ff9c7`

## .NET 与 WPF

应用使用 .NET 8 WPF。1.0.0-beta.1 的 SelfContained 包包含 .NET 8.0.30，相关许可位于 `third-party/dotnet/`：

- `Microsoft.NETCore.App/8.0.30/LICENSE.TXT`
- `Microsoft.NETCore.App/8.0.30/THIRD-PARTY-NOTICES.TXT`
- `Microsoft.WindowsDesktop.App/8.0.30/LICENSE`

FrameworkDependent 构建需要另行安装 x64 .NET 8 Desktop Runtime。

## 开发与测试依赖

测试依赖包括 Microsoft.NET.Test.Sdk、MSTest、Microsoft.TestPlatform / Testing.Platform 及其传递依赖，具体版本记录在各测试项目的 `packages.lock.json` 中。这些工具通过 NuGet 还原，不包含在应用发布包中。

各包的许可见对应 `.nuspec` 和包内许可证文件；部分 Microsoft.Testing 扩展采用 Microsoft .NET Library 条款。

## 字体与媒体素材

应用使用 Windows 系统字体，不附带字体文件。内置图标的位置和用途见 [素材清单](ASSET_PROVENANCE.md)。

媒体海报、背景和视频内容由连接的媒体服务器提供，其权利归各自权利人所有。文档截图用于展示客户端界面；Jeby Player 不提供截图中的影视内容。
