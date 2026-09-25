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
        // Every ingest schema caps buildVersion at 64 chars (src/lib/crash-schema.ts and siblings). A
        // longer bundleVersion (CI stamps like "1.4.2+build.8812.sha.<40 hex>") 400'd EVERY crash,
        // event and heartbeat of that build as poison. Clamped here like every other field.
        private const int MAX_BUILD_VERSION = 64;

        /// <summary>Project build version, guarded against an empty bundleVersion (never "") and
        /// clamped to the server's 64-char limit.</summary>
        internal static string BuildVersion()
        {
            var version = Application.version;
            if (string.IsNullOrEmpty(version)) return FALLBACK_BUILD_VERSION;
            return version.Length <= MAX_BUILD_VERSION ? version : version.Substring(0, MAX_BUILD_VERSION);
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
                case RuntimePlatform.Android:
                    return "android";
                case RuntimePlatform.IPhonePlayer:
                    return "ios";
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
