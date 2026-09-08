# Jeby Player 第三方组件与素材

核验日期：2026-09-08。项目自有代码采用 [GPL-3.0-or-later](../LICENSE)，第三方组件保留各自许可与署名。本文件说明来源与打包方式；许可证原文随对应产物提供。

## MPV 与 native 依赖

当前运行时使用官方 MSYS2 包 `mingw-w64-x86_64-mpv-0.41.0-7-any.pkg.tar.zst`，已替换早期 shinchiro 开发版。旧版调查记录不代表当前运行时仍缺少来源材料。

[运行时 manifest](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json) 记录：

- 主文件 `libmpv-2.dll`，AMD64，版本 `v0.41.0`，3,010,315 字节。
- 主文件 SHA256：`808744f489a235d390020b4b1baba956b2427a6ca20c4e3846a1c33851a9da25`。
- 官方包 SHA256：`4b958c3a705bbf196b3efd6f2c5809c9f5c59e4ff0fa4f4dc03deaccbe16eba2`。
- 构建配方来自 `msys2/MINGW-packages`，提交 `052099e63e69816e35b05f28c852a5209c4dd1e0`。
- 来源状态 `verified`；运行时集合共 132 个 DLL，许可材料共 493 项文件记录。各依赖版本、文件哈希和许可原文按 manifest 核对，不能把所有依赖统一当作项目自有 GPL 代码。

官方包地址见 manifest 的 `sourceUrl`。应用发布时必须携带完整 DLL 集合，适用许可材料复制到 `third-party/mpv/`。

### 对应源码

对应源码包 `JebyPlayer-1.0.0-beta.1-mpv-corresponding-sources.zip` 已准备，大小 **1,609,619,309 字节**，SHA256：

`00b5fd1972d5776abeb23b174bca9960e3b6f637261c8270aa39ffa3364ff9c7`

该归档需作为同一 Release 的独立附件提供；源码归档不需要解压到应用运行目录。以上记录表示材料已准备，下载地址以正式 Release 为准。

## .NET 与 WPF

应用目标为 .NET 8 WPF。五个应用项目没有外部 NuGet PackageReference；测试工具链不进入应用包。

- FrameworkDependent 包由用户安装 x64 .NET 8 Desktop Runtime。
- SelfContained 包由脚本读取实际 `runtimeconfig.json` 中的版本，从对应 NuGet runtime 包提取 .NET `LICENSE.TXT`、`THIRD-PARTY-NOTICES.TXT` 以及 WindowsDesktop `LICENSE`，复制到 `third-party/dotnet/<framework>/<version>/`，记录长度和 SHA256。

提交 `5679691` 的 SelfContained 产物已通过本地 Public 管线，使用 .NET 8.0.30。上述三份 .NET / WPF 许可原文已随包存在，并记录长度和 SHA256；`distributionReady` 为 `true`。GitHub 上传及远端 CI 尚待完成，不能据此称附件已经可公开下载。

## 开发与测试依赖

四个测试项目通过提交的 `packages.lock.json` 锁定依赖，包括 Microsoft.NET.Test.Sdk、MSTest、Microsoft.TestPlatform / Testing.Platform 及其传递依赖。它们通过 NuGet 还原，不把包缓存和测试输出作为应用附件上传。

各测试包遵循自身 `.nuspec` 和包内许可原文；部分 Microsoft.Testing 扩展使用 Microsoft .NET Library 条款，不能统一标成 MIT 或项目 GPL。测试工具中出现 Telemetry 或 ApplicationInsights 依赖，不代表应用项目引入了相应功能。

## 字体、图标和截图

- 应用不附带字体文件，使用 WPF / Windows 字体解析。
- 窗口和程序使用 `app-icon.ico`，关于页面复用 `app-icon.png`；首页有五张内置媒体库 PNG，相同动作图标复用 XAML 几何。
- 素材来源状态见 [素材记录](ASSET_PROVENANCE.md)。没有证据的来源不推定外部作者或生成工具，也不因品牌更名就声明为重新设计。
- 海报和背景由用户的媒体服务器提供。发布截图使用用户授权展示的实际产品画面，并遮蔽用户名；这些图片不因此被重新授权为本项目自有素材，也不构成附送影视内容。

最终发布前核对实际包内许可证、对应源码附件及公开截图，保留第三方原有署名。流程见 [发布与维护](PUBLIC_RELEASE.md)。
