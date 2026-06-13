using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Gentle first-run bootstrap. On editor load, if the plugin is unconfigured (not signed
    /// in or project unlinked), a small dismissible prompt window points to the Hub — at most
    /// once per editor session (SessionState) and never again once the user clicks
    /// "Don't show this again" (EditorPrefs). It is a regular window, not a modal nag.
    /// </summary>
    [InitializeOnLoad]
    public sealed class TombstackFirstRun : EditorWindow
    {
        private const string UXML_FILE = "TombstackFirstRun.uxml";
        private const string SESSION_SHOWN_KEY = "AnkleBreaker.Tombstack.FirstRunShownThisSession";
        private const string PREFS_DISMISSED = "AnkleBreaker.Tombstack.FirstRunDismissed";
        private const float WINDOW_WIDTH = 460f;
        private const float WINDOW_HEIGHT = 230f;

        private Button _openHubButton;
        private Button _laterButton;
        private Label _dismissLink;

        static TombstackFirstRun()
        {
            // Defer: AssetDatabase / windows aren't safe in the static constructor itself.
            EditorApplication.delayCall += maybeShowOnLoad;
        }

        private static void maybeShowOnLoad()
        {
            try
            {
                if (Application.isBatchMode) return;
                if (TombstackSession.IsConfigured) return;
                if (EditorPrefs.GetBool(PREFS_DISMISSED, false)) return;
                if (SessionState.GetBool(SESSION_SHOWN_KEY, false)) return;
                SessionState.SetBool(SESSION_SHOWN_KEY, true);

                var window = GetWindow<TombstackFirstRun>(utility: true, title: "Tombstack");
                window.minSize = new Vector2(WINDOW_WIDTH, WINDOW_HEIGHT);
                window.maxSize = new Vector2(WINDOW_WIDTH, WINDOW_HEIGHT);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] first-run prompt failed: {e.Message}");
            }
        }

        /// <summary>Unity UI Toolkit entry point — builds the visual tree.</summary>
        public void CreateGUI()
        {
            if (!TombstackEditorUi.BuildWindow(rootVisualElement, UXML_FILE)) return;
            _openHubButton = rootVisualElement.Q<Button>("firstrun-open-hub");
            _laterButton = rootVisualElement.Q<Button>("firstrun-later");
            _dismissLink = rootVisualElement.Q<Label>("firstrun-dismiss");

            _openHubButton?.RegisterCallback<ClickEvent>(onOpenHubClicked);
            _laterButton?.RegisterCallback<ClickEvent>(onLaterClicked);
            _dismissLink?.RegisterCallback<ClickEvent>(onDismissClicked);
        }

        private void OnDisable()
        {
            _openHubButton?.UnregisterCallback<ClickEvent>(onOpenHubClicked);
            _laterButton?.UnregisterCallback<ClickEvent>(onLaterClicked);
            _dismissLink?.UnregisterCallback<ClickEvent>(onDismissClicked);
        }

        private void onOpenHubClicked(ClickEvent evt)
        {
            TombstackHubWindow.Open();
            Close();
        }

        private void onLaterClicked(ClickEvent evt) => Close();

        private void onDismissClicked(ClickEvent evt)
        {
            EditorPrefs.SetBool(PREFS_DISMISSED, true);
            Close();
        }
    }
}
