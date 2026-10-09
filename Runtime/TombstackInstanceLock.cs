using System;
using System.Globalization;
using System.IO;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Gives each running process a state folder no other live process uses (session marker, offline
    /// queue, session logs). Every process of a game used to share <c>persistentDataPath/Tombstack</c>:
    /// several dedicated servers on one host, or a player next to the Editor, read each other's
    /// <c>session.lock</c> (a false unclean shutdown for a process still running), deleted each other's
    /// marker on quit (hiding a real crash), and sent each other's offline payloads.
    ///
    /// The first process to take <c>Tombstack/instance.lock</c> keeps <c>Tombstack/</c> itself, the
    /// layout every earlier SDK used, so a single process still finds what its previous run left. The
    /// next ones take <c>Tombstack/instances/1</c>, <c>/2</c>, and so on. Slots are reused by later
    /// processes, so a crashed server's marker and queue are picked up by the next process that
    /// takes its slot. The lock is held for the life of the process: an exclusive share mode on
    /// Windows plus a byte-range lock (fcntl on Linux, macOS and Android, where share modes only
    /// apply inside one process). The OS releases both when the process dies, crash included.
    ///
    /// <c>identity.json</c> stays in <c>Tombstack/</c>: it identifies the device, not the process.
    /// Never throws: on any failure the process falls back to a folder of its own.
    /// </summary>
    internal static class TombstackInstanceLock
    {
        private const string ROOT_DIR_NAME = "Tombstack";
        private const string INSTANCES_DIR_NAME = "instances";
        private const string LOCK_FILE_NAME = "instance.lock";
        private const int MAX_SLOTS = 16;

        private static readonly object _gate = new object();
        private static FileStream _lockStream;
        private static string _stateDir;

        /// <summary>The state folder this process owns: the same folder for every call in one process.
        /// Call on the main thread at Init (<c>Application.persistentDataPath</c> is read there).</summary>
        internal static string Acquire(string persistentDataPath)
        {
            lock (_gate)
            {
                if (_stateDir != null) return _stateDir;
                var root = Path.Combine(persistentDataPath, ROOT_DIR_NAME);
#if UNITY_WEBGL && !UNITY_EDITOR
                // One process per tab, and no lock a sibling tab could see: the previous layout.
                _stateDir = root;
                return root;
#else
                for (int slot = 0; slot < MAX_SLOTS; slot++)
                {
                    var dir = slot == 0
                        ? root
                        : Path.Combine(root, INSTANCES_DIR_NAME, slot.ToString(CultureInfo.InvariantCulture));
                    var outcome = tryLock(dir);
                    if (outcome == LockOutcome.Held) continue;
                    // Acquired, or this platform/folder cannot lock at all: in the latter case a new
                    // folder per launch would lose the previous run's marker and queue, so it keeps the
                    // layout every earlier SDK used.
                    _stateDir = outcome == LockOutcome.Acquired ? dir : root;
                    return _stateDir;
                }
                // Every slot is held by a live process. A folder per process id is never shared; it is
                // simply not picked up by a later run, which only matters past MAX_SLOTS processes.
                int pid = TombstackExitInfo.CurrentPid();
                var own = pid > 0 ? "pid-" + pid.ToString(CultureInfo.InvariantCulture) : Guid.NewGuid().ToString("N");
                _stateDir = Path.Combine(root, INSTANCES_DIR_NAME, own);
                TombstackLog.Warn($"{MAX_SLOTS} Tombstack processes already run on this machine; this one keeps its state in {_stateDir}");
                return _stateDir;
#endif
            }
        }

        private enum LockOutcome { Acquired, Held, Unavailable }

        /// <summary>Take the slot lock in <paramref name="dir"/>: Held when another live process has it,
        /// Unavailable when this platform or folder cannot take a lock at all.</summary>
        private static LockOutcome tryLock(string dir)
        {
            FileStream stream = null;
            try
            {
                Directory.CreateDirectory(dir);
                stream = new FileStream(
                    Path.Combine(dir, LOCK_FILE_NAME), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                try
                {
                    stream.Lock(0, 1);
                }
                catch (NotSupportedException)
                {
                    // No byte-range locks on this platform (WebGL): the share mode is the only guard.
                }
                _lockStream = stream;
                AppDomain.CurrentDomain.DomainUnload += onDomainUnload;
                return LockOutcome.Acquired;
            }
            catch (Exception e)
            {
                if (stream != null)
                {
                    try { stream.Dispose(); }
                    catch { /* best-effort */ }
                }
                // A sharing or lock violation is an IOException; a missing directory or file is a
                // subclass of it too, but CreateDirectory above rules those out.
                return e is IOException ? LockOutcome.Held : LockOutcome.Unavailable;
            }
        }

        /// <summary>Release the lock when the Editor reloads scripts, so the next domain of the same
        /// Editor process takes the same slot instead of finding its own leftover handle.</summary>
        private static void onDomainUnload(object sender, EventArgs e)
        {
            try
            {
                lock (_gate)
                {
                    _lockStream?.Dispose();
                    _lockStream = null;
                }
            }
            catch { /* unload path must never throw */ }
        }
    }
}
