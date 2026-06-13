using System;
using System.Collections.Concurrent;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Live Tail (<c>Window ▸ Tombstack ▸ Live Tail</c>): in Play mode, subscribes to the runtime's
    /// internal <c>Tombstack.OnTelemetry</c> event (raised fail-silently on each captured crumb /
    /// event / metric / crash) and shows a scrolling list of recent telemetry. Editor-only — this
    /// type lives in the editor assembly and never ships in a player build.
    ///
    /// The runtime raises telemetry from any thread (capture can run off the main thread), so incoming
    /// lines are buffered in a thread-safe queue and drained onto the UI on <c>EditorApplication.update</c>
    /// (the main thread — UI Toolkit is not thread-safe). The visible list is bounded so a long session
    /// can't grow it without limit. Forge-dark USS, consistent with the other Tombstack editor windows.
    /// </summary>
    public sealed class TombstackLiveTailWindow : EditorWindow
    {
        private const float MIN_WIDTH = 460f;
        private const float MIN_HEIGHT = 320f;
        private const int MAX_ROWS = 300;

        // Thread-safe inbox: OnTelemetry may fire off the main thread; the UI drains it on update.
        private readonly ConcurrentQueue<string> _inbox = new ConcurrentQueue<string>();
        private ScrollView _list;
        private Label _statusLabel;
        private bool _subscribed;
        private bool _paused;

        /// <summary>Open (or focus) the Live Tail window.</summary>
        [MenuItem("Window/Tombstack/Live Tail", priority = 20)]
        public static void Open()
        {
            var window = GetWindow<TombstackLiveTailWindow>(utility: false, title: "Tombstack Live Tail");
            window.minSize = new Vector2(MIN_WIDTH, MIN_HEIGHT);
        }

        /// <summary>UI Toolkit entry point — builds the tree in code (no UXML) and styles it with the theme.</summary>
        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList(TombstackEditorUi.ROOT_CLASS);
            var theme = AssetDatabase.LoadAssetAtPath<StyleSheet>(
                TombstackEditorUi.UI_ROOT + "TombstackTheme.uss");
            if (theme != null) root.styleSheets.Add(theme);

            var page = new VisualElement();
            page.AddToClassList("tmb-page");
            root.Add(page);

            var header = new VisualElement();
            header.AddToClassList("tmb-row");
            var title = new Label("LIVE TAIL");
            title.AddToClassList("tmb-head");
            header.Add(title);
            var spacer = new VisualElement();
            spacer.AddToClassList("tmb-spacer");
            header.Add(spacer);

            var pauseButton = new Button(togglePause) { text = "PAUSE" };
            pauseButton.AddToClassList("tmb-btn");
            pauseButton.name = "tmb-livetail-pause";
            header.Add(pauseButton);

            var clearButton = new Button(clearRows) { text = "CLEAR" };
            clearButton.AddToClassList("tmb-btn");
            header.Add(clearButton);
            page.Add(header);

            _statusLabel = new Label();
            _statusLabel.AddToClassList("tmb-dim");
            page.Add(_statusLabel);

            _list = new ScrollView(ScrollViewMode.Vertical);
            _list.AddToClassList("tmb-livetail");
            page.Add(_list);

            updateStatus();
        }

        private void OnEnable()
        {
            subscribe();
            EditorApplication.update += onEditorUpdate;
            EditorApplication.playModeStateChanged += onPlayModeChanged;
        }

        private void OnDisable()
        {
            unsubscribe();
            EditorApplication.update -= onEditorUpdate;
            EditorApplication.playModeStateChanged -= onPlayModeChanged;
        }

        private void subscribe()
        {
            if (_subscribed) return;
            Tombstack.OnTelemetry += onTelemetry;
            _subscribed = true;
        }

        private void unsubscribe()
        {
            if (!_subscribed) return;
            Tombstack.OnTelemetry -= onTelemetry;
            _subscribed = false;
        }

        // May be invoked off the main thread — only touch the thread-safe queue here.
        private void onTelemetry(string line)
        {
            if (_paused || string.IsNullOrEmpty(line)) return;
            _inbox.Enqueue(line);
        }

        private void onPlayModeChanged(PlayModeStateChange change) => updateStatus();

        private void onEditorUpdate()
        {
            if (_list == null) return;
            bool added = false;
            // Drain the inbox onto the UI on the main thread (bounded per-tick by the queue contents).
            while (_inbox.TryDequeue(out var line))
            {
                appendRow(line);
                added = true;
            }
            if (added) trimRows();
        }

        private void appendRow(string line)
        {
            var row = new Label($"{DateTime.Now:HH:mm:ss}  {line}");
            row.AddToClassList("tmb-livetail__row");
            _list.Add(row);
            // Keep the newest entry in view.
            _list.scrollOffset = new Vector2(0, float.MaxValue);
        }

        private void trimRows()
        {
            var content = _list.contentContainer;
            while (content.childCount > MAX_ROWS) content.RemoveAt(0);
        }

        private void togglePause()
        {
            _paused = !_paused;
            var button = rootVisualElement.Q<Button>("tmb-livetail-pause");
            if (button != null) button.text = _paused ? "RESUME" : "PAUSE";
            updateStatus();
        }

        private void clearRows()
        {
            _list?.Clear();
            while (_inbox.TryDequeue(out _)) { }
        }

        private void updateStatus()
        {
            if (_statusLabel == null) return;
            if (!EditorApplication.isPlaying)
                _statusLabel.text = "Enter Play mode to see live crumbs, events, metrics, and crashes.";
            else if (_paused)
                _statusLabel.text = "Paused — new telemetry is dropped until you resume.";
            else
                _statusLabel.text = "Listening for runtime telemetry…";
        }
    }
}
