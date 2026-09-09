# 开发与发布

## 开发环境

- Windows 10 / 11 x64。
- [global.json](../global.json) 指定的 .NET SDK。
- .NET 8 Desktop Runtime，用于运行 WPF 应用和测试。

```powershell
dotnet restore EmbyPlayer.sln --locked-mode --disable-parallel
dotnet build EmbyPlayer.sln --no-restore
.\scripts\run-app.ps1
```

工程、命名空间和可执行文件沿用 `EmbyPlayer` 标识，产品显示名称为 Jeby Player。设置和凭据存储标识保持兼容。

## MPV 运行时

原生 DLL 不纳入 Git 仓库。播放和完整发布验证需要与 [mpv-runtime.json](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json) 匹配的全部 DLL，放置于：

`src/EmbyPlayer.App/runtimes/win-x64/native/`

清单记录主 DLL、依赖、文件哈希和许可证材料。组件来源及对应源码见 [第三方组件](THIRD_PARTY_NOTICES.md)。缺少 MPV 时仍可构建应用和浏览媒体库。

## 测试

```powershell
.\scripts\build-test.ps1
```

完整测试包含 WPF 界面和发布脚本检查，需要 Windows 交互桌面及匹配的原生运行时。[GitHub CI](https://github.com/blackriki/jeby-player/actions) 运行构建、Core / Emby / Player 测试及不依赖交互桌面的 UI 契约测试。实际服务器与播放验收见 [测试计划](TEST_PLAN.md)。

## 打包

在干净的工作树中准备原生运行时和许可文件后执行：

```powershell
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public -PreflightOnly
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public
```

`SelfContained` 包自带 .NET；`FrameworkDependent` 包需要额外安装 .NET 8 Desktop Runtime。

发布脚本执行锁定依赖还原、构建、测试、运行时校验和窗口启动检查，并生成 ZIP 与逐文件哈希清单。输出位于 `.tmp/release-staging/`。带有 `RELEASE-FAILED.txt` 的目录为失败产物。

## 发布附件

每个二进制版本提供：

- Windows 应用 ZIP。
- MPV 及依赖的对应源码归档。
- `SHA256SUMS.txt` 下载校验文件。

应用包包含项目 LICENSE、`third-party/mpv/` 和 `third-party/dotnet/` 下的许可材料。上传后核对附件大小和 SHA256。发行版本见 [GitHub Releases](https://github.com/blackriki/jeby-player/releases)。

## 本地部署与回退

```powershell
.\scripts\deploy-daily.ps1 -InstallRoot .\.tmp\daily-install
```

部署脚本验证新输出后轮换 `current` 和 `previous`，保留上一版用于回退。运行中的应用会阻止替换。未指定目录时，默认安装到 `%LOCALAPPDATA%\Programs\EmbyPlayer`。
