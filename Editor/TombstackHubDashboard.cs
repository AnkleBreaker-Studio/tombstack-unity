using System;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Dashboard tab sub-controller for <see cref="TombstackHubWindow"/> (IDisposable,
    /// uitoolkit-base pattern). Renders the live game summary — crash-free %, 24h/7d counts,
    /// spike banner, top-10 status-colored signature rows (click → web detail page), and the
    /// 30-day trend as plain VisualElement bars. Auto-refresh runs off
    /// <c>EditorApplication.update</c> with a timestamp comparison only — zero allocations
    /// per editor frame; all fetching is async and fail-soft.
    /// </summary>
    public sealed class TombstackHubDashboard : IDisposable
    {
        private const int TREND_DAYS = 30;
        private const double AUTO_REFRESH_SECONDS = 60.0;
        private const float TREND_MAX_BAR_HEIGHT = 60f;
        private const float TREND_MIN_BAR_HEIGHT = 2f;

        private readonly Label _gameNameLabel;
        private readonly Label _statusLabel;
        private readonly Label _spikeBanner;
        private readonly Label _crashFreeLabel;
        private readonly Label _crashes24hLabel;
        private readonly Label _crashes7dLabel;
        private readonly VisualElement _trendContainer;
        private readonly VisualElement _signaturesContainer;
        private readonly Label _signaturesEmptyLabel;
        private readonly Button _refreshButton;
        private readonly Toggle _autoRefreshToggle;

        private bool _active;
        private bool _fetching;
        private bool _updateHooked;
        private double _nextRefreshAt;

        /// <summary>Query the dashboard elements out of the already-built hub tree.</summary>
        /// <param name="root">The hub window root containing the <c>dash-*</c> elements.</param>
        public TombstackHubDashboard(VisualElement root)
        {
            _gameNameLabel = root.Q<Label>("dash-game-name");
            _statusLabel = root.Q<Label>("dash-status");
            _spikeBanner = root.Q<Label>("dash-spike");
            _crashFreeLabel = root.Q<Label>("dash-crashfree");
            _crashes24hLabel = root.Q<Label>("dash-24h");
            _crashes7dLabel = root.Q<Label>("dash-7d");
            _trendContainer = root.Q("dash-trend");
            _signaturesContainer = root.Q("dash-signatures");
            _signaturesEmptyLabel = root.Q<Label>("dash-signatures-empty");
            _refreshButton = root.Q<Button>("dash-refresh");
            _autoRefreshToggle = root.Q<Toggle>("dash-autorefresh");

            _refreshButton?.RegisterCallback<ClickEvent>(onRefreshClicked);
            _autoRefreshToggle?.RegisterValueChangedCallback(onAutoRefreshChanged);
            SyncLinkState();
        }

        /// <summary>Unregister callbacks and stop the auto-refresh pump.</summary>
        public void Dispose()
        {
            _refreshButton?.UnregisterCallback<ClickEvent>(onRefreshClicked);
            _autoRefreshToggle?.UnregisterValueChangedCallback(onAutoRefreshChanged);
            unhookUpdate();
        }

        /// <summary>Called when the dashboard tab is shown/hidden — gates fetch + auto-refresh.</summary>
        /// <param name="active">True when the dashboard tab is visible.</param>
        public void SetActive(bool active)
        {
            _active = active;
            if (active)
            {
                refresh();
                if (_autoRefreshToggle != null && _autoRefreshToggle.value) hookUpdate();
            }
            else
            {
                unhookUpdate();
            }
        }

        /// <summary>Re-read the linked game (called by the hub on session/link changes).</summary>
        public void SyncLinkState()
        {
            var settings = TombstackProjectSettingsSO.instance;
            if (_gameNameLabel != null)
                _gameNameLabel.text = settings.IsLinked ? settings.GameName.ToUpperInvariant() : "NO LINKED GAME";
            _refreshButton?.SetEnabled(settings.IsLinked);
            if (!settings.IsLinked) showStatus("Link this project on the Connection tab to see live data.");
        }

        private void onRefreshClicked(ClickEvent evt) => refresh();

        private void onAutoRefreshChanged(ChangeEvent<bool> evt)
        {
            if (evt.newValue && _active) hookUpdate();
            else unhookUpdate();
        }

        private void hookUpdate()
        {
            if (_updateHooked) return;
            _nextRefreshAt = EditorApplication.timeSinceStartup + AUTO_REFRESH_SECONDS;
            EditorApplication.update += onEditorUpdate;
            _updateHooked = true;
        }

        private void unhookUpdate()
        {
            if (!_updateHooked) return;
            EditorApplication.update -= onEditorUpdate;
            _updateHooked = false;
        }

        // Hot path: a double comparison per editor frame, nothing else — no allocations.
        private void onEditorUpdate()
        {
            if (EditorApplication.timeSinceStartup < _nextRefreshAt) return;
            _nextRefreshAt = EditorApplication.timeSinceStartup + AUTO_REFRESH_SECONDS;
            refresh();
        }

        private async void refresh()
        {
            if (_fetching) return;
            var settings = TombstackProjectSettingsSO.instance;
            if (!settings.IsLinked || !TombstackSession.IsSignedIn) return;

            _fetching = true;
            showStatus("FETCHING…");
            try
            {
                var result = await TombstackEditorApi.GetSummaryAsync(
                    TombstackSession.EditorToken, settings.GameId, TREND_DAYS);
                if (!result.Ok)
                {
                    if (result.Status == 401) TombstackSession.HandleUnauthorized();
                    showStatus(result.Error);
                    return;
                }
                showStatus(null);
                render(result.Data);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Tombstack] dashboard refresh failed: {e.Message}");
                showStatus("Refresh failed — see the Console.");
            }
            finally
            {
                _fetching = false;
            }
        }

        private void render(EditorGameSummaryData summary)
        {
            if (_crashFreeLabel != null)
            {
                _crashFreeLabel.text = summary.crashFreePct.ToString("0.0") + "%";
                bool good = summary.crashFreePct >= 99f;
                _crashFreeLabel.EnableInClassList("tmb-stat__value--good", good);
                _crashFreeLabel.EnableInClassList("tmb-stat__value--bad", !good);
            }
            if (_crashes24hLabel != null) _crashes24hLabel.text = summary.totalCrashes24h.ToString();
            if (_crashes7dLabel != null) _crashes7dLabel.text = summary.totalCrashes7d.ToString();
            _spikeBanner?.EnableInClassList("hidden", !summary.crashSpike);
            renderTrend(summary.dailyTrend);
            renderSignatures(summary.topSignatures);
        }

        private void renderTrend(EditorTrendPoint[] trend)
        {
            if (_trendContainer == null) return;
            _trendContainer.Clear();
            if (trend == null || trend.Length == 0) return;

            long max = 0;
            int peakIndex = 0;
            for (int i = 0; i < trend.Length; i++)
            {
                if (trend[i].count > max)
                {
                    max = trend[i].count;
                    peakIndex = i;
                }
            }

            for (int i = 0; i < trend.Length; i++)
            {
                var bar = new VisualElement();
                bar.AddToClassList("tmb-trend__bar");
                if (i == peakIndex && max > 0) bar.AddToClassList("tmb-trend__bar--peak");
                float ratio = max > 0 ? (float)trend[i].count / max : 0f;
                bar.style.height = Mathf.Max(TREND_MIN_BAR_HEIGHT, ratio * TREND_MAX_BAR_HEIGHT);
                bar.tooltip = $"{trend[i].dateIso}: {trend[i].count} crashes";
                _trendContainer.Add(bar);
            }
        }

        private void renderSignatures(EditorSignatureSummary[] signatures)
        {
            if (_signaturesContainer == null) return;
            _signaturesContainer.Clear();
            bool empty = signatures == null || signatures.Length == 0;
            _signaturesEmptyLabel?.EnableInClassList("hidden", !empty);
            if (empty) return;

            foreach (var sig in signatures)
            {
                var row = new VisualElement { userData = sig.signature };
                row.AddToClassList("tmb-sig-row");

                var status = new VisualElement();
                status.AddToClassList("tmb-sig-row__status");
                if (!string.IsNullOrEmpty(sig.status))
                    status.AddToClassList("tmb-sig-row__status--" + sig.status.ToLowerInvariant());
                row.Add(status);

                var hint = new Label(string.IsNullOrEmpty(sig.stackHint) ? sig.signature : sig.stackHint);
                hint.AddToClassList("tmb-sig-row__hint");
                row.Add(hint);

                var count = new Label("×" + sig.count);
                count.AddToClassList("tmb-sig-row__count");
                row.Add(count);

                var users = new Label(sig.affectedUsers + " users");
                users.AddToClassList("tmb-sig-row__users");
                row.Add(users);

                row.tooltip = "Open in the web dashboard";
                row.RegisterCallback<ClickEvent>(onSignatureRowClicked);
                _signaturesContainer.Add(row);
            }
        }

        // One shared named callback for every row; the signature rides in userData
        // (rows are cleared+rebuilt each refresh, so no per-row unregister bookkeeping).
        private void onSignatureRowClicked(ClickEvent evt)
        {
            var row = evt.currentTarget as VisualElement;
            var signature = row?.userData as string;
            if (string.IsNullOrEmpty(signature)) return;
            var settings = TombstackProjectSettingsSO.instance;
            Application.OpenURL(
                settings.ResolveEndpoint() + "/games/" + settings.GameId + "/signatures/" + signature);
        }

        private void showStatus(string message)
        {
            if (_statusLabel == null) return;
            bool has = !string.IsNullOrEmpty(message);
            _statusLabel.text = has ? message : string.Empty;
            _statusLabel.EnableInClassList("hidden", !has);
        }
    }
}
