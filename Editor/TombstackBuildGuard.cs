using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Build check on the TombstackConfig assets a build can load. Everything in them ships inside the
    /// build, readable by anyone who has it, and only the ingest-only per-game SDK token is made for
    /// that: the build fails when one holds a studio key, an editor sign-in token or an MCP key. A config
    /// that still holds the placeholder token only warns: that build sends no telemetry, which can be on
    /// purpose. A write-scoped per-game token (for server log pulls) cannot be told apart from an SDK
    /// token here; keep it out of client builds.
    /// </summary>
    public sealed class TombstackBuildGuard : IPreprocessBuildWithReport
    {
        /// <summary>Unity's ordering hook (interface member, Unity's own name); nothing depends on it.</summary>
        public int callbackOrder => 0;

        /// <summary>Unity build callback: fail on a non-SDK credential, warn on the placeholder.</summary>
        public void OnPreprocessBuild(BuildReport report)
        {
            foreach (var config in TombstackConfigWriter.FindShippedConfigs())
            {
                var path = AssetDatabase.GetAssetPath(config);
                var token = config.GameToken;
                var wrongKind = TombstackConfigSO.DescribeNonSdkCredential(token);
                if (wrongKind != null)
                {
                    throw new BuildFailedException(
                        "[Tombstack] " + path + " holds " + wrongKind + " instead of the game's SDK token (tmb_...), "
                        + "and it would ship inside this build. Link the project again from Window > Tombstack > Hub, "
                        + "or paste the game's SDK token into the asset.");
                }
                if (config.AutoInitOnLoad && token != null && token.Trim() == TombstackConfigSO.TOKEN_PLACEHOLDER)
                    Debug.LogWarning("[Tombstack] " + path + " still holds the placeholder token: this build sends no crash reports or telemetry.");
            }
        }
    }
}
