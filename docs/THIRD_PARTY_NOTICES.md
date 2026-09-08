# Jeby Player 第三方组件与素材清单（发布准备稿）

核对日期：2026-09-08。本文件记录当前仓库和本地包缓存中可以复核的证据，尚不是公开二进制包的完整许可证附件。项目自有代码采用 [GPL-3.0-or-later](../LICENSE)；该选择不改变第三方组件、素材或操作系统字体的许可。

## 应用运行时

| 组件 | 已核实的证据 | 发布前仍需完成 |
| --- | --- | --- |
| .NET / WPF | `src` 下五个项目使用 .NET 8，UI 和 App 使用 WPF；没有外部 `PackageReference`，应用锁文件仅包含项目引用。现有 framework-dependent 构建的 `.deps.json` 也仅列出五个自有程序集。 | framework-dependent 包由用户安装 Windows Desktop Runtime；self-contained 包必须按实际纳入的运行时版本整理对应 LICENSE、第三方通知及分发材料。不能用当前开发机版本代替最终发布包版本。 |
| libmpv-2.dll | 仓库 MPV manifest 记录版本、大小、哈希和本地压缩包线索，见下节。 | 实际构建来源、构建参数、依赖组成、适用许可、对应源码及随包材料均需补证据。 |

测试依赖没有通过项目引用进入应用包。此结论来自项目和现有 framework-dependent 输出，不代表已审计未来 self-contained 包或 MPV 内部依赖。

### MPV 二进制

证据文件：[mpv-runtime.json](../src/EmbyPlayer.App/runtimes/win-x64/native/mpv-runtime.json)。

- 文件：`libmpv-2.dll`，117,549,568 字节，AMD64。
- 版本标签：`v0.41.0-724-g71ebd0840`。
- SHA-256：`02FA97CBDB32A651ADDBB0EAFCDC8446E3B4CB7A09DA83518DAC4FBF8D62FD81`。
- 本地包线索：`mpv-dev-x86_64-20260607-git-71ebd08.7z`；构建工具线索为 `mpv-winbuild-cmake`。
- `sourceUrl`、`buildRecipeRevision`、`license.expression` 尚为空，`license.files` 尚无内容。
- 当前状态为 `internal-only/unverified`，公开发布门禁必须保持阻塞。

2026-09-08 后续核验已找到与本地压缩包哈希完全一致的上游附件，并确认包内 DLL 与当前应用 DLL 相同；构建提交和 MPV 源码版本已定位，源码许可原文已保存。详见 [来源核验记录](third-party/mpv/README.md)。manifest 中的空字段仍保留为未完成整体分发审核的状态，不能据此忽略新找到的来源证据。实际依赖版本和完整随包材料尚未闭合，上游日志附件已过期。

仅凭 MPV 项目的名称、版本标签或某份上游许可证，不能确定这个 DLL 的全部依赖和分发条件。FFmpeg 等可能内置组件必须以实际构建清单核对，不能凭常见配置推定。

## 开发和测试 NuGet 包

证据来自四个测试项目的 `packages.lock.json` 和对应本地 NuGet 包缓存。下表路径以 NuGet 全局包缓存为起点，形式为 `<小写包名>/<版本>/`；不记录开发者个人目录。所有包均属于测试工具链，未在应用项目中声明。

| 包 | 锁定版本 | 本地许可证据 |
| --- | --- | --- |
| Microsoft.NET.Test.Sdk | 17.10.0 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| MSTest.TestAdapter | 3.4.3 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| MSTest.TestFramework | 3.4.3 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| Microsoft.ApplicationInsights | 2.22.0 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| Microsoft.CodeCoverage | 17.10.0 | `.nuspec` 声明 `MIT`；另有根目录及 `build/netstandard2.0/ThirdPartyNotices.txt`。 |
| Microsoft.TestPlatform.ObjectModel | 17.10.0 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| Microsoft.TestPlatform.TestHost | 17.10.0 | `.nuspec` 声明 `MIT`；另有 `ThirdPartyNotices.txt`。 |
| Microsoft.Testing.Platform | 1.2.1 | `.nuspec` 声明 `MIT`；缓存内未发现独立许可证正文。 |
| Microsoft.Testing.Extensions.Telemetry | 1.2.1 | `.nuspec` 指定 `License.txt`；正文标题为 `MICROSOFT SOFTWARE LICENSE TERMS / MICROSOFT .NET LIBRARY`，不标为 MIT。 |
| Microsoft.Testing.Extensions.TrxReport.Abstractions | 1.2.1 | 同包内 `License.txt`，Microsoft .NET Library 条款。 |
| Microsoft.Testing.Extensions.VSTestBridge | 1.2.1 | 同包内 `License.txt`，Microsoft .NET Library 条款。 |
| Microsoft.Testing.Platform.MSBuild | 1.2.1 | 同包内 `License.txt`，Microsoft .NET Library 条款。 |
| Newtonsoft.Json | 13.0.1 | `.nuspec` 声明 `MIT`，另有 `LICENSE.md`。 |
| System.Diagnostics.DiagnosticSource | 5.0.0 | `.nuspec` 声明 `MIT`，`LICENSE.TXT` 含 MIT 正文及 .NET Foundation and Contributors 署名。 |
| System.Reflection.Metadata | 1.6.0 | `.nuspec` 无现代 `license` 节点；`LICENSE.TXT` 明确含 MIT 正文及 .NET Foundation and Contributors 署名。 |

这里共列出 15 个包，包括三个直接测试依赖及其传递依赖。表中的元数据声明与许可证正文证据已区分；尚未逐一整理所有测试包内嵌第三方内容的完整通知。

发布源代码仓库时保留锁文件，通过 NuGet 还原测试工具；不要把包缓存或测试输出作为应用附件上传。若未来分发测试工具二进制，应依据具体包的原文单独完成随包通知，不能统一套用项目 GPL 许可。

测试链中存在名为 Telemetry 和 ApplicationInsights 的传递依赖，不等于 Jeby Player 应用引入了遥测功能；本清单不对测试工具实际运行时的数据行为作未经验证的承诺。

## 字体、图标和图片

| 范围 | 已核实的证据 | 未完成事项 |
| --- | --- | --- |
| 字体 | `src` 未包含 `.ttf`、`.otf` 或 `.woff` 字体文件；当前应用 XAML 未显式指定 `FontFamily`，使用 WPF / Windows 字体解析。 | 若将来随包提供字体，需单独加入字体来源与许可。不能将 Windows 字体当作项目自有素材再分发。 |
| 应用图标 | `src/EmbyPlayer.App/Assets/app-icon.ico` 与 `app-icon.png` 在仓库中；窗口与程序使用 ICO，关于页以资源链接复用高清 PNG。 | 尚无可核验的原始设计来源或授权记录。界面复用正式图标不等于完成来源核验。 |
| 首页媒体库图片 | `src/EmbyPlayer.UI/Assets/HomeIcons/` 包含 Animation、BoxSet、Movie、Playlist、Series 五张 PNG，作为 WPF Resource 嵌入。 | 尚未找到素材作者、原始来源及许可记录；公开前需补证据或替换为来源明确的素材。 |
| XAML 动作图标 | `src/EmbyPlayer.UI/Resources/Icons.xaml` 使用本地矢量定义；已找到当前收藏、时钟、设置和播放等实心图标的提案/绘制脚本，见 [素材记录](ASSET_PROVENANCE.md)。 | 其他历史路径仍需逐项确认；不能将当前部分设计记录推及全部图标。 |
| 服务器媒体图片 | 海报、背景等由用户配置的媒体服务器提供，不属于本仓库上述静态素材清单。 | 公开 README、宣传图或测试附件应使用有权公开的示例图片，避免直接附带私人服务器内容、地址或账户信息。 |

## 转为正式发布附件的剩余工作

1. 补齐或替换 MPV 运行时，核实实际构建和所有随附依赖，准备相应许可证、通知与对应源码材料后再运行 Public 门禁。
2. 对最终选用的 .NET 发布方式及运行时版本提取实际随包许可证、第三方通知；逐项核对输出内容，不只审查项目依赖声明。
3. 确认应用 Logo、五张首页 PNG 和 XAML 图标的设计来源；来源无法确认的素材需在公开前替换。
4. 将需要随二进制分发的许可证和通知原文纳入发布产物，并验证文件确实随包存在。本草稿的描述不能替代这些原文。
5. 正式发布时更新本清单的版本与证据，保留第三方原有署名。项目公开版权署名另行确认，不从开发机账户或 Git 邮箱推定。

相关状态见 [公开发布准备](PUBLIC_RELEASE.md) 和 [品牌规范](brand-guidelines.md)。
