# Hikrobot SDK source and runtime evidence

T21 implements a managed adapter and an isolated development device probe. **No Hikrobot Runtime is production-qualified in this release.** The closed production compatibility catalog is empty. Hardware, driver/service behavior, cross-version Windows ABI execution, reconnect and station qualification remain `NotRun`. Managed fixtures and PE/header inspection do not change that classification.

## Sources inspected on 2026-09-08

The [official Hikrobot download center](https://www.hikrobotics.com/en/machinevision/service/download/) returned the Windows SDK Runtime package via its public download metadata. The downloaded artifact is `MVS_SDK_V4_8_1_2_MVFG_V2_8_0_3_VC90_Runtime_STD.exe` (79,088,976 bytes). SHA-256:

`ED6F7ACB8FF0834CABC35F696918B260A24066F6BCAA03865C0C1559845A4DC4`

Authenticode status was `Valid`, signer `Hangzhou Hikrobot Co., Ltd.`. Only archive extraction was performed with an existing 7-Zip executable. Neither installer nor driver nor SDK executable was launched. Extraction path within the nested archives:

`Compress.7z / TEMP/MVS_SDK_Runtime_x64_Setup.exe / MVS_Runtime_x64_Setup.exe / Win64_x64/MvCameraControl.dll`

The extracted DLL is native AMD64, file version `4.8.1.2`, product version `4.8.1.2.1873781`, valid Authenticode from the same signer. SHA-256:

`98CA43BD9B4872B4D2F1C09822B0FBCEBF43AFE6BC0140B0BEB077742454C3C2`

`tools/Inspect-HikrobotRuntimeFile.ps1` binds that exact SHA-256, requires valid Authenticode and file version 4.8.1.2, then requires all 22 exports used by `HikrobotNativeApi` among 321 named exports. The bounded input file remains locked against writing/deletion throughout inspection. This proves the frozen file identity, signature, export names and architecture, **not function signatures, live ABI compatibility, dependency loadability, drivers, device behavior or production support**. Its output has `nativeSdkLoaded=false`, `deviceAccess=NotRun`, `hardwareQualification=NotRun`; a binding or export failure records `result=Fail` and returns an error.

The official complete MVS 5.0.2 and 5.0.0 Windows ZIP links returned HTTP 403. No form, login or access-control workaround was used. The Runtime-only package contains no Windows development headers. Therefore the header source below is explicitly a different, older distribution.

## Windows C ABI source boundary

Vendor-authored headers and samples were read from a [public mirror of MVS 4.0.0 (221208)](https://cncu.co.za/FlatCUT-Flatbed-Cutting-Machine/MDR%20Oscillating%20Blade%20CNC%20Router/Hikrobot%20MVS%20%28Machine%20Vision%20Software%29%20STD%204.0.0%20%28221208%29%20Installation/Development/). This is a third-party distribution of vendor source, not an official signed header delivery. No header or vendor binary is redistributed in this repository or NuGet.

| File | SHA-256 |
| --- | --- |
| CameraParams.h | 1AA4C908F1BBB817AF90EF0E8EC22BB8F5C187FB5146D2EFE5E159BD719CC455 |
| MvCameraControl.h | DE3E5EB4008D630844D4D91D88609386F0929A7CECA46854C6100169256A6B6F |
| PixelType.h | BC7667F39836A6512D2D759ED2DAC30CCFEC840C58A7F74C0EBF03C2381ADC78 |
| MvErrorDefine.h | ACB0FCC8F1B7083CF904ECE2148365B2B2B7D3875DB30BA0F78AAF633E52F253 |
| MvISPErrorDefine.h | 9B31A4AFB76BC3BF058F4D6B6847DFF55BBB10EDD5A58135DA99EAEF3FF11E0A |
| MvObsoleteInterfaces.h | 7380A121FB0554858755C1849A932D58093EA40757EA7E6717068070811763EC |
| ObsoleteCamParams.h | E530868D36A6867C9107FE1AC6743C9E28ACA30D057903957875364EBD691F5B |

The main include closure contains no `#pragma pack`. `tools/HikrobotAbiLayoutProbe.cpp` compiles static size/offset assertions with clang targeting `x86_64-pc-windows-msvc`, C++17, without linking or loading a DLL. The inspected headers require C++ parsing; a C17 parse is not claimed as passing.

Reproduce after obtaining and checking these exact headers:

```powershell
& $clang --target=x86_64-pc-windows-msvc -x c++ -std=c++17 -fsyntax-only -I $headerDirectory tools/HikrobotAbiLayoutProbe.cpp
./tools/Inspect-HikrobotRuntimeFile.ps1 -LibraryPath $absoluteDllPath -OutputPath $absoluteEvidencePath
```

The retained assertions establish the x64 device-list layout (2056 bytes; pointer array offset 8), node values (IntEx 96, Float 28, Enum 280, String 272, EnumEntry 84), and callback prefix offsets. The callback reads only documented prefix fields and immediately delegates bounded copying; SDK pointers never become public frame ownership. No packed/Bayer conversion struct, legacy trigger API or vendor-managed wrapper is bound.

`MvCameraControl.h` defines generic command dispatch. The vendor sample `Development/Samples/C#/MvCamCtrlNet/BasicDemo` sets software trigger source and calls `SetCommandValue("TriggerSoftware")` (downloaded `MvCamCtrlNet.BasicDemo.cs`, lines 538–563); its wrapper forwards to `MV_CC_SetCommandValue`.

The more recent [vendor-authored Doxygen API mirror](https://hikdocs.krins.cloud/html/_mv_camera_control_8h) provides `int __stdcall MV_CC_Initialize()` / `MV_CC_Finalize()` declarations. Its [home page](https://hikdocs.krins.cloud/html/) identifies a **Linux SDK**. Its [node table](https://hikdocs.krins.cloud/html/_xE7_x9B_xB8_xE6_x9C_xBA_xE5_x8F_x82_xE6_x95_xB0_xE8_x8A_x82_xE7_x82_xB9_xE8_xA1_xA8) corroborates node names/types, including WidthMax/HeightMax, trigger, exposure and gain nodes. These are semantic/signature references only and do not close the Windows 4.8.1.2 hardware/ABI gap.

## Development candidate versus support

The private native loader accepts only candidate file version `4.8.1.2`, validates AMD64/native PE, resolves all required exports and compares `MV_CC_GetSDKVersion` against the file version. Only the unpackaged friend executable can enter it. The public Provider has no runtime path, qualification mode, injected SDK or compatibility override. Its production Discover/Open remain denied for every version.

The first native candidate exposes only device-reported monochrome formats (Mono8 and unpacked Mono10/12/16). Native RGB/BGR/manual white-balance model support is not declared. The managed pixel normalizer separately implements and tests RGB/BGR canonical conversion. Packed/Bayer/chunk/incomplete frames are rejected rather than guessed. Float nodes lack an SDK increment, so the adapter advertises a conservative exactly representable binary grid (exposure/delay at least 1 us, gain at least 1/16 dB, enlarged as required by float precision); it does not claim this is the camera's native quantization grid.

To add a production compatibility entry, a later provider release must bind matching vendor Windows source/runtime evidence and real device/driver/model/firmware/transport results, then complete the repository's Provider Qualification and Station Acceptance requirements. A successful development probe is not such a record.

Local read-only evidence from this session is retained outside the repository under `E:\temp\SharpInspect.NET-validation-artifacts\ticket21-sdk-sources`, including `runtime-static-inspection.json`, official metadata, archive files, headers and the initial compiler probe. This machine-specific location is an engineering verification pointer, not an application diagnostic payload.
