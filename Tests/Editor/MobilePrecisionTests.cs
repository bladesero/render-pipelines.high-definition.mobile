using System;
using System.IO;
using System.Runtime.InteropServices;
using NUnit.Framework;
using UnityEngine;

namespace UnityEditor.Rendering.HighDefinition.Tests
{
    public class MobilePrecisionTests
    {
        [TestCase("HDRP/Lit", "MobileForward")]
        [TestCase("Hidden/HDRP/Mobile/Deferred", "MobileDeferredNative")]
        [TestCase("Hidden/HDRP/Mobile/Sky", "MobileReflectionOctahedral")]
        [TestCase("Hidden/HDRP/Mobile/Composite", "MobileReflectionCapture")]
        public void AndroidVulkanPrecisionVariantsCompile(string shaderName, string passName)
        {
            var subshader = ShaderUtil.GetShaderData(Shader.Find(shaderName)).GetSubshader(0);
            ShaderData.Pass pass = null;
            for (int i = 0; i < subshader.PassCount; i++)
                if (subshader.GetPass(i).Name == passName) { pass = subshader.GetPass(i); break; }
            Assert.IsNotNull(pass, passName);
            // Vertex-stage smoke test. Shadow coverage below checks both stages.
            var variant = pass.CompileVariant(ShaderType.Vertex, new[] { "MOBILE_EXTENDED_LIT" },
                ShaderCompilerPlatform.Vulkan, BuildTarget.Android);
            Assert.IsTrue(variant.Success, string.Join("\n", variant.Messages));
            Assert.Greater(variant.ShaderData.Length, 0);
        }

        [TestCase("HDRP/Lit", "ShadowCaster", ShaderCompilerPlatform.D3D)]
        [TestCase("HDRP/Unlit", "ShadowCaster", ShaderCompilerPlatform.D3D)]
        [TestCase("HDRP/Lit", "ShadowCaster", ShaderCompilerPlatform.Vulkan)]
        [TestCase("HDRP/Unlit", "ShadowCaster", ShaderCompilerPlatform.Vulkan)]
        [TestCase("Hidden/HDRP/Tests/MobileShadowTransition", "Cascade", ShaderCompilerPlatform.D3D)]
        [TestCase("Hidden/HDRP/Tests/MobileShadowTransition", "Local", ShaderCompilerPlatform.D3D)]
        [TestCase("Hidden/HDRP/Tests/MobileShadowTransition", "Cascade", ShaderCompilerPlatform.Vulkan)]
        [TestCase("Hidden/HDRP/Tests/MobileShadowTransition", "Local", ShaderCompilerPlatform.Vulkan)]
        public void ShadowVariantsCompileWithoutMinimumPrecision(string shaderName, string passName, ShaderCompilerPlatform platform)
        {
            var shader = Shader.Find(shaderName);
            Assert.IsNotNull(shader);
            var subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            ShaderData.Pass pass = null;
            for (int i = 0; i < subshader.PassCount; i++)
                if (subshader.GetPass(i).Name == passName) { pass = subshader.GetPass(i); break; }
            Assert.IsNotNull(pass, passName);
            // Alpha clipping exercises caster coverage arithmetic as well as transforms.
            var keywords = passName == "ShadowCaster"
                ? new[] { "HDRP_MOBILE_RENDERER", "_ALPHATEST_ON", "INSTANCING_ON" }
                : Array.Empty<string>();
            foreach (var stage in new[] { ShaderType.Vertex, ShaderType.Fragment })
            {
                var variant = pass.CompileVariant(stage, keywords, platform,
                    platform == ShaderCompilerPlatform.Vulkan ? BuildTarget.Android : BuildTarget.StandaloneWindows64, true);
                Assert.IsTrue(variant.Success, string.Join("\n", variant.Messages));
                Assert.Greater(variant.ShaderData.Length, 0);
                if (passName == "ShadowCaster" && stage == ShaderType.Fragment)
                    Assert.IsTrue(Array.Exists(variant.TextureBindings, binding =>
                        binding.Name == (shaderName == "HDRP/Lit" ? "_BaseColorMap" : "_UnlitColorMap")),
                        "Diagnostic discard alone must not count as material alpha coverage; the caster must also retain its alpha texture.");
                if (platform == ShaderCompilerPlatform.Vulkan && passName == "ShadowCaster" && stage == ShaderType.Fragment)
                    Assert.IsTrue(ContainsSpirvOpcode(variant.ShaderData, 252),
                        "The Android caster fragment must retain OpKill for alpha clipping; vertex compilation alone cannot verify coverage.");
#if UNITY_EDITOR_WIN
                if (platform != ShaderCompilerPlatform.D3D) continue;
                Assert.AreEqual(0, D3DDisassemble(variant.ShaderData, (UIntPtr)variant.ShaderData.Length, 0, null, out var blob));
                try
                {
                    string assembly = Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize().ToUInt64()).TrimEnd('\0');
                    StringAssert.DoesNotContain("min16f", assembly, shaderName + "/" + passName + "/" + stage);
                    if (passName == "ShadowCaster" && stage == ShaderType.Fragment)
                        StringAssert.Contains("discard", assembly, "The tested caster must retain alpha clipping.");
                    if (passName == "Cascade" || passName == "Local")
                        if (stage == ShaderType.Fragment)
                            StringAssert.Contains("sample_c", assembly, "The tested receiver must retain shadow comparisons.");
                }
                finally { Marshal.ReleaseComObject(blob); }
#endif
            }
        }

        [TestCase("HDRP/Lit", ShaderCompilerPlatform.D3D)]
        [TestCase("HDRP/Unlit", ShaderCompilerPlatform.D3D)]
        [TestCase("HDRP/Lit", ShaderCompilerPlatform.Vulkan)]
        [TestCase("HDRP/Unlit", ShaderCompilerPlatform.Vulkan)]
        public void ShadowVertexProgramRetainsAlphaTestDependency(string shaderName, ShaderCompilerPlatform platform)
        {
            var subshader = ShaderUtil.GetShaderData(Shader.Find(shaderName)).GetSubshader(0);
            ShaderData.Pass pass = null;
            for (int i = 0; i < subshader.PassCount; i++)
                if (subshader.GetPass(i).Name == "ShadowCaster") pass = subshader.GetPass(i);
            Assert.IsNotNull(pass);
            var target = platform == ShaderCompilerPlatform.Vulkan ? BuildTarget.Android : BuildTarget.StandaloneWindows64;
            var opaque = pass.CompileVariant(ShaderType.Vertex, new[] { "HDRP_MOBILE_RENDERER" }, platform, target, true);
            var cutout = pass.CompileVariant(ShaderType.Vertex, new[] { "HDRP_MOBILE_RENDERER", "_ALPHATEST_ON" }, platform, target, true);
            Assert.IsTrue(opaque.Success, string.Join("\n", opaque.Messages));
            Assert.IsTrue(cutout.Success, string.Join("\n", cutout.Messages));
            Assert.Greater(opaque.ShaderData.Length, 0);
            Assert.Greater(cutout.ShaderData.Length, 0);
            CollectionAssert.AreNotEqual(opaque.ShaderData, cutout.ShaderData,
                "Shadow batching must retain the alpha-test dependency in the vertex program, not only in the fragment program.");
        }

        static bool ContainsSpirvOpcode(byte[] bytes, uint opcode)
        {
            Assert.GreaterOrEqual(bytes.Length, 20, "SPIR-V must include its five-word header.");
            Assert.AreEqual(0x07230203u, BitConverter.ToUInt32(bytes, 0), "Expected SPIR-V bytecode.");
            for (int offset = 20; offset < bytes.Length;)
            {
                uint instruction = BitConverter.ToUInt32(bytes, offset);
                int length = (int)(instruction >> 16) * 4;
                Assert.GreaterOrEqual(length, 4, "Invalid SPIR-V instruction length.");
                Assert.LessOrEqual(offset + length, bytes.Length, "Truncated SPIR-V instruction.");
                if ((instruction & 0xffffu) == opcode) return true;
                offset += length;
            }
            return false;
        }

#if UNITY_EDITOR_WIN
        [ComImport, Guid("8BA5FB08-5195-40E2-AC58-0D989C3A0102"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface ShaderBlob
        {
            [PreserveSig] IntPtr GetBufferPointer();
            [PreserveSig] UIntPtr GetBufferSize();
        }

        [DllImport("d3dcompiler_47.dll", CallingConvention = CallingConvention.StdCall)]
        static extern int D3DDisassemble(byte[] source, UIntPtr size, uint flags,
            [MarshalAs(UnmanagedType.LPStr)] string comments, out ShaderBlob result);
#endif

        [TestCase("HDRP/Lit", "MobileForward")]
        [TestCase("Hidden/HDRP/Mobile/Deferred", "MobileDeferredSampled")]
        public void CompiledLightingContainsMinimum16BitArithmetic(string shaderName, string passName)
        {
#if UNITY_EDITOR_WIN
            var shader = Shader.Find(shaderName);
            Assert.IsNotNull(shader);
            var subshader = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            ShaderData.Pass pass = null;
            for (int i = 0; i < subshader.PassCount; i++)
                if (subshader.GetPass(i).Name == passName) { pass = subshader.GetPass(i); break; }
            Assert.IsNotNull(pass, passName);
            var variant = pass.CompileVariant(ShaderType.Fragment, new[] { "MOBILE_EXTENDED_LIT" },
                ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64, true);
            Assert.IsTrue(variant.Success, string.Join("\n", variant.Messages));
            Assert.Greater(variant.ShaderData.Length, 0);
            Assert.AreEqual(0, D3DDisassemble(variant.ShaderData, (UIntPtr)variant.ShaderData.Length, 0, null, out var blob));
            try
            {
                string assembly = Marshal.PtrToStringAnsi(blob.GetBufferPointer(), (int)blob.GetBufferSize().ToUInt64()).TrimEnd('\0');
                string folder = Path.Combine(Path.GetDirectoryName(Application.dataPath), "mobile-precision-validation");
                Directory.CreateDirectory(folder);
                File.WriteAllText(Path.Combine(folder, passName + ".dxbc.txt"), assembly);
                StringAssert.IsMatch(@"(?m)^\s*(?:add|mul|mad|dp3|rsq|max)(?:_sat)?\s+[^,\r\n]+\{min16f\}", assembly,
                    "FP16 arithmetic, rather than only storage/conversions, must survive shader compilation.");
            }
            finally { Marshal.ReleaseComObject(blob); }
#else
            Assert.Ignore("DXBC disassembly validation requires a Windows Editor.");
#endif
        }
    }
}
