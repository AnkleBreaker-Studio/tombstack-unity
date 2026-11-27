using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Snapshots static device + runtime context (model, OS, CPU/GPU, screen, locale, engine) into a
    /// <see cref="DevicePayload"/>. MAIN-THREAD ONLY — <see cref="SystemInfo"/> and <see cref="Screen"/>
    /// must be read on the Unity main thread, so this is called once from Tombstack.Init and the result
    /// is cached + attached to crash/bug reports (which may be built off-thread). Fail-soft at the call
    /// site: a throw leaves the device context null and reporting continues without it.
    ///
    /// Every value is clamped to the server's device schema (src/lib/device-schema.ts). The crash and
    /// bug-report schemas reject the WHOLE report when one device field is out of bounds, and the
    /// uploader deletes such a 400 as poison, so one long GPU string or a non-finite refresh rate used to
    /// cost every report of that device. tests/unity-device-clamp.test.ts pins these bounds to the schema.
    /// </summary>
    internal static class TombstackDevice
    {
        private const int MAX_MODEL = 256;
        private const int MAX_TYPE = 32;
        private const int MAX_OS = 256;
        private const int MAX_OS_FAMILY = 48;
        private const int MAX_CPU = 256;
        private const int MAX_GPU = 256;
        private const int MAX_GPU_VENDOR = 256;
        private const int MAX_GPU_VERSION = 256;
        private const int MAX_GPU_API = 64;
        private const int MAX_SCREEN = 32;
        private const int MAX_ORIENTATION = 32;
        private const int MAX_LANGUAGE = 48;
        private const int MAX_ENGINE = 48;
        private const int MAX_SCRIPTING_BACKEND = 32;
        private const int MAX_PLATFORM = 48;
        private const int MAX_CPU_COUNT = 4096;
        private const int MAX_MEMORY_MB = 4194304;
        private const float MAX_SCREEN_DPI = 10000f;
        private const float MAX_REFRESH_RATE = 1000f;

        internal static DevicePayload Capture()
        {
            string backend;
#if ENABLE_IL2CPP
            backend = "IL2CPP";
#elif ENABLE_MONO
            backend = "Mono";
#else
            backend = "Unknown";
#endif
            // refreshRateRatio is the Unity 6 (6000.0+) API; .value is the refresh in Hz. A zero
            // denominator (headless processes) makes it NaN or Infinity: clampFloat turns that into 0.
            float refresh = (float)Screen.currentResolution.refreshRateRatio.value;

            return new DevicePayload
            {
                model = clampText(SystemInfo.deviceModel, MAX_MODEL),
                type = clampText(SystemInfo.deviceType.ToString(), MAX_TYPE),
                os = clampText(SystemInfo.operatingSystem, MAX_OS),
                osFamily = clampText(SystemInfo.operatingSystemFamily.ToString(), MAX_OS_FAMILY),
                cpu = clampText(SystemInfo.processorType, MAX_CPU),
                cpuCount = clampInt(SystemInfo.processorCount, MAX_CPU_COUNT),
                ramMB = clampInt(SystemInfo.systemMemorySize, MAX_MEMORY_MB),
                gpu = clampText(SystemInfo.graphicsDeviceName, MAX_GPU),
                gpuVendor = clampText(SystemInfo.graphicsDeviceVendor, MAX_GPU_VENDOR),
                gpuVersion = clampText(SystemInfo.graphicsDeviceVersion, MAX_GPU_VERSION),
                gpuApi = clampText(SystemInfo.graphicsDeviceType.ToString(), MAX_GPU_API),
                vramMB = clampInt(SystemInfo.graphicsMemorySize, MAX_MEMORY_MB),
                screen = clampText(Screen.width + "x" + Screen.height, MAX_SCREEN),
                screenDpi = clampFloat(Screen.dpi, MAX_SCREEN_DPI),
                refreshRate = clampFloat(refresh, MAX_REFRESH_RATE),
                orientation = clampText(Screen.orientation.ToString(), MAX_ORIENTATION),
                fullscreen = Screen.fullScreen,
                language = clampText(Application.systemLanguage.ToString(), MAX_LANGUAGE),
                engine = clampText(Application.unityVersion, MAX_ENGINE),
                scriptingBackend = clampText(backend, MAX_SCRIPTING_BACKEND),
                platform = clampText(Application.platform.ToString(), MAX_PLATFORM),
            };
        }

        /// <summary>At most <paramref name="max"/> UTF-16 units (the schema counts the same units), never
        /// ending on the first half of a surrogate pair.</summary>
        private static string clampText(string value, int max)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= max) return value;
            int cut = max;
            if (char.IsHighSurrogate(value[cut - 1])) cut--;
            return value.Substring(0, cut);
        }

        /// <summary>Into [0, <paramref name="max"/>]; a negative value means "unknown" and becomes 0.</summary>
        private static int clampInt(int value, int max)
        {
            if (value < 0) return 0;
            return value > max ? max : value;
        }

        /// <summary>Into [0, <paramref name="max"/>]. NaN and Infinity become 0 ("unknown"): JsonUtility writes
        /// them as bare tokens, which are not JSON, and the whole report would be refused.</summary>
        private static float clampFloat(float value, float max)
        {
            if (float.IsNaN(value) || float.IsInfinity(value) || value < 0f) return 0f;
            return value > max ? max : value;
        }
    }
}
