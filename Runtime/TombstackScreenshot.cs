using System;
using System.Collections;
using System.Security.Cryptography;
using UnityEngine;

namespace AnkleBreaker.Tombstack
{
    /// <summary>
    /// Captures a downscaled PNG of the current frame for attachment to a bug report or an
    /// exception crash. Designed to be "invisible": the grab happens at end-of-frame (so the
    /// fully-composited image is read, nothing half-drawn), the result is downscaled to a bounded
    /// max dimension to keep it small, and consecutive captures are throttled so an exception
    /// storm can never spam GPU readbacks. Fail-silent — any failure yields null and the report
    /// simply ships without a screenshot.
    ///
    /// PERF: ScreenCapture.CaptureScreenshotAsTexture does one synchronous GPU->CPU readback (a
    /// single-frame cost on the capturing frame only). For a user-initiated bug report that is
    /// imperceptible; for exceptions it is gated behind opt-in + the throttle window. (A fully
    /// async AsyncGPUReadback path is a future optimization — kept simple here for reliability.)
    ///
    /// EDITOR-VERIFY: this file uses Unity rendering APIs and must be compiled + playmode-tested in
    /// the Unity Editor before the SDK tarball is republished.
    /// </summary>
    internal static class TombstackScreenshot
    {
        /// <summary>Captured image bytes + their content metadata for the ingest payload.</summary>
        internal struct Shot
        {
            public byte[] Bytes;   // PNG-encoded image (content-type image/png on the presigned PUT)
            public int Size;       // Bytes.Length, echoed in the ingest "screenshot.size"
            public string Sha256;  // lowercase hex digest, echoed in "screenshot.sha256"
        }

        // Throttle: at most one capture per window (guards exception storms). Monotonic seconds.
        private static double _lastCaptureAt = -1000.0;

        // Managed id of the Unity main thread, stamped at Bootstrap. ScreenCapture is main-thread-only,
        // so the synchronous exception path must verify it is on this thread before grabbing.
        internal static int MainThreadId = -1;

        private static double Now =>
            System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

        /// <summary>True when a capture is allowed now (outside the throttle window).</summary>
        internal static bool ThrottleAllows(double minIntervalSeconds) =>
            Now - _lastCaptureAt >= minIntervalSeconds;

        /// <summary>True only on the Unity main thread (where rendering APIs are legal to call).</summary>
        internal static bool OnMainThread =>
            MainThreadId != -1 && System.Threading.Thread.CurrentThread.ManagedThreadId == MainThreadId;

        /// <summary>
        /// Synchronous best-effort grab for the exception path (a crash may be fatal, so we must not
        /// wait for end-of-frame nor delay the durable crash enqueue). Returns null off the main
        /// thread or on any failure — the crash then ships with no screenshot. Never throws/blocks.
        /// </summary>
        internal static Shot? CaptureSync(int maxDimension)
        {
            if (!OnMainThread) return null;
            _lastCaptureAt = Now;
            return GrabAndEncode(maxDimension);
        }

        /// <summary>
        /// Coroutine: grab the frame at end-of-frame, downscale to <paramref name="maxDimension"/>,
        /// PNG-encode, and hand the result to <paramref name="onComplete"/> (null on any failure).
        /// Must run on a MonoBehaviour (the hidden [Tombstack] host).
        /// </summary>
        internal static IEnumerator Capture(int maxDimension, Action<Shot?> onComplete)
        {
            // Wait for the frame to finish so we read the fully-composited image, never a partial one.
            yield return new WaitForEndOfFrame();
            _lastCaptureAt = Now;
            onComplete?.Invoke(GrabAndEncode(maxDimension));
        }

        /// <summary>Grab the current backbuffer, downscale, PNG-encode. Caller guarantees the main
        /// thread + (for the coroutine) end-of-frame. Fail-silent → null. Always frees textures.</summary>
        private static Shot? GrabAndEncode(int maxDimension)
        {
            Texture2D full = null;
            Texture2D scaled = null;
            try
            {
                full = ScreenCapture.CaptureScreenshotAsTexture();
                if (full == null) return null;
                var src = full;
                if (maxDimension > 0 && (full.width > maxDimension || full.height > maxDimension))
                {
                    scaled = Downscale(full, maxDimension);
                    if (scaled != null) src = scaled;
                }
                byte[] png = src.EncodeToPNG();
                if (png != null && png.Length > 0)
                    return new Shot { Bytes = png, Size = png.Length, Sha256 = Sha256Hex(png) };
                return null;
            }
            catch (Exception e)
            {
                TombstackLog.Warn("screenshot capture failed: " + e.Message);
                return null;
            }
            finally
            {
                if (full != null) UnityEngine.Object.Destroy(full);
                if (scaled != null) UnityEngine.Object.Destroy(scaled);
            }
        }

        /// <summary>GPU-blit downscale that preserves aspect ratio, longest side = maxDimension.</summary>
        private static Texture2D Downscale(Texture2D source, int maxDimension)
        {
            try
            {
                float scale = maxDimension / (float)Mathf.Max(source.width, source.height);
                int w = Mathf.Max(1, Mathf.RoundToInt(source.width * scale));
                int h = Mathf.Max(1, Mathf.RoundToInt(source.height * scale));
                var rt = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32);
                var prev = RenderTexture.active;
                try
                {
                    Graphics.Blit(source, rt);
                    RenderTexture.active = rt;
                    var dst = new Texture2D(w, h, TextureFormat.RGB24, false);
                    dst.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    dst.Apply();
                    return dst;
                }
                finally
                {
                    RenderTexture.active = prev;
                    RenderTexture.ReleaseTemporary(rt);
                }
            }
            catch (Exception e)
            {
                TombstackLog.Warn("screenshot downscale failed: " + e.Message);
                return null;
            }
        }

        private static string Sha256Hex(byte[] bytes)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                var sb = new System.Text.StringBuilder(hash.Length * 2);
                foreach (byte b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
