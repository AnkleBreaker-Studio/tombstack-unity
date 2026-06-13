using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// Forge-dark branded sign-in window for the Tombstack account. Opens from
    /// <c>Window ▸ Tombstack ▸ Sign In</c>, from the Hub, and once per editor session
    /// (non-nagging, via <see cref="TombstackFirstRun"/>) while the plugin is unconfigured.
    /// On success the window closes itself and the Hub opens.
    /// </summary>
    public sealed class TombstackSignInWindow : EditorWindow
    {
        private const string UXML_FILE = "TombstackSignIn.uxml";
        private const float WINDOW_WIDTH = 420f;
        private const float WINDOW_HEIGHT = 380f;

        private TextField _emailField;
        private TextField _passwordField;
        private Button _submitButton;
        private Label _errorLabel;
        private Label _loadingLabel;
        private Label _signupLink;
        private bool _busy;

        /// <summary>Open (or focus) the sign-in window.</summary>
        [MenuItem("Window/Tombstack/Sign In", priority = 11)]
        public static void Open()
        {
            var window = GetWindow<TombstackSignInWindow>(utility: false, title: "Tombstack — Sign In");
            window.minSize = new Vector2(WINDOW_WIDTH, WINDOW_HEIGHT);
        }

        /// <summary>Unity UI Toolkit entry point — builds the visual tree.</summary>
        public void CreateGUI()
        {
            if (!TombstackEditorUi.BuildWindow(rootVisualElement, UXML_FILE)) return;

            _emailField = rootVisualElement.Q<TextField>("signin-email");
            _passwordField = rootVisualElement.Q<TextField>("signin-password");
            _submitButton = rootVisualElement.Q<Button>("signin-submit");
            _errorLabel = rootVisualElement.Q<Label>("signin-error");
            _loadingLabel = rootVisualElement.Q<Label>("signin-loading");
            _signupLink = rootVisualElement.Q<Label>("signin-signup");

            _submitButton?.RegisterCallback<ClickEvent>(onSubmitClicked);
            _signupLink?.RegisterCallback<ClickEvent>(onSignupClicked);
            _passwordField?.RegisterCallback<KeyDownEvent>(onPasswordKeyDown);
        }

        private void OnDisable()
        {
            _submitButton?.UnregisterCallback<ClickEvent>(onSubmitClicked);
            _signupLink?.UnregisterCallback<ClickEvent>(onSignupClicked);
            _passwordField?.UnregisterCallback<KeyDownEvent>(onPasswordKeyDown);
        }

        private void onPasswordKeyDown(KeyDownEvent evt)
        {
            if (evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                onSubmitClicked(null);
        }

        private void onSignupClicked(ClickEvent evt)
        {
            Application.OpenURL(TombstackProjectSettingsSO.instance.ResolveEndpoint() + "/signup");
        }

        private async void onSubmitClicked(ClickEvent evt)
        {
            if (_busy) return;
            var email = _emailField?.value?.Trim();
            var password = _passwordField?.value;
            if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
            {
                showError("Enter your email and password.");
                return;
            }

            setBusy(true);
            showError(null);
            try
            {
                var result = await TombstackSession.SignInAsync(email, password);
                if (this == null) return; // window closed while awaiting
                if (!result.Ok)
                {
                    showError(result.Error);
                    return;
                }
                TombstackHubWindow.Open();
                Close();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] sign-in failed unexpectedly: {e.Message}");
                if (this != null) showError("Something went wrong — see the Console.");
            }
            finally
            {
                if (this != null) setBusy(false);
            }
        }

        private void setBusy(bool busy)
        {
            _busy = busy;
            _submitButton?.SetEnabled(!busy);
            _loadingLabel?.EnableInClassList("hidden", !busy);
        }

        private void showError(string message)
        {
            if (_errorLabel == null) return;
            bool hasError = !string.IsNullOrEmpty(message);
            _errorLabel.text = hasError ? message : string.Empty;
            _errorLabel.EnableInClassList("hidden", !hasError);
        }
    }
}
