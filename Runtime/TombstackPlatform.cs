using System.Runtime.InteropServices;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>Maps Unity runtime info to the ingestion contract's os/arch whitelist.</summary>
    internal static class TombstackPlatform
    {
        // An empty bundleVersion would serialize buildVersion:"" and fail the server's
        // min(1) schema, 400ing (and silently dropping) every crash/event/heartbeat.
        private const string FALLBACK_BUILD_VERSION = "0.0.0";

        /// <summary>Project build version, guarded against an empty bundleVersion (never "").</summary>
        internal static string BuildVersion()
        {
            var version = Application.version;
            return string.IsNullOrEmpty(version) ? FALLBACK_BUILD_VERSION : version;
        }

        internal static string Os()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.WindowsEditor:
                    return "windows";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.OSXEditor:
                    return "macos";
                case RuntimePlatform.LinuxPlayer:
                case RuntimePlatform.LinuxEditor:
                    return "linux";
                default:
                    return "other";
            }
        }

        internal static string Arch()
        {
            switch (RuntimeInformation.ProcessArchitecture)
            {
                case Architecture.X64:
                    return "x64";
                case Architecture.Arm64:
                    return "arm64";
                case Architecture.X86:
                    return "x86";
                default:
                    return "other";
            }
        }
    }
}
