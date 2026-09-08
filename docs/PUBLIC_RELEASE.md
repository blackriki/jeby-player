# Jeby Player 发布与维护

发布目标：[blackriki/jeby-player](https://github.com/blackriki/jeby-player)。当前版本仅支持 Emby；Jellyfin 属于后续计划。最终公开管线和远端 Release 仍待完成，以下准备结果不等于已经发布。

## 当前准备状态

- 产品名称为 Jeby Player，自有代码采用 [GPL-3.0-or-later](../LICENSE)。工程、可执行文件及设置目录保留 EmbyPlayer 标识以兼容旧版本。
- 播放运行时已更换为核验过的 MSYS2 MPV 0.41.0-7；[manifest](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json) 为 `verified`，记录主 DLL、132 个 DLL 的运行时集合及 493 项许可文件记录。
- MPV 对应源码 ZIP 已整理，约 1.61 GB，需与二进制 Release 一起提供。文件名和 SHA256 见 manifest；详见 [第三方清单](THIRD_PARTY_NOTICES.md)。
- SelfContained 发布脚本已实现按实际 runtimeconfig 版本提取 .NET 与 WPF 许可材料、复制到 `third-party/dotnet/` 并核对哈希；最终产物仍需执行完整管线验证。
- 已核验新运行时的本地合成视频与带认证头的本地 HTTP 播放、暂停、seek、音轨、字幕导入及预览。不把这些结果等同于真实服务器 HTTPS、转码或 GPU 视觉验收。
- 发布介绍采用用户授权展示的真实产品截图，并遮蔽用户名；不伪造演示账号、媒体内容或素材来源。截图展示前仍应检查私人地址及其他个人信息。

## 从源码构建

需要 Windows 10/11 x64、[global.json](../global.json) 指定的 .NET SDK，以及 .NET 8 Desktop Runtime。仓库不提交运行时 DLL；只构建和浏览界面不要求 MPV，播放需要完整且与 manifest 匹配的 native DLL 集合，不能只复制主 DLL。

```powershell
dotnet restore EmbyPlayer.sln --locked-mode --disable-parallel
dotnet build EmbyPlayer.sln --no-restore
.\scripts\run-app.ps1
```

完整验证使用 `.\scripts\build-test.ps1`，发布管线测试需要匹配的 MPV 运行时。GitHub CI 运行无需真实服务器、交互桌面及 native DLL 的测试子集；最终结果以对应提交的 CI 与 Release 说明为准。

## 公开打包

命令在仓库根目录执行。准备完整 native 运行时与许可证文件，确保工作树干净，再执行：

```powershell
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public -PreflightOnly
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public
```

MPV 来源材料已补齐，原开发版 shinchiro 二进制的证据缺口已不再是当前运行时状态；旧核验记录仅作为被替换版本的历史调查。不得通过 Internal、脏工作树或跳过检查替代 Public 发布。预检成功只代表前置条件满足，不代表完整测试、打包和启动验收已完成。

[发布脚本](../scripts/publish-release.ps1) 检查 Git 状态、敏感文本、locked restore、Release 构建和测试，核对 native 文件与许可证哈希，再发布 payload、运行窗口启动检查、生成逐文件 SHA256 manifest 与 ZIP。输出位于 `.tmp/release-staging/`，Public 包名为 `JebyPlayer-<version>-win-x64-<deployment>`。

`SelfContained` 自带 .NET 运行时；`FrameworkDependent` 需要用户安装 x64 .NET 8 Desktop Runtime。两者都必须包含经过核验的完整 native DLL 集合、项目 LICENSE 和适用第三方通知。发布包不得包含 PDB；存在 `RELEASE-FAILED.txt` 的产物不可分发。

## 发布前最后检查

1. 对最终源码快照运行文本检查、构建与相关测试，并核对 GitHub CI。
2. 完成 Public 管线，检查实际 ZIP、版本、逐文件哈希、MPV 与 .NET 通知文件。
3. 同时提供 manifest 指定的 MPV 对应源码 ZIP 和校验值。
4. 按 [测试计划](TEST_PLAN.md) 核对服务器登录、浏览、播放、字幕及进度同步；在 Release 说明中明确实际覆盖和未覆盖范围。
5. 检查产品截图、安装说明、下载链接和已知限制，再发布 Release。

## 本机验收与回退

```powershell
.\scripts\deploy-daily.ps1 -InstallRoot .\.tmp\daily-install
```

默认安装根为 `%LOCALAPPDATA%\Programs\EmbyPlayer`，可通过参数选择验收目录。部署脚本验证新输出后轮换 `current` 与 `previous`，保留上一版用于回退；运行中的应用会阻止部署，脚本不替用户强制退出。异常事务残留应先检查，不能盲目覆盖。
