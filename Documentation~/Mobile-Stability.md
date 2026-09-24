# Mobile 第二阶段：正确性、曝光与天空回退

本轮保留现有 HDRP 材质、URP 14.0.9 metallic BRDF、两级阴影和独立移动 backend。没有引入桌面 SkyManager、自动曝光历史、计算光源列表或动态反射。

## 行为与兼容性

- `cascadeBlend` 默认 0.1，范围 0–0.3；在第一级球半径末端平滑过渡到第二级。设为 0 关闭。阴影剔除覆盖同步扩展，PCF 不跨图集瓦片采样，无效投影返回无阴影。
- 每个 RenderGraph Pass 保存自己的相机、光照、阴影和后处理参数。自定义视图矩阵决定相机位置/朝向；相机 Transform 仍用于默认 Volume 锚点。
- HDRP 烘焙反射探针在剔除前同步纹理与影响范围，并明确开启原生 Reflection Probe 剔除；释放、停用和场景卸载时恢复纹理绑定。
- 缺失/退化切线采用有限的备用切线空间；不使用法线贴图时无需切线。Mobile 选择/拾取遵循单面/双面裁剪，桌面 Unlit 不引入双面光照常量。
- 构造中途失败、backend 替换和重复释放安全；阴影深度偏移、SRP 回调和上下文收尾具有异常清理路径。

## Desktop 曝光对应

移动路径现在在 RGBA16F 写入**之前**应用当前相机曝光，消除先截断到 65504、再曝光导致的亮度丢失。该过程不使用历史帧。

固定曝光为 `1 / (lensScale × 2^(EV100 − compensation))`。Perfect Lens 的 lensScale 为 1，Imperfect Lens 为 1.2，读取已注册 HDRP Global Settings，未注册时默认 Imperfect Lens；不会依赖此前是否运行过 Desktop 管线。

Physical Camera 模式使用同一台 Camera 的 aperture、shutterSpeed、ISO；Scene View 曝光覆盖沿用 HDRP 的编辑器设置。默认 Volume 使用已配置的 HDRP 默认 Profile，不创建完整桌面资源。自动曝光仍不支持，会出现在审计报告中。

Unlit 基础色与纯色清屏保持 Desktop 的非物理曝光语义；自发光遵守 Emissive Exposure Weight。Color Adjustments 的 Post Exposure 仍作为后处理调整。固定 EV 转换可精确比较，完整 Lit 画面还受到 URP 与 HDRP BRDF 差异影响。

## 天空近似回退

相机选择 Sky 清屏时，依次使用显式 Mobile Cubemap、Volume 天空、支持的 `RenderSettings.skybox`。关闭 `skyFallback` 则只使用显式 Cubemap。Color 清屏不被天空覆盖。

- HDRI：保留 Cubemap、旋转、Exposure/Multiplier/Lux 强度；不支持背板和扭曲。
- Gradient：保留上、中、下颜色、渐变宽度及强度。
- Physically Based Sky：根据太阳强度、空气与地面色调生成渐变和太阳圆盘。它是低成本近似，不是大气散射实现。
- URP `Skybox/Cubemap` 与 `Skybox/Procedural`：使用对应贴图/颜色、旋转、材质 Exposure 和太阳参数；不依赖 URP 包。Legacy Skybox 按显示亮度处理，不因 HDRP 物理相机 EV 而变黑。

回退只绘制背景，不更新 SH、反射探针或环境光照；继续使用烘焙数据。六面图、全景天空及自定义 Skybox Shader 不在本轮回退范围内。

## 验证入口

完整 Editor 测试包含实际线性浮点渲染、透明叠层、Mask/Normal/Emission、双面/负缩放、蒙皮、实例化、辅助 Pass 裁剪、相机 A→B→A、局部 Volume 锚点、反射探针、50 次目标尺寸/管线切换、50 次资源/场景释放，以及构造和回调异常注入。

独立阴影测试使用生产 HLSL 与受控深度图，检查过渡端点/单调性、关闭过渡、无阴影、越界和距离淡出。曝光测试直接调用 Desktop `Exposure.compute` 的固定曝光 kernel 作参考。

`MobilePlayModeValidation.Run` 在临时验证工程重复开启/关闭 Domain Reload 的播放进出；`MobileRenderValidation.Run` 检查桌面与移动关键 Shader；`ExportAndroid` 验证 Vulkan/IL2CPP 导出。所有修改 Graphics/Quality/场景的批处理入口仅适用于可丢弃工程。

Android 设备视觉、温度、GPU 捕帧和 1080p/60 FPS 仍需真机验收，不能由这些检查替代。

## 本轮验证结果

2026-09-24，Unity 2022.3.17f1，Windows D3D11，RTX 3060 Laptop GPU：

- 完整 Editor 测试 **118/118 通过，0 失败、0 跳过**，保留原有全部 85 项测试。[NUnit 报告](MobileValidation/Stability/EditorTests.xml)
- 开启 Domain Reload 三轮、关闭三轮，均实际离屏渲染；释放后材质、RenderGraph、移动 RenderTexture、VolumeComponent 数量未增长。[播放检查记录](MobileValidation/Stability/PlayMode.txt)
- 桌面/移动关键 Shader 和功能变体通过；离屏阴影检查检测到 1,040 个接收阴影变暗的像素。
- Android Vulkan / IL2CPP 工程导出、ARM64 原生编译和 Debug APK 打包成功，APK 的 v1/v2 签名验证通过。Gradle 依赖通过 FlClash 本地代理下载。[构建记录](MobileValidation/Stability/Build.txt)
- 实例化图像比较期间关闭 SRP Batcher，确保测试实际进入 GPU Instancing 路径；完整 118 项测试再次通过。
- 反射探针图像检查等待原生探针完成一次帧更新注册后，再验证采样颜色；分别验证红/绿探针、A→B→A、管线替换，以及 SRP Batcher 开启/关闭的一致性。
- 当前无连接的 Android 设备；没有真机安装、视觉或性能通过结论。

以下为确定性的 128×96 功能图像。差异图放大 20 倍；黑色表示无差异，不是未执行渲染。

| 检查 | 基线/关闭过渡 | 结果 | 差异 |
| --- | --- | --- | --- |
| 相机 A→B→A | [A](MobileValidation/Stability/camera-a.png) | [ABA](MobileValidation/Stability/camera-aba.png) | [差异 ×20](MobileValidation/Stability/camera-difference-x20.png) |
| 级联过渡 | [关闭](MobileValidation/Stability/cascade-before.png) | [开启 0.1](MobileValidation/Stability/cascade-after.png) | [差异 ×20](MobileValidation/Stability/cascade-difference-x20.png) |

[上下/左右方向检查](MobileValidation/Stability/orientation.png) · [URP Procedural Skybox 近似](MobileValidation/Stability/skybox-procedural.png)
