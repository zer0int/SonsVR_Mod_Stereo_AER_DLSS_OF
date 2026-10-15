# Third-party notices and publication review

## Original mod and license discrepancy — review before publishing

The original source archive includes an **Apache License 2.0** file at its repository root; this exact file is preserved as [`LICENSE`](LICENSE). The live upstream GitHub repository also currently has an Apache-2.0 `LICENSE`. However, an older nested community-fork `README.md` claims that the program is distributed under **GNU GPL version 2**. Those two statements conflict.

**This repository cleanup cannot determine the legal status of every community contribution. Before publishing, confirm the applicable licensing with the relevant authors/rights holders. Do not treat the bundled `LICENSE` file alone as proof that all fork contributions are Apache-2.0.** This is a provenance note, not legal advice.

Original SonsVR mod: https://github.com/iPowerTech/SonsVR_Mod — Antonio Mauriello.

The Fork this mod is directly based on: https://github.com/Manny79/SonsVR_Mod.

## NVIDIA Optical Flow SDK code

Files in `native_nvof/Common/NvOFBase/` and `native_nvof/NvOFInterface/` originate from NVIDIA Optical Flow SDK 5.0.7 and contain NVIDIA copyright and permission notices in their headers. Those notices are kept intact. See `native_nvof/NVIDIA_SOURCE_NOTICE.txt`. The project-specific `native_nvof/SonsVR_NvOF_V90.cpp` is not an NVIDIA sample.

The native bridge loads `nvofapi64.dll` from the NVIDIA display driver at runtime; this repository does not bundle NVIDIA's runtime DLL.

## SteamVR/OpenVR and MelonLoader libraries

`mod/MelonLoaderUserLibs/` contains the original mod's supplied `SonsSteamVR_IL2CPP.dll` and `openvr_api.dll` as build/runtime dependencies. They are not the game's IL2CPP interop assemblies. Verify third-party redistribution conditions if publishing the binaries; alternatively require developers to obtain them from their licensed sources. Generated game interop assemblies and game assets are not distributed.

Valve/SteamVR, MelonLoader, Il2CppInterop, NVIDIA and Unity are third-party components, not authors of this fork. All product names/trademarks belong to their owners.
