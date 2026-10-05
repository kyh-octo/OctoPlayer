# Third-party notices

Scope: the self-contained Windows x64 publish inspected for this notice. NuGet metadata and package contents were checked locally. This notice does not assign one license to every VLC plugin or dependency.

## VideoLAN .NET bindings

- `LibVLCSharp` 3.10.0 - NuGet license expression `LGPL-2.1-or-later`; package: https://www.nuget.org/packages/LibVLCSharp/3.10.0. Its nuspec pins source repository https://code.videolan.org/videolan/LibVLCSharp at commit `59d70e96026229e7c232ce5074ecefbf6f8959b6`. The upstream license file at that exact commit is [LibVLCSharp-LICENSE](licenses/LibVLCSharp-LICENSE).
- `LibVLCSharp.WPF` 3.10.0 - NuGet license expression `LGPL-2.1-or-later`; package: https://www.nuget.org/packages/LibVLCSharp.WPF/3.10.0. Its nuspec pins the same source repository and commit; see [LibVLCSharp-LICENSE](licenses/LibVLCSharp-LICENSE).

## VideoLAN native runtime

- `VideoLAN.LibVLC.Windows` 3.0.23.1 - NuGet license expression `LGPL-2.1-or-later`; package: https://www.nuget.org/packages/VideoLAN.LibVLC.Windows/3.0.23.1. Package project: https://code.videolan.org/videolan/libvlc-nuget. The LGPL text in the matching VLC source archive is [VLC-COPYING.LIB](licenses/VLC-COPYING.LIB): https://github.com/videolan/vlc/blob/79128878ddb2c280bbb6c89c76a46b31a80ade1c/COPYING.LIB. The corresponding upstream GPL text is also included as [VLC-COPYING](licenses/VLC-COPYING): https://github.com/videolan/vlc/blob/79128878ddb2c280bbb6c89c76a46b31a80ade1c/COPYING. Its inclusion is a reference to upstream VLC licensing, not a claim that this exact package contains a GPL component.
- The package and publish contain `libvlc.dll`, `libvlccore.dll`, and a modular plugin tree. The x64 package contains `plugins/codec/libavcodec_plugin.dll` and `plugins/video_chroma/libswscale_plugin.dll`. The package contains no per-plugin license inventory or third-party notice files. Upstream build references: VideoLAN's package project https://code.videolan.org/videolan/libvlc-nuget; VLC 3.0 FFmpeg contrib recipe https://github.com/videolan/vlc-3.0/blob/master/contrib/src/ffmpeg/rules.mak; and VLC configure options https://github.com/videolan/vlc-3.0/blob/master/configure.ac. These are project-level sources, not a build manifest tied to NuGet package 3.0.23.1. The exact linked FFmpeg/libav revision, configure flags, and licenses of each bundled plugin remain unverified. No blanket license is asserted for the mixed plugin bundle.

## Microsoft .NET self-contained runtime

The inspected `OctoPlayer.runtimeconfig.json` includes `Microsoft.NETCore.App` and `Microsoft.WindowsDesktop.App` 10.0.11.

- `Microsoft.NETCore.App.Runtime.win-x64` 10.0.11 - nuspec license expression `MIT`; https://www.nuget.org/packages/Microsoft.NETCore.App.Runtime.win-x64/10.0.11. Exact package files: [DOTNET-LICENSE.TXT](licenses/DOTNET-LICENSE.TXT) and [DOTNET-THIRD-PARTY-NOTICES.TXT](licenses/DOTNET-THIRD-PARTY-NOTICES.TXT).
- `Microsoft.WindowsDesktop.App.Runtime.win-x64` 10.0.11 - nuspec license expression `MIT`; https://www.nuget.org/packages/Microsoft.WindowsDesktop.App.Runtime.win-x64/10.0.11. Its package `LICENSE` is [WINDOWS-DESKTOP-RUNTIME-LICENSE](licenses/WINDOWS-DESKTOP-RUNTIME-LICENSE).
- Microsoft identifies file-specific Windows terms in https://github.com/dotnet/core/blob/main/license-information-windows.md. The referenced [.NET Library License](https://dotnet.microsoft.com/dotnet_library_license.htm) and [Windows SDK License](https://learn.microsoft.com/legal/windows-sdk/license) texts are included as [DOTNET-LIBRARY-LICENSE.html](licenses/DOTNET-LIBRARY-LICENSE.html) and [WINDOWS-SDK-LICENSE.html](licenses/WINDOWS-SDK-LICENSE.html). The inspected publish includes `coreclr.dll`, `Microsoft.DiaSymReader.Native.amd64.dll`, WPF's `PresentationNative_cor3.dll`, `vcruntime140_cor3.dll`, `wpfgfx_cor3.dll`, and `D3DCompiler_47_cor3.dll`; consult Microsoft's file-specific terms.

## Remaining verification

The native package's SPDX metadata alone does not establish the license of every file in its plugin bundle. Confirm the package-specific native build manifest and dependency/license inventory, especially the exact `libavcodec_plugin.dll` linkage and whether GPL-enabled components were built in, before making whole-bundle license claims.

## Exact upstream source access and library replacement

OctoPlayer's own source remains MIT: https://github.com/kyh-octo/OctoPlayer/tree/v1.3.1 and the original installed `LICENSE`. Its source license does not replace any third-party license.

The untouched native DLLs match `VideoLAN.LibVLC.Windows` 3.0.23.1 package hashes. Querying the actual libVLC runtime reports **3.0.23 Vetinari**, changeset **3.0.23-2-0-g79128878dd**, compiler gcc 6.4.0. The official VideoLAN GitHub mirror resolves that changeset to **79128878ddb2c280bbb6c89c76a46b31a80ade1c**. The matching upstream core/library/plugin source and its build/contrib recipes are provided as an unchanged source archive:

- [VLC exact changeset source](https://github.com/kyh-octo/OctoPlayer/releases/download/v1.3.1/vlc-79128878ddb2c280bbb6c89c76a46b31a80ade1c.tar.gz)
- Original source URL: https://codeload.github.com/videolan/vlc/tar.gz/79128878ddb2c280bbb6c89c76a46b31a80ade1c
- [LibVLCSharp/WPF exact NuGet repository commit source](https://github.com/kyh-octo/OctoPlayer/releases/download/v1.3.1/libvlcsharp-59d70e96026229e7c232ce5074ecefbf6f8959b6.zip)
- Original mirror source URL: https://codeload.github.com/videolan/libvlcsharp/legacy.zip/59d70e96026229e7c232ce5074ecefbf6f8959b6

The source archives are separate downloadable release assets, not required for normal playback. Archive hashes are in the release `SHA256SUMS.txt`; no new postal or three-year written-source-offer promise is made.

LibVLCSharp assemblies and native libVLC are separately loaded files, not merged into OctoPlayer.exe. A user can rebuild the corresponding sources and replace compatible `LibVLCSharp.dll`, `LibVLCSharp.WPF.dll`, and the x64 `libvlc/win-x64` library/plugin files in a copied installation. Keep the libVLC/libvlccore/plugin set ABI-compatible and preserve each component's notices. Close the app before replacement; use a writable copied directory or appropriate permissions. The published OctoPlayer source/project also permits rebuilding/relinking against the modified libraries. Reverse engineering for debugging modifications to LGPL components is permitted as required by their applicable LGPL terms. Changed or replaced files no longer carry the original publisher signature; no promise is made that every incompatible replacement will function.

The VLC archive includes its `contrib/src` dependency recipes, patches and checksums. This identifies the core/library/plugin source changeset and makes upstream build inputs inspectable. It is not a binary-reproducibility certification or a package-specific manifest of every statically linked third-party dependency. Exact per-plugin linked dependency versions/build flags have not been independently reconstructed from the vendor DLLs. In particular, the native codec plugin embeds/uses libavcodec; there is no separate `ffmpeg.exe` command-line program bundled by OctoPlayer. Do not infer one blanket license for the entire native plugin tree from the NuGet-level LGPL expression.
