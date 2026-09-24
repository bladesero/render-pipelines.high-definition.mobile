using System;
using System.Collections.Generic;
using System.IO;

namespace UnityEngine.Rendering.HighDefinition
{
    /// <summary>Opt-in fixed-route benchmark component used by the generated mobile sample.</summary>
    public sealed class MobileBenchmark : MonoBehaviour
    {
        public Camera benchmarkCamera;
        public Transform animatedBone;
        [Min(0)] public float warmupSeconds = 300;
        [Min(1)] public float captureSeconds = 600;
        [Tooltip("Record the operator's observed device temperature, or leave unavailable. Android battery temperature is not GPU temperature.")]
        public string temperatureObservation = "unavailable";
        readonly List<float> m_Frames = new List<float>(40000);
        readonly List<float> m_CPU = new List<float>(40000);
        readonly List<float> m_GPU = new List<float>(40000);
        readonly FrameTiming[] m_Timing = new FrameTiming[1];
        double m_Start;
        bool m_Complete, m_FixedResolution = true, m_Interrupted;
        int m_PreviousFrameRate, m_PreviousVSync;

        [Serializable]
        class Report
        {
            public string contentVersion = "HDRP-Mobile-Benchmark-1";
            public string utc, device, gpu, driver, operatingSystem, unity, temperature;
            public int samples, width, height, displayWidth, displayHeight, cpuSamples, gpuSamples;
            public float warmupSeconds, captureSeconds, p50Ms, p95Ms, p99Ms, cpuP95Ms, gpuP95Ms;
            public bool fixed1080p, interrupted, passed;
        }

        void OnEnable()
        {
            m_Start = Time.realtimeSinceStartupAsDouble;
            m_PreviousFrameRate = Application.targetFrameRate;
            m_PreviousVSync = QualitySettings.vSyncCount;
            Application.targetFrameRate = 60;
            QualitySettings.vSyncCount = 0;
            m_Frames.Clear(); m_CPU.Clear(); m_GPU.Clear();
            m_Complete = false; m_FixedResolution = true; m_Interrupted = false;
        }

        void OnDisable()
        {
            Application.targetFrameRate = m_PreviousFrameRate;
            QualitySettings.vSyncCount = m_PreviousVSync;
        }

        void OnApplicationPause(bool paused)
        {
            if (paused && !m_Complete) m_Interrupted = true;
        }

        void Update()
        {
            double elapsed = Time.realtimeSinceStartupAsDouble - m_Start;
            float phase = (float)(elapsed % 30) * (2 * Mathf.PI / 30);
            if (benchmarkCamera != null)
            {
                benchmarkCamera.transform.position = new Vector3(Mathf.Sin(phase) * 8, 3, Mathf.Cos(phase) * 8);
                benchmarkCamera.transform.LookAt(new Vector3(0, 1, 0));
            }
            if (animatedBone != null) animatedBone.localRotation = Quaternion.Euler(0, 0, Mathf.Sin(phase * 5) * 25);
            if (m_Complete) return;
            FrameTimingManager.CaptureFrameTimings();
            if (elapsed < warmupSeconds) return;
            var asset = GraphicsSettings.currentRenderPipeline as HDRenderPipelineAsset;
            Vector2Int renderSize = benchmarkCamera != null && asset != null ? asset.mobileSettings.GetRenderSize(benchmarkCamera) : Vector2Int.zero;
            m_FixedResolution &= renderSize.x == 1920 && renderSize.y == 1080
                && asset != null && asset.rendererMode == HDRendererMode.Mobile && Mathf.Approximately(asset.mobileSettings.renderScale, 1);
            m_Frames.Add(Time.unscaledDeltaTime * 1000);
            if (FrameTimingManager.GetLatestTimings(1, m_Timing) > 0)
            {
                if (m_Timing[0].cpuFrameTime > 0) m_CPU.Add((float)m_Timing[0].cpuFrameTime);
                if (m_Timing[0].gpuFrameTime > 0) m_GPU.Add((float)m_Timing[0].gpuFrameTime);
            }
            if (elapsed < warmupSeconds + captureSeconds) return;
            m_Complete = true;
            float p95 = Percentile(m_Frames, 0.95f);
            var report = new Report
            {
                utc = DateTime.UtcNow.ToString("O"), device = SystemInfo.deviceModel, gpu = SystemInfo.graphicsDeviceName,
                driver = SystemInfo.graphicsDeviceVersion, operatingSystem = SystemInfo.operatingSystem,
                unity = Application.unityVersion, temperature = temperatureObservation,
                samples = m_Frames.Count, cpuSamples = m_CPU.Count, gpuSamples = m_GPU.Count,
                width = renderSize.x, height = renderSize.y,
                displayWidth = Screen.width, displayHeight = Screen.height,
                warmupSeconds = warmupSeconds, captureSeconds = captureSeconds,
                p50Ms = Percentile(m_Frames, 0.5f), p95Ms = p95, p99Ms = Percentile(m_Frames, 0.99f),
                cpuP95Ms = Percentile(m_CPU, 0.95f), gpuP95Ms = Percentile(m_GPU, 0.95f),
                fixed1080p = m_FixedResolution,
                interrupted = m_Interrupted,
                passed = !m_Interrupted && m_FixedResolution && p95 <= 16.67f && m_Frames.Count > 0 && warmupSeconds >= 300 && captureSeconds >= 600
            };
            string path = Path.Combine(Application.persistentDataPath, "hdrp-mobile-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + ".json");
            File.WriteAllText(path, JsonUtility.ToJson(report, true));
            Debug.Log("HDRP Mobile benchmark saved: " + path + ". Passed=" + report.passed);
        }

        internal static float Percentile(List<float> values, float fraction)
        {
            if (values.Count == 0) return -1;
            values.Sort();
            return values[Mathf.Clamp(Mathf.CeilToInt(fraction * values.Count) - 1, 0, values.Count - 1)];
        }
    }
}
