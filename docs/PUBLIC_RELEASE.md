# Jeby Player 发布准备与维护

当前正在准备首个 Emby 公开测试版，尚未公开发布。Jellyfin 支持属于后续计划。本文命令均从仓库根目录运行。

## 首个公开测试版的完成条件

以下是发布准备清单，不代表检查已经全部通过。应用改名和内部验收通过不能替代公开分发审核。

| 项目 | 当前状态 / 尚需完成 |
|---|---|
| 产品名称与版本 | 已确定 Jeby Player；本批统一窗口、关于信息和产品属性，保留工程及存储标识兼容旧版本。最终产物仍需检查显示版本与包版本一致。 |
| 项目许可证 | 已选择 `GPL-3.0-or-later`，自有项目代码按 GNU GPL 第 3 版或后续版本授权，已加入 [LICENSE](../LICENSE)；版权署名待维护者确认。 |
| MPV 来源与许可 | 已核对上游附件哈希、包内 DLL、构建提交并归档源码许可；实际依赖版本和完整分发材料尚缺，上游日志已过期，见 [核验记录](third-party/mpv/README.md)。manifest 暂保留未核验状态，Public 门禁继续保留。 |
| 其他第三方组件 | 已整理 [组件与素材清单初稿](THIRD_PARTY_NOTICES.md)，区分应用与测试依赖；最终运行时通知、图标图片来源及随包材料待补。 |
| 仓库公开前检查 | 使用独立源码快照，排除私人配置、开发临时目录和旧 Git 历史；最终快照仍需执行文本检查。 |
| 反馈入口 | 已编写 GitHub 问题反馈与功能建议表单；发布目标为 blackriki/jeby-player，仓库创建后检查远端展示。 |
| 发布说明 | 已编写 [更新记录](../CHANGELOG.md)；首版实际发布说明、安装/升级/回退步骤及有权展示的截图仍需随最终产物完成。 |
| 持续集成 | 已编写 GitHub Actions 工作流，远端 CI 尚未运行；不能据此宣称 CI 检查通过。 |
| 构建与验收 | 待对最终待发布提交执行干净工作树 Public 管线、完整测试、启动检查和真实服务器核心流程验收。 |
| 分发包 | 待生成通过检查的 win-x64 ZIP、文件哈希和第三方材料；在干净 Windows 环境检查启动与更新。 |
| 公开仓库与 Release | 发布目标为 [blackriki/jeby-player](https://github.com/blackriki/jeby-player)，尚待创建与验证；完成准备不等于已经上传或发布。 |

项目只连接用户自己的服务器，不提供内容或账号，与 Emby / Jellyfin 没有官方隶属关系。Jellyfin 支持和自动更新不能写成现有功能。

## 发布验证

最终源码快照的检查、构建和测试结果以对应提交的 CI 与发布说明为准。当前快照仍待最终验证，旧开发版本的验收结果不作为本次发布结果。

文本检查使用 `scripts/check-sensitive-files.ps1`，覆盖常见凭据和私人地址模式；它不替代图片检查或第三方材料核对。公开截图应去掉私人账号、服务器地址及个人路径。

## MPV 公开分发限制

项目自有代码采用 `GPL-3.0-or-later`，不改变 MPV 和其他第三方组件原有的许可，也不能替代对实际二进制构建来源、适用许可及对应分发材料的核验。

证据文件为 [mpv-runtime.json](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json)。它记录当前 DLL 的大小、SHA256、版本、AMD64 架构、本地归档哈希和构建线索，但缺少可证明的下载 URL、完整构建配方 revision 及许可证文件。

后续已在 [来源核验记录](third-party/mpv/README.md) 补齐下载附件与构建提交的对应证据、源码许可原文；尚未写入 runtime manifest 或宣称整体分发就绪，原因是内置依赖及对应材料仍待补齐。

- `Internal` 可以生成包，但 manifest 标记 `distributionReady=false`，不可当作公开下载包。
- `Public` 在证据不完整时硬失败。补齐并审核来源、构建配方、许可证表达式与文件后才能解除阻塞。
- 不要通过改用 Internal、跳过测试或手工修改就绪标志绕过公开发布检查。

## 开发与完整验证

开发环境为 Windows 10/11 x64。[global.json](../global.json) 指定 .NET SDK `10.0.303`，允许同一 feature band 的更新补丁；应用目标框架为 .NET 8 WPF，运行应用及测试还需安装 x64 .NET 8 Desktop Runtime（仅安装 .NET 10 SDK 不会提供这个运行时）。没有 MPV DLL 也可构建应用并使用浏览界面；实际播放需要本地的 `src/EmbyPlayer.App/runtimes/win-x64/native/libmpv-2.dll`。发布管线仍严格检查 DLL 是否存在及其证据是否完整。

```powershell
dotnet restore EmbyPlayer.sln --locked-mode --disable-parallel
dotnet build EmbyPlayer.sln --no-restore
.\scripts\run-app.ps1
```

完整验证使用 `.\scripts\build-test.ps1`。发布管线测试会检查真实 MPV DLL，因此完整测试需先准备上述运行时；GitHub CI 仅运行无需该 DLL 和交互桌面的测试子集。

依赖使用已提交的 lock 文件；参与发布的 source 项目另有 `packages.win-x64.lock.json`。日常与 RID 发布分别使用对应的 locked restore 图。

## 内部验收部署

```powershell
.\scripts\deploy-daily.ps1
```

默认部署到当前用户的 `%LOCALAPPDATA%\Programs\EmbyPlayer`。目录名称为兼容旧版本保留。可显式指定独立的验收目录：

```powershell
.\scripts\deploy-daily.ps1 -InstallRoot .\.tmp\daily-install
```

脚本持有安装根的跨进程锁，在隔离目录构建并执行测试和可见窗口启动检查，再轮换 `current` 与 `previous`。正在运行的应用会使部署失败，脚本不替用户终止正常实例。旧 `previous` 会在成功轮换后清理，只保留上一版。

目录轮换不是整体原子操作；断电或强制中断可能留下事务目录。脚本会拒绝自动覆盖异常残留，需先检查，不能直接重新发布覆盖证据。详细行为以 [部署脚本](../scripts/deploy-daily.ps1) 和 [支持脚本](../scripts/daily-deploy-support.ps1) 为准。

## 内部打包

```powershell
.\scripts\publish-release.ps1 -Version 1.0.0-internal.1 -Deployment SelfContained -Channel Internal
```

版本号为示例。默认要求工作树干净。仅验证未提交的内部修改时可显式添加 `-AllowDirty`；`-SkipTests` 和 `-SkipLaunchSmoke` 仅用于有明确理由的内部检查，manifest 会记录测试跳过状态，不能据此宣称完整验证通过。

也可将 `-Deployment` 改为 `FrameworkDependent`，目标电脑需要 x64 .NET 8 Desktop Runtime；`SelfContained` 自带 .NET 运行时。两种包都仍依赖经校验的 MPV DLL。

## Public 预检与打包

证据补齐且工作树干净后，先执行预检，再运行完整打包。版本号仅为命令示例：

```powershell
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public -PreflightOnly
.\scripts\publish-release.ps1 -Version 1.0.0-beta.1 -Deployment SelfContained -Channel Public
```

当前 MPV 材料尚未补齐，Public 检查应失败。预检成功只表示前置检查通过，仍需完整打包和验收。Public 禁止脏工作树、跳过测试、跳过启动检查和只输出目录的模式。

[发布脚本](../scripts/publish-release.ps1) 会校验 Git 状态与文本候选文件、locked restore、Release 构建和四个测试项目，核对 MPV 架构与哈希，发布独立 payload 并执行可见窗口启动检查，最后生成逐文件 SHA256 manifest 和 ZIP。输出位于 `.tmp/release-staging/`，Public 目录及同名 ZIP 使用 `JebyPlayer-<version>-win-x64-<deployment>`，Internal 保留 `<version>-<deployment>-<channel>` 命名。发布不覆盖标准 `bin/Release`。

项目 [LICENSE](../LICENSE) 随应用构建和发布复制。Public 的 MPV 许可证证据需要是工作区内的普通非 reparse 文件，并随包复制到 `third-party/mpv/`。发布包禁止 PDB；如存在 `RELEASE-FAILED.txt`，该产物不可分发。

启动检查仅证明应用能打开顶层窗口。服务器登录、浏览、视频播放、字幕、音轨及进度同步仍需按 [测试计划](TEST_PLAN.md) 验收。
