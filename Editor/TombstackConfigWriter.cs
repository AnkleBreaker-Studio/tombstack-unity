using UnityEditor;
using UnityEngine;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Bridges the editor plugin to the runtime config the SDK boots from. The runtime loads
    /// <c>Resources/TombstackConfig</c> (<see cref="TombstackConfigSO"/>) at startup; this
    /// writer finds (or creates at <c>Assets/Tombstack/Resources/TombstackConfig.asset</c>)
    /// that asset and writes the minted SDK token + endpoint through SerializedObject —
    /// the runtime class stays untouched. The tmb_ token is game-facing by design and is the
    /// ONLY credential that ever lands inside the project.
    /// </summary>
    public static class TombstackConfigWriter
    {
        private const string CONFIG_FOLDER = "Assets/Tombstack/Resources";
        private const string CONFIG_ASSET_PATH = CONFIG_FOLDER + "/TombstackConfig.asset";
        private const string PROP_ENDPOINT = "_endpoint";
        private const string PROP_GAME_TOKEN = "_gameToken";
        private const string PROP_HEARTBEAT = "_heartbeatIntervalSeconds";
        private const string PROP_REQUIRE_CONSENT = "_requireConsent";
        private const string TOKEN_PREFIX = "tmb_";
        private const string TOKEN_PLACEHOLDER = "tmb_REPLACE_ME";

        /// <summary>Find the project's config asset anywhere under Assets (null when absent).</summary>
        public static TombstackConfigSO FindConfig()
        {
            var guids = AssetDatabase.FindAssets("t:TombstackConfigSO");
            if (guids == null || guids.Length == 0) return null;
            var path = AssetDatabase.GUIDToAssetPath(guids[0]);
            return AssetDatabase.LoadAssetAtPath<TombstackConfigSO>(path);
        }

        /// <summary>Find the config asset, creating it under a Resources folder when missing.</summary>
        public static TombstackConfigSO FindOrCreateConfig()
        {
            var existing = FindConfig();
            if (existing != null) return existing;

            if (!AssetDatabase.IsValidFolder("Assets/Tombstack"))
                AssetDatabase.CreateFolder("Assets", "Tombstack");
            if (!AssetDatabase.IsValidFolder(CONFIG_FOLDER))
                AssetDatabase.CreateFolder("Assets/Tombstack", "Resources");

            var config = ScriptableObject.CreateInstance<TombstackConfigSO>();
            AssetDatabase.CreateAsset(config, CONFIG_ASSET_PATH);
            AssetDatabase.SaveAssets();
            return config;
        }

        /// <summary>True when the config holds a real-looking SDK token (not the placeholder).</summary>
        public static bool HasSdkToken()
        {
            var config = FindConfig();
            if (config == null) return false;
            var token = config.GameToken;
            return !string.IsNullOrEmpty(token)
                && token != TOKEN_PLACEHOLDER
                && token.StartsWith(TOKEN_PREFIX, System.StringComparison.Ordinal);
        }

        /// <summary>Write endpoint + minted SDK token into the config asset (created if missing).</summary>
        /// <param name="endpoint">Ingestion base URL the shipped game should post to.</param>
        /// <param name="sdkToken">Per-game tmb_ ingest token.</param>
        /// <returns>True on success; false (logged) when the asset could not be written.</returns>
        public static bool WriteLink(string endpoint, string sdkToken)
        {
            try
            {
                var config = FindOrCreateConfig();
                if (config == null) return false;
                var so = new SerializedObject(config);
                so.FindProperty(PROP_ENDPOINT).stringValue = endpoint;
                so.FindProperty(PROP_GAME_TOKEN).stringValue = sdkToken;
                so.ApplyModifiedPropertiesWithoutUndo();
                saveAsset(config);
                return true;
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] Could not write config asset: {e.Message}");
                return false;
            }
        }

        /// <summary>Update the heartbeat interval on the config asset (no-op when absent).</summary>
        /// <param name="seconds">Seconds between heartbeats (the runtime clamps 15–600).</param>
        public static void WriteHeartbeatInterval(float seconds)
        {
            writeProperty(so => so.FindProperty(PROP_HEARTBEAT).floatValue = seconds);
        }

        /// <summary>Update the consent-required default on the config asset (no-op when absent).</summary>
        /// <param name="requireConsent">True to stay silent until <c>Tombstack.SetConsent(true)</c>.</param>
        public static void WriteRequireConsent(bool requireConsent)
        {
            writeProperty(so => so.FindProperty(PROP_REQUIRE_CONSENT).boolValue = requireConsent);
        }

        /// <summary>Clear the SDK token from the config asset (unlink), keeping the asset.</summary>
        public static void ClearSdkToken()
        {
            writeProperty(so => so.FindProperty(PROP_GAME_TOKEN).stringValue = TOKEN_PLACEHOLDER);
        }

        private static void writeProperty(System.Action<SerializedObject> mutate)
        {
            try
            {
                var config = FindConfig();
                if (config == null) return;
                var so = new SerializedObject(config);
                mutate(so);
                so.ApplyModifiedPropertiesWithoutUndo();
                saveAsset(config);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] Could not update config asset: {e.Message}");
            }
        }

        private static void saveAsset(TombstackConfigSO config)
        {
            EditorUtility.SetDirty(config);
            AssetDatabase.SaveAssetIfDirty(config);
        }
    }
}
