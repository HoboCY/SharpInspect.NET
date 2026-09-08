# SharpInspect.NET.Cameras.Hikrobot

托管海康相机适配器，目标为 .NET 6。厂商依赖为 **Windows x64 Hikrobot MVS Runtime**，由设备集成方另行安装 Runtime、接口驱动与服务；本包不包含或安装厂商二进制、头文件、CTI 或驱动。

工程探针的唯一 native 候选文件版本为 `4.8.1.2`。**当前生产兼容清单为空，没有任何已资格化 Runtime/型号/固件组合。** 公开 `HikrobotCameraProvider` 只提供安全的本机依赖诊断；Discover/Open 拒绝，Ready 保持 false，也没有生产 override。

常见 Runtime 目录为 `%CommonProgramFiles%\MVS\Runtime\Win64_x64`。诊断要求 `MvCameraControl.dll`、`MVGigEVisionSDK.dll`、`MvUsb3vTL.dll` 为同目录 native AMD64 文件。其余传递依赖与所用接口的驱动/服务由厂商安装包提供；文件存在不证明设备或驱动工作。

单独的工程设备探针保留在源仓库，需要显式设备授权及 Runtime 哈希、设备身份、型号、完整配置和新建证据目录。托管测试、静态 PE/头文件检查及工程采集观测都不授予 Hardware Qualification。

当前工程候选每个已打开物理句柄只注册一次回调、安装一次采集请求。单帧终态后拒绝再次 Start/Acquire；即使尚未采集，注册回调后的 Stop 也退役整个设备。再次采集须先完成旧设备 Dispose/Close/Destroy，再重新 Open。同一句柄的重复采集尚不支持，SDK 缓冲与注销后回调的真实行为仍待设备资格化。

原生工程候选仅支持 SoftwareTrigger；HardwareTrigger 在探针加载 SDK 前以 `HikrobotHardwareTriggerNotQualified` 拒绝。托管硬触发时序测试不能证明设备在 Busy 前产生的脉冲未被 SDK 缓冲。

[安装、探针入口与实际验证范围](https://github.com/HoboCY/SharpInspect.NET/blob/main/docs/verification/v1-21.md)
