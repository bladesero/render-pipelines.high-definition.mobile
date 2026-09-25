namespace UnityEngine.Rendering.HighDefinition
{
    // A raster-only fallback. Never creates a SkyRenderer, atmospheric LUT or probe.
    internal struct MobileSky
    {
        internal int mode; // 0: clear, 1: cube, 2: gradient, 3: legacy procedural, 4: physical approximation
        internal Texture cube;
        internal Color top, middle, bottom, tint;
        internal float intensity, rotation, diffusion, sunSize;
        internal Color horizonTint, zenithTint;
        internal Vector4 physicalParameters; // Exponential tint power/scale, color saturation, reserved.
        internal bool cameraExposure;

        static float Intensity(SkySettings sky)
        {
            switch (sky.skyIntensityMode.value)
            {
                case SkyIntensityMode.Exposure: return MobileExposure.FromEV100(-sky.exposure.value, 0, MobileExposure.LensScale);
                case SkyIntensityMode.Lux: return sky.desiredLuxValue.value / Mathf.Max(sky.upperHemisphereLuxValue.value, 0.00001f);
                default: return sky.multiplier.value;
            }
        }

        internal static MobileSky Resolve(MobileRenderSettings settings, VolumeStack stack, Material legacySkybox, float sunIntensity)
        {
            var result = new MobileSky { tint = Color.white, diffusion = 1, sunSize = 0.04f, cameraExposure = true };
            if (settings.sky != null)
            {
                result.mode = 1; result.cube = settings.sky; result.intensity = Mathf.Max(0, settings.skyIntensity); return result;
            }
            if (!settings.skyFallback) return result;
            var environment = stack.GetComponent<VisualEnvironment>();
            int type = environment.active ? environment.skyType.value : 0;
            var hdri = stack.GetComponent<HDRISky>();
            if (type == (int)SkyType.HDRI && hdri.active && hdri.hdriSky.value != null)
            {
                result.mode = 1; result.cube = hdri.hdriSky.value; result.intensity = Intensity(hdri);
                result.rotation = -hdri.rotation.value * Mathf.Deg2Rad; return result;
            }
            var gradient = stack.GetComponent<GradientSky>();
            if (type == (int)SkyType.Gradient && gradient.active)
            {
                result.mode = 2; result.top = gradient.top.value; result.middle = gradient.middle.value;
                result.bottom = gradient.bottom.value; result.diffusion = gradient.gradientDiffusion.value;
                result.intensity = Intensity(gradient); return result;
            }
            var physical = stack.GetComponent<PhysicallyBasedSky>();
            if (type == (int)SkyType.PhysicallyBased && physical.active)
            {
                result.mode = 4;
                result.top = new Color(0.15f, 0.35f, 0.75f) * physical.airTint.value.linear;
                result.middle = new Color(0.65f, 0.75f, 0.85f) * physical.airTint.value.linear;
                // Match Desktop's ground albedo and artistic tint color conventions.
                result.bottom = physical.groundTint.value;
                result.horizonTint = physical.horizonTint.value;
                result.zenithTint = physical.zenithTint.value;
                float power = 10f * physical.horizonZenithShift.value;
                // Use the linear limit around zero to avoid cancellation in exp(x) - 1.
                float scale = Mathf.Abs(power) < 0.001f ? 0f : 1f / (Mathf.Exp(power) - 1f);
                result.physicalParameters = new Vector4(power, scale, physical.colorSaturation.value, 0);
                result.intensity = Intensity(physical) * Mathf.Max(0, sunIntensity) * 0.05f;
                return result;
            }
            if (legacySkybox == null || legacySkybox.shader == null) return result;
            // URP's legacy skyboxes have display-referred intensity, independent of
            // HDRP physical EV. Preserve their own material exposure in this fallback.
            result.cameraExposure = false;
            result.intensity = legacySkybox.HasProperty("_Exposure") ? legacySkybox.GetFloat("_Exposure") : 1;
            if (legacySkybox.shader.name == "Skybox/Cubemap" && legacySkybox.GetTexture("_Tex") is Cubemap cube)
            {
                result.mode = 1; result.cube = cube;
                result.tint = legacySkybox.GetColor("_Tint").linear * 2;
                result.rotation = -legacySkybox.GetFloat("_Rotation") * Mathf.Deg2Rad;
            }
            else if (legacySkybox.shader.name == "Skybox/Procedural")
            {
                result.mode = 3;
                // URP's neutral grey sky tint modulates atmospheric scattering; it
                // is not the sky's final RGB. Preserve a blue daylight base at grey.
                var tint = legacySkybox.GetColor("_SkyTint").linear / Mathf.GammaToLinearSpace(0.5f);
                result.top = new Color(0.15f, 0.35f, 0.75f) * tint;
                result.middle = new Color(0.65f, 0.75f, 0.85f) * tint;
                result.bottom = legacySkybox.GetColor("_GroundColor").linear;
                result.diffusion = Mathf.Max(0.1f, legacySkybox.GetFloat("_AtmosphereThickness"));
                result.sunSize = Mathf.Clamp(legacySkybox.GetFloat("_SunSize"), 0.001f, 0.5f);
            }
            return result;
        }
    }
}
