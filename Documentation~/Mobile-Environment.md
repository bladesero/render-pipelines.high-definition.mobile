# Mobile 轻量环境光照

Mobile 可独立从当前天空生成漫反射辐照度和 GGX 环境反射，不初始化 Desktop SkyManager，不进行运行时 CPU 测光或环境纹理回读。它不计算场景物体之间的光照反弹，也不捕获场景几何。

## 制作入口

在 Mobile Pipeline Asset 的 **Environment Lighting** 分组开启环境光照。默认反射分辨率为每面 32×32，可选 64×64；漫反射为每面 8×8。天空来源沿用显式 Mobile cubemap、相机 Volume 的 HDRI/Gradient/Physically Based 近似天空及已有 legacy fallback。强度、旋转、颜色变化会触发更新。

`Rendering > HDRP Mobile > Settings` 显示选定相机的缓存分辨率、完成代数和更新进度，并提供 **Rebuild Camera Environment**。运行时代码可调用 `MobileEnvironmentControl.Reset(camera)`；原地更新天空纹理但未改变 Unity texture update count 时，应显式调用该接口。下一次相机渲染会重新预热。

## 与烘焙的关系

| 对象/配置 | 漫反射来源 |
| --- | --- |
| 有 Lightmap 的 Renderer | 原 Lightmap，方向图解码保持不变，不重复添加天空漫反射 |
| Prefer Baked Probes，场景有烘焙探针集 | 原生每对象 SH |
| Prefer Baked Probes，场景没有烘焙探针集 | 实时天空辐照度 |
| Runtime Sky，无 Lightmap | 实时天空辐照度，替代 SH |

这是场景范围的来源策略，不是每个 Renderer 的探针有效性检测。Prefer Baked Probes 下，Light Probe Usage Off 或 Custom Provided 仍由原生 SH 绑定决定；没有烘焙探针集时，Runtime Sky 路径也不会保留自定义 SH。有效的黑色 SH/探针不能通过颜色自动判为“缺失”。室内和动态角色优先使用烘焙探针；仅凭天空不能获得室内遮挡。

局部烘焙 Reflection Probe 继续支持双探针、盒投影及混合距离；探针影响范围剩余权重由天空反射补足。默认无限范围的 Unity 天空绑定不被误认成局部探针。局部黑色反射探针仍能覆盖天空反射。实时场景反射、平面反射和自定义 Proxy Volume 不在支持范围内。

标准 `Indirect Lighting Controller` 的 diffuse、reflection lighting、reflection probe intensity multiplier 可调节结果；Light Layer 过滤不支持，诊断会提示。

## 更新、曝光与开销

- 每个相机独立维护天空快照、漫反射及反射双缓冲。Volume 混合结果按相机解析，缓存不串用。
- 首次渲染完整预热：32 档约 48 个 raster draw，64 档约 54 个，另有源 cubemap mip 生成。首帧并非零成本。
- 后续天空不变时不执行卷积。变化时一次捕获六面，默认每次相机渲染处理 6 个“面/mip”任务；32 档约 7 次、64 档约 8 次渲染完成一代。手动多次 Camera.Render 会相应推进多次。
- 更新中的旧完整缓存继续可见，完成后整体交换。持续变化的天空不会反复重启未完成工作，因此会有更新延迟。
- 每个卷积像素使用固定 64 个 Hammersley 样本；不使用 Compute Shader。32 档纹理数据约 0.19 MiB/相机，64 档约 0.76 MiB/相机，不含驱动额外分配。
- 缓存保存未曝光辐亮度，采用固定 1/1024 存储比例以扩展 RGBA16F 的高光范围。材质照明只应用一次当前相机曝光。曝光或相机位移变化不重新卷积；Unlit 保持原亮度语义。
- 相机销毁、对象停用、长期未使用和管线释放时清理缓存；关闭 Environment Lighting 会在下一次渲染移除对应相机缓存。

Physically Based Sky 使用近地面、水平地平线的轻量近似：上半球向地平线集中薄雾，下半球逐渐过渡到受太阳高度和简化天空漫射照明的 Lambert 地面，太阳盘被地平线遮挡。支持 `Horizon Tint`、`Zenith Tint`、`Horizon Zenith Shift`（沿用 Desktop 的角度与指数插值语义）、`Color Saturation` 和 `Ground Tint`。这些参数和太阳变化会触发环境缓存更新，背景与环境捕获共用采样函数。

这不是对 Desktop 参考图像完成误差标定的拟合，也没有完整大气散射、气溶胶、行星曲率/高海拔地平线、地面纹理或云。薄雾宽度、太阳透射和天空漫射比例使用固定近似系数。地面颜色现在作为反照率参与光照，不再是固定下半球渐变颜色；已有场景的地面反射可能随之变化。Legacy 天空保留原有路径。该方案没有通过 Android 真机帧率、功耗或热稳定性验收。

## 验证记录

2026-09-25，地平线/地面近似更新：隔离工程 Windows D3D11 EditMode 验证 15/15 通过，结果见工程根目录 `MobileValidationRun/sky-tests-final.xml`。覆盖地平线连续、地面随太阳高度变化、地下太阳遮挡、Horizon Zenith Shift 的 -1/0/1 边界、同方向背景/反射一致性、染色变化触发缓存更新，以及既有 HDRI/Gradient/legacy 天空、曝光、相机独立 Volume、Lightmap 优先级回归。本次未执行 Android 构建或 Desktop 图像误差标定；下述 Android 构建记录来自此前版本。

2026-09-25，Unity 2022.3.17f1 / HDRP 14.0.9 / Windows D3D11：83 项 Mobile EditMode 回归全部通过，无失败、无跳过，任务 `8681891bee2a48fe9f9e2a5ce90c9d21`。测试期间编辑器曾长时间停顿，任务累计耗时不作为性能数据。

新增用例覆盖 32/64 档六面及所有 GGX mip 的均匀输入能量、方向和旋转、非均匀输入粗糙度扩散、缓存原子发布、持续变化不饥饿、多相机与资源释放。实际 Forward 图像覆盖漫反射/金属反射、EV 加一亮度减半且不重建缓存、局部探针边界/黑色探针、相机独立 Volume，以及 Lightmap 不重复叠加天空漫反射。GPU 测试对指定 cubemap 面/mip 显式复制后回读，回读只存在于测试代码。

制作编辑器保持 `Assets/mobile.unity`，场景无未保存改动，Pipeline 为 `HDRP Balanced`。默认 Volume Profile SHA-256 仍为 `15ECAFA120E0816123B5FBA99AF273C79C08E7D9B69026FA5889B0610F707D59`，测试没有重置 EV。

Android Vulkan/IL2CPP/ARM64 在 `MobileValidationRun/Project` 隔离工程构建。最终结果见 `MobileValidationRun/environment-final-android.log` 与 `mobile-benchmark.apk`；以前的实际烘焙/重载结果单独记录在 [Mobile-Production-Validation.md](Mobile-Production-Validation.md)，本次没有重新执行完整烘焙验收。

最终构建成功（`HDRP_MOBILE_ANDROID_BUILD=Succeeded`）。Capture、Diffuse、Specular 三个新增 Pass 均实际编译 Vulkan 变体；APK 包含 ARM64 `libil2cpp.so` 和 `libunity.so`。APK 为 48,789,075 字节，SHA-256：`DA5C68D2D5A925787A4B0D5E1168882FE77622ECF45B6EE5ABEF5507F651ED9A`。构建成功不等同于真机运行或性能验证。
