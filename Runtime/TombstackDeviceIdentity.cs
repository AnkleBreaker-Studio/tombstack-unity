using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>JsonUtility wrapper for <c>Tombstack/identity.json</c> (the persisted device id).</summary>
    [Serializable]
    internal sealed class DeviceIdentityData
    {
        /// <summary>The persisted device-derived provisional id ("dev_" + 16 lowercase hex chars).</summary>
        public string deviceId;
    }

    /// <summary>
    /// v0.16 device identity: mints (or restores) the persistent device-derived provisional user id
    /// so the SDK never sends an anonymous player. The id is <c>"dev_" + first 16 hex chars of
    /// SHA-256(salt + "|" + SystemInfo.deviceUniqueIdentifier)</c> — the salt is the game's ingest
    /// token, so the SAME physical device yields DIFFERENT ids for different games (cross-tenant
    /// unlinkability), and the raw device identifier never goes on the wire. The id is persisted to
    /// <c>persistentDataPath/Tombstack/identity.json</c>; on later launches the FILE WINS over a
    /// re-mint, so the id stays stable even across an ingest-token rotation. MUST be called on the
    /// main thread (SystemInfo / Application.persistentDataPath are main-thread-only). Never throws
    /// into Init — every failure degrades to a freshly minted id (worst case: a new id per launch
    /// on a device with no writable storage).
    /// </summary>
    internal static class TombstackDeviceIdentity
    {
        private const string DIR_NAME = "Tombstack";
        private const string FILE_NAME = "identity.json";
        private const string ID_PREFIX = "dev_";
        private const int HASH_HEX_CHARS = 16;
        // Plausibility bound for a persisted id: "dev_" + 16 hex is 20 chars; anything beyond 40
        // is a corrupt/tampered file and falls through to a fresh mint.
        private const int MAX_PERSISTED_ID_LENGTH = 40;

        /// <summary>Restore the persisted device id, or mint + persist a fresh one. Main thread only.</summary>
        /// <param name="salt">The game's ingest token — per-game salt for cross-tenant unlinkability.</param>
        internal static string Acquire(string salt)
        {
            var filePath = identityFilePath();
            var persisted = readPersisted(filePath);
            if (persisted != null) return persisted; // the file wins: stable across token rotation
            var minted = mint(salt);
            persistBestEffort(filePath, minted);
            return minted; // a failed persist still returns the minted id (non-sticky, but valid)
        }

        /// <summary>Resolve <c>persistentDataPath/Tombstack/identity.json</c>, or null when unavailable.</summary>
        private static string identityFilePath()
        {
            try
            {
                var root = Application.persistentDataPath;
                if (string.IsNullOrEmpty(root)) return null;
                return Path.Combine(root, DIR_NAME, FILE_NAME);
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"device identity path unavailable: {e.Message}");
                return null;
            }
        }

        /// <summary>Read-then-act (never mutate state before the I/O succeeds): returns the persisted
        /// id when the file holds a plausible one, else null so the caller mints fresh.</summary>
        private static string readPersisted(string filePath)
        {
            try
            {
                if (filePath == null || !File.Exists(filePath)) return null;
                var data = JsonUtility.FromJson<DeviceIdentityData>(File.ReadAllText(filePath));
                var id = data != null ? data.deviceId : null;
                return isPlausible(id) ? id : null;
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"device identity read failed: {e.Message}");
                return null;
            }
        }

        /// <summary>Sanity gate on a persisted id (non-empty, "dev_"-prefixed, bounded) — a corrupt
        /// or hand-edited file re-mints instead of putting junk on the wire.</summary>
        private static bool isPlausible(string id)
        {
            return !string.IsNullOrEmpty(id)
                   && id.StartsWith(ID_PREFIX, StringComparison.Ordinal)
                   && id.Length <= MAX_PERSISTED_ID_LENGTH;
        }

        /// <summary>Mint <c>"dev_" + first 16 hex of SHA-256(salt + "|" + device identifier)</c>.
        /// A missing/unsupported device identifier falls back to a random GUID source (the id is
        /// then random-but-persisted rather than device-derived).</summary>
        private static string mint(string salt)
        {
            string source;
            try
            {
                source = SystemInfo.deviceUniqueIdentifier;
                if (string.IsNullOrEmpty(source) || source == SystemInfo.unsupportedIdentifier)
                    source = Guid.NewGuid().ToString("N");
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"device identifier unavailable: {e.Message}");
                source = Guid.NewGuid().ToString("N");
            }
            try
            {
                using (var sha = SHA256.Create())
                {
                    var hash = sha.ComputeHash(Encoding.UTF8.GetBytes((salt ?? string.Empty) + "|" + source));
                    var id = new StringBuilder(ID_PREFIX.Length + HASH_HEX_CHARS);
                    id.Append(ID_PREFIX);
                    for (int i = 0; i < hash.Length && id.Length < ID_PREFIX.Length + HASH_HEX_CHARS; i++)
                        id.Append(hash[i].ToString("x2")); // lowercase hex
                    return id.ToString();
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"device identity hash failed: {e.Message}");
                // Last-resort fallback: still a well-formed (if non-derived) id — never null.
                return ID_PREFIX + Guid.NewGuid().ToString("N").Substring(0, HASH_HEX_CHARS);
            }
        }

        /// <summary>Persist the minted id best-effort — a write failure is logged and swallowed;
        /// the caller still uses the minted id for this session.</summary>
        private static void persistBestEffort(string filePath, string id)
        {
            try
            {
                if (filePath == null || string.IsNullOrEmpty(id)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(filePath));
                File.WriteAllText(filePath, JsonUtility.ToJson(new DeviceIdentityData { deviceId = id }));
            }
            catch (Exception e)
            {
                TombstackLog.Warn($"device identity persist failed: {e.Message}");
            }
        }
    }
}
