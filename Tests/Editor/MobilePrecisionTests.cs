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
            // Vulkan compiles all stages through ShaderType.Vertex.
            var variant = pass.CompileVariant(ShaderType.Vertex, new[] { "MOBILE_EXTENDED_LIT" },
                ShaderCompilerPlatform.Vulkan, BuildTarget.Android);
            Assert.IsTrue(variant.Success, string.Join("\n", variant.Messages));
            Assert.Greater(variant.ShaderData.Length, 0);
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
