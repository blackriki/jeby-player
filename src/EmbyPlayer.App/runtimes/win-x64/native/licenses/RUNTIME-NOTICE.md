# MPV runtime distribution notes

This runtime was assembled from unmodified MSYS2 MINGW64 binary packages. DLLs were
selected by recursively reading their Windows PE import tables. The bundled
`libvapoursynth.dll` is also retained as a companion of its script API library.
All 132 DLLs retain their original bytes; `runtime-files.json` records their hashes,
package owners, sizes and imported libraries.

The combined MPV/FFmpeg runtime is distributed under **GPL-3.0-or-later**. Individual
components retain their respective licenses, listed in `packages.lock.json` and
preserved in the accompanying `licenses` directory. This statement does not relicense
third-party components. In particular:

- MPV: GPL-2.0-or-later, MSYS2 package 0.41.0-7.
- FFmpeg: GPL-3.0-or-later, MSYS2 package 9.0.1-3. Its matching PKGBUILD enables GPL
  and version 3; it does not enable nonfree components.
- OpenSSL: Apache-2.0, MSYS2 package 3.6.4-1.
- x264: its binary package metadata says `custom`; the bundled exact source's
  `x264.h` explicitly permits GPL version 2 or any later version. This distribution
  uses that GPL permission, not a commercial license. COPYING, AUTHORS and x264.h
  are included with its notices.

Corresponding sources are supplied in the release attachment
`JebyPlayer-1.0.0-beta.1-mpv-corresponding-sources.zip`. It includes complete MSYS2
source archives (upstream tarballs or Git object repositories), patches, PKGBUILD
recipes and original binary build metadata. All 143 source packages were checked
against their `.SRCINFO` base package name and version; every pair matched.

The MPV PKGBUILD matches this exact upstream build-recipe commit byte for byte:
https://github.com/msys2/MINGW-packages/blob/052099e63e69816e35b05f28c852a5209c4dd1e0/mingw-w64-mpv/PKGBUILD

MPV binary package:
https://repo.msys2.org/mingw/mingw64/mingw-w64-x86_64-mpv-0.41.0-7-any.pkg.tar.zst

Binary package SHA-256:
`4b958c3a705bbf196b3efd6f2c5809c9f5c59e4ff0fa4f4dc03deaccbe16eba2`

libmpv-2.dll SHA-256:
`808744f489a235d390020b4b1baba956b2427a6ca20c4e3846a1c33851a9da25`

Source attachment SHA-256:
`00b5fd1972d5776abeb23b174bca9960e3b6f637261c8270aa39ffa3364ff9c7`

Jeby Player does not provide or claim support for optional VapourSynth/Python scripts.
Their DLL load dependencies are included, but Python standard library modules and
user scripts are not supplied or accepted as a tested product feature. Windows system
libraries and graphics drivers are supplied by Windows and the hardware vendor.
