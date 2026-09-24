# HDRP Mobile 制作流程验证报告

验证日期：2026-09-24 至 2026-09-25。环境：Unity 2022.3.17f1、HDRP 14.0.9 本地分支、Windows D3D11、RTX 3060 Laptop。此报告不代表 Android 真机性能验收。

本报告记录环境 IBL 开发前的制作流程基线；后续 83 项回归及环境光照构建记录见 [Mobile-Environment.md](Mobile-Environment.md)。下方 APK 大小和哈希为当时的基线，当前同名 APK 已由环境光照版本更新。

## 交付入口

- `Rendering > HDRP Mobile > Settings`：相机、有效管线与 Volume 参数、实际 Mobile 曝光模式、天空来源、烘焙状态、可定位诊断及烘焙入口。
- Pipeline Asset Inspector：分辨率、灯光与阴影、天空与雾、曝光与后处理、诊断分组。
- 标准 Exposure Volume 继续作为参数来源；窗口不写混合结果或共享 Profile。
- `MobileExposureControl.Reset(Camera)`：传送、镜头切换时显式重置。
- 验收场景：`Assets/MobileBenchmark/Benchmark.unity`，对应 `Mobile.asset`、`Volume.asset`、LightingData、Lightmap 和 ReflectionProbe 资源均随场景交付。
- 支持矩阵及工作流：[Mobile-Production.md](Mobile-Production.md)。

## GPU 与编辑器回归

通过 73 项 EditMode 测试，0 失败、0 跳过，耗时 28.96 秒。MCP 测试任务：`7f175702ff954c82a14ef66da14eec38`。

覆盖 MobileExposureTests、MobileMeterTests、MobileImageTests、MobileRenderTests、MobileStabilityTests、MobileShadowTests、MobileURPMaterialTests。

| 范围 | 证据 |
| --- | --- |
| 曝光 | 六组合成亮度/补偿/目标灰度/适应测试，将 raster 测光结果与 Desktop 使用的 EV 和适应公式对照；每例 EV 断言误差 ≤0.05，包括受控非均匀输入，严于该类输入的 0.25 EV 要求 |
| 历史 | 相机独立资源、模式切换/关闭释放、显式重置、真实相机自动曝光收敛、多相机与管线替换；固定/物理曝光和 Scene View 路径保留原回归 |
| 材质 | HDRP 属性布局、URP BRDF 对照、透明混合、裁剪、双面、自发光、实例化/蒙皮；新增 Detail 覆盖与遮罩、原始 UV1 取样图像测试 |
| 反射 | 单探针、多相机、替换后端、双探针颜色混合；原生贴图/盒投影/混合距离释放后恢复。修正同步后必须刷新原生探针缓存的首次绑定问题 |
| 稳定性 | 有限像素检查、目标纹理尺寸切换、构造异常、资源释放和现有阴影回归 |
| 资源保护 | 测试保存并逐字节检查项目 Volume Profile；场景路径和管线状态恢复 |

这些是具体用例的通过结果，不是任意复杂场景与 Desktop 像素完全相同的保证。两条路径不共享上一帧曝光纹理；BRDF、天空近似及测光网格也存在差异。

## 实际烘焙与重载

所有批处理在 `MobileValidationRun/Project` 隔离副本执行，没有在制作编辑器调用退出式入口。

- `bake.log`：普通和方向 Lightmap 均完成 Progressive CPU 烘焙、HDRP 反射探针烘焙、保存、关闭/重开及 Mobile 有限像素/可见中间调检查。两次均检测到 1 张 Lightmap 和 18 个 SH 探针，方向模式另检查方向图存在。
- `workflow.log`：`MOBILE_WORKFLOW_CANCEL_PASSED`、`MOBILE_WORKFLOW_BAKE_RELOAD_PASSED`。调用 Settings 窗口所用的同一服务，验证立即取消恢复，再验证异步完整烘焙/重载。额外比对 GraphicsSettings、QualitySettings、Volume Profile 文件和 LightingSettings 引用。
- `cancel.log`：`MOBILE_ACTIVE_BAKE_CANCEL_RELOAD_PASSED`。在 Lightmapping.isRunning 为真时取消，逐字节检查夹具既有文件（含 .meta）、Graphics/Quality 设置，恢复后重载并验证 Mobile 图像。
- 最终夹具采用 32 texel/unit、1024 最大图集，包含 Detail 材质、Baked 点光源、Mixed Baked Indirect 太阳、动态角色和反射探针；输出 2 组方向 Lightmap。最终工作流日志无 UV 重叠警告。
- 线性验收图：`MobileValidationRun/Project/mobile-bake-NonDirectional.exr`、`mobile-bake-Single, Dual.exr`（Unity 对 CombinedDirectional 枚举的字符串表示）、`mobile-bake-Workflow.exr`。

取消或异常会恢复明确备份的原有烘焙文件和引用。烘焙器新建的无引用文件会保留，避免删除同时新增的用户资源；场景仅在成功后保存。批处理生产验证入口另要求 `.hdrp-mobile-validation` 文件，其内容必须是该隔离副本的绝对 Assets 路径。

## Android 构建

首次与最终增量 Player 构建均成功，使用 Vulkan、IL2CPP、ARM64，生成 APK 而非仅导出 Gradle 工程。日志包含 MobileForward 与三个自动曝光 Pass 的 Vulkan 编译记录；最终 Lit MobileForward 保留并编译/命中缓存 16 个变体，包含新增 Detail 夹具。已检查 APK 包含 `lib/arm64-v8a/libil2cpp.so`、`libunity.so` 及 AndroidManifest，ELF machine=183（AArch64）。

最终夹具增量构建记录与产物位于 `MobileValidationRun/android-final.log` 和 `MobileValidationRun/Project/mobile-benchmark.apk`。

最终 APK 大小：36,998,679 字节。SHA-256：`BE6148471459411BA05CBD91E004B436A9CDE609FDAD0E0C591F50D2E45F1421`。

## 制作项目状态与限制

制作编辑器保持 `Assets/mobile.unity` 和 `HDRP Balanced`，默认 Profile 仍为 Automatic、Fixed EV 10。该 Profile 的 SHA-256 为 `15ECAFA120E0816123B5FBA99AF273C79C08E7D9B69026FA5889B0610F707D59`，最终检查与此前一致。

不支持 Shadowmask/Subtractive、Shader Graph、软粒子、实时 GI、LPPV、折射、高级材质模型、实时/平面反射与自定义探针 Proxy Volume。Premultiply 保留既有预乘纹理约定。未进行 Android 真机图像、热稳定性、功耗或帧率验收。
