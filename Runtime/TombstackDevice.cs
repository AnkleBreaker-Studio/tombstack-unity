using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Snapshots static device + runtime context (model, OS, CPU/GPU, screen, locale, engine) into a
    /// <see cref="DevicePayload"/>. MAIN-THREAD ONLY — <see cref="SystemInfo"/> and <see cref="Screen"/>
    /// must be read on the Unity main thread, so this is called once from Tombstack.Init and the result
    /// is cached + attached to crash/bug reports (which may be built off-thread). Fail-soft at the call
    /// site: a throw leaves the device context null and reporting continues without it.
    /// </summary>
    internal static class TombstackDevice
    {
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
            // refreshRateRatio is the Unity 6 (6000.0+) API; .value is the refresh in Hz.
            float refresh = (float)Screen.currentResolution.refreshRateRatio.value;

            return new DevicePayload
            {
                model = SystemInfo.deviceModel,
                type = SystemInfo.deviceType.ToString(),
                os = SystemInfo.operatingSystem,
                osFamily = SystemInfo.operatingSystemFamily.ToString(),
                cpu = SystemInfo.processorType,
                cpuCount = SystemInfo.processorCount,
                ramMB = SystemInfo.systemMemorySize,
                gpu = SystemInfo.graphicsDeviceName,
                gpuVendor = SystemInfo.graphicsDeviceVendor,
                gpuVersion = SystemInfo.graphicsDeviceVersion,
                gpuApi = SystemInfo.graphicsDeviceType.ToString(),
                vramMB = SystemInfo.graphicsMemorySize,
                screen = Screen.width + "x" + Screen.height,
                screenDpi = Screen.dpi,
                refreshRate = refresh,
                orientation = Screen.orientation.ToString(),
                fullscreen = Screen.fullScreen,
                language = Application.systemLanguage.ToString(),
                engine = Application.unityVersion,
                scriptingBackend = backend,
                platform = Application.platform.ToString(),
            };
        }
    }
}
