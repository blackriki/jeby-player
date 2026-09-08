# MPV 来源核验记录

核验日期：2026-09-08。此目录是发布准备证据，不是当前 DLL 的完整第三方分发附件。运行时 manifest 暂时仍保持 `internal-only/unverified`；不能仅凭本目录解除 Public 检查。

## 已建立的对应关系

1. [上游 20260607 发布](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20260607) 的附件 `mpv-dev-x86_64-20260607-git-71ebd08.7z` 与本地原始压缩包一致。GitHub [发布 API](https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/releases/tags/20260607) 返回大小 **30,289,689** 字节，digest 为 `sha256:faa0be46643cd889a1d816696f60b9962d7bb70e9d9d6e619da368d0b22211d6`；本地重新计算结果一致。
2. 用 Windows `tar` 解出该包的 `libmpv-2.dll`，与当前应用 DLL 的 SHA-256 都是 `02fa97cbdb32a651addbb0eafcdc8446e3b4cb7a09da83518dac4fbf8d62fd81`。解出的 DLL 未执行、未替换应用文件。
3. [构建工作流 27077466212](https://github.com/shinchiro/mpv-winbuild-cmake/actions/runs/27077466212) 的 API 返回 `completed/success`，`head_sha` 为 `5efd298cb51513c2410e4e9029b5e56b83c2aaac`，与发布标签解析出的提交一致。
4. DLL 的版本标签指向 MPV 源码提交 [71ebd08406547339a5decd9c61ab3e83739e96b3](https://github.com/mpv-player/mpv/commit/71ebd08406547339a5decd9c61ab3e83739e96b3)。同目录版权与许可文件来自该固定提交，未修改原文。

确切附件地址：
[mpv-dev-x86_64-20260607-git-71ebd08.7z](https://github.com/shinchiro/mpv-winbuild-cmake/releases/download/20260607/mpv-dev-x86_64-20260607-git-71ebd08.7z)。这记录来源，不代表本项目已经提供获完整核验的下载包。

## 已归档的源码许可材料

| 文件 | 原始地址 | SHA-256 |
| --- | --- | --- |
| [Copyright](Copyright) | [固定提交原文](https://raw.githubusercontent.com/mpv-player/mpv/71ebd08406547339a5decd9c61ab3e83739e96b3/Copyright) | `bfe9ee4cceabcb8ecbfadf208d04156f73d801e6a57369a5606bb8341e204a23` |
| [LICENSE.GPL](LICENSE.GPL) | [固定提交原文](https://raw.githubusercontent.com/mpv-player/mpv/71ebd08406547339a5decd9c61ab3e83739e96b3/LICENSE.GPL) | `edaef632cbb643e4e7a221717a6c441a4c1a7c918e6e4d56debc3d8739b233f6` |
| [LICENSE.LGPL](LICENSE.LGPL) | [固定提交原文](https://raw.githubusercontent.com/mpv-player/mpv/71ebd08406547339a5decd9c61ab3e83739e96b3/LICENSE.LGPL) | `72b672113d642cbb8ef5dcc76938db801983c56e50b1400ab930f1a64d6dc8d9` |

保留两种许可原文是为了完整记录源码说明，不能据此称当前 DLL 可以任意选择 LGPL。包内 `client.h` 的 ISC 许可仅适用于该 API 头文件，不代表整个播放内核的许可。

## 尚未闭合的证据

- 本地开发包只包含四个 API 头文件、导入库和 DLL，没有完整依赖清单或许可证附件。
- [固定提交的 mpv.cmake](https://github.com/shinchiro/mpv-winbuild-cmake/blob/5efd298cb51513c2410e4e9029b5e56b83c2aaac/packages/mpv.cmake) 启用了静态依赖偏好和多个外部组件；[ffmpeg.cmake](https://github.com/shinchiro/mpv-winbuild-cmake/blob/5efd298cb51513c2410e4e9029b5e56b83c2aaac/packages/ffmpeg.cmake) 包含 `--enable-gpl` 和 `--enable-version3`。这些构建参数是核验线索，不能替代每个实际依赖版本的许可和对应源码。
- [该次构建的 artifacts API](https://api.github.com/repos/shinchiro/mpv-winbuild-cmake/actions/runs/27077466212/artifacts) 显示包括 `mpv-x86_64-logs` 在内的全部 12 个附件均 `expired=true`。发布附件仍存在，但不能把已过期日志当作可获取的依赖锁定记录。
- 尚需找回实际依赖版本与完整通知、准备对应源码材料；若记录无法恢复，应准备一次固定依赖版本并保存完整证据的构建，再单独进行播放器回归验收。

本次未更换 MPV、未修改 `license.expression`、未将来源状态改为 verified。下次完善 runtime manifest 时应引用本记录中已核实的 URL 与构建提交，并与剩余分发材料一起审查。
