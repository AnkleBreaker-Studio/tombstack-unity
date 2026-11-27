using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// The Tombstack Hub (<c>Window ▸ Tombstack ▸ Hub</c>): account status, studio/game
    /// picker fed by <c>GET /api/editor/me</c>, project linking (mint SDK token → write
    /// config), connection status card, and the live dashboard tab
    /// (<see cref="TombstackHubDashboard"/> sub-controller).
    /// </summary>
    public sealed class TombstackHubWindow : EditorWindow
    {
        private const string UXML_FILE = "TombstackHub.uxml";
        private const float MIN_WIDTH = 520f;
        private const float MIN_HEIGHT = 480f;
        private const string NO_GAMES_CHOICE = "— no games —";
        private const string NOT_LOADED_CHOICE = "— not loaded —";

        private Label _statusText;
        private VisualElement _statusDot;
        private VisualElement _signedOutPanel;
        private VisualElement _signedInPanel;
        private Button _openSignInButton;
        private Button _tabConnection;
        private Button _tabDashboard;
        private ScrollView _panelConnection;
        private ScrollView _panelDashboard;
        private Label _errorBanner;
        private DropdownField _studioDropdown;
        private DropdownField _gameDropdown;
        private Button _linkButton;
        private Button _refreshStudiosButton;
        private Label _linkLoading;
        private Label _emailLabel;
        private Label _linkedGameLabel;
        private Label _tokenStateLabel;
        private Label _endpointLabel;
        private Button _unlinkButton;
        private Button _openWebButton;

        private TombstackHubDashboard _dashboard;
        private EditorMeData _me;
        // The session _me was loaded for (server and sign-in): when either changes it is dropped, so the
        // Hub never offers the studios of another server or account. Memory only, never serialized.
        private string _meOrigin;
        private string _meToken;
        private bool _busy;

        /// <summary>Open (or focus) the Hub window.</summary>
        [MenuItem("Window/Tombstack/Hub", priority = 10)]
        public static void Open()
        {
            var window = GetWindow<TombstackHubWindow>(utility: false, title: "Tombstack Hub");
            window.minSize = new Vector2(MIN_WIDTH, MIN_HEIGHT);
        }

        /// <summary>Unity UI Toolkit entry point — builds the visual tree.</summary>
        public void CreateGUI()
        {
            if (!TombstackEditorUi.BuildWindow(rootVisualElement, UXML_FILE)) return;
            queryElements();
            registerCallbacks();
            _dashboard = new TombstackHubDashboard(rootVisualElement);
            TombstackSession.OnChanged += onSessionChanged;
            refreshState();
            // Automatic: never prompts, and never sends credentials to an unconfirmed server.
            if (TombstackSession.IsSignedIn) loadStudios(askUser: false);
        }

        private void OnDisable()
        {
            TombstackSession.OnChanged -= onSessionChanged;
            unregisterCallbacks();
            _dashboard?.Dispose();
            _dashboard = null;
        }

        private void queryElements()
        {
            var root = rootVisualElement;
            _statusText = root.Q<Label>("hub-status-text");
            _statusDot = root.Q("hub-status-dot");
            _signedOutPanel = root.Q("hub-signedout");
            _signedInPanel = root.Q("hub-signedin");
            _openSignInButton = root.Q<Button>("hub-open-signin");
            _tabConnection = root.Q<Button>("hub-tab-connection");
            _tabDashboard = root.Q<Button>("hub-tab-dashboard");
            _panelConnection = root.Q<ScrollView>("hub-panel-connection");
            _panelDashboard = root.Q<ScrollView>("hub-panel-dashboard");
            _errorBanner = root.Q<Label>("hub-error");
            _studioDropdown = root.Q<DropdownField>("hub-studio");
            _gameDropdown = root.Q<DropdownField>("hub-game");
            _linkButton = root.Q<Button>("hub-link");
            _refreshStudiosButton = root.Q<Button>("hub-refresh-studios");
            _linkLoading = root.Q<Label>("hub-link-loading");
            _emailLabel = root.Q<Label>("hub-email");
            _linkedGameLabel = root.Q<Label>("hub-linked-game");
            _tokenStateLabel = root.Q<Label>("hub-token-state");
            _endpointLabel = root.Q<Label>("hub-endpoint");
            _unlinkButton = root.Q<Button>("hub-unlink");
            _openWebButton = root.Q<Button>("hub-open-web");
        }

        private void registerCallbacks()
        {
            _openSignInButton?.RegisterCallback<ClickEvent>(onOpenSignInClicked);
            _tabConnection?.RegisterCallback<ClickEvent>(onConnectionTabClicked);
            _tabDashboard?.RegisterCallback<ClickEvent>(onDashboardTabClicked);
            _linkButton?.RegisterCallback<ClickEvent>(onLinkClicked);
            _refreshStudiosButton?.RegisterCallback<ClickEvent>(onRefreshStudiosClicked);
            _unlinkButton?.RegisterCallback<ClickEvent>(onUnlinkClicked);
            _openWebButton?.RegisterCallback<ClickEvent>(onOpenWebClicked);
            _studioDropdown?.RegisterValueChangedCallback(onStudioChanged);
        }

        private void unregisterCallbacks()
        {
            _openSignInButton?.UnregisterCallback<ClickEvent>(onOpenSignInClicked);
            _tabConnection?.UnregisterCallback<ClickEvent>(onConnectionTabClicked);
            _tabDashboard?.UnregisterCallback<ClickEvent>(onDashboardTabClicked);
            _linkButton?.UnregisterCallback<ClickEvent>(onLinkClicked);
            _refreshStudiosButton?.UnregisterCallback<ClickEvent>(onRefreshStudiosClicked);
            _unlinkButton?.UnregisterCallback<ClickEvent>(onUnlinkClicked);
            _openWebButton?.UnregisterCallback<ClickEvent>(onOpenWebClicked);
            _studioDropdown?.UnregisterValueChangedCallback(onStudioChanged);
        }

        private void onSessionChanged()
        {
            // The project's server or its sign-in changed: what was loaded for the previous one no longer applies.
            if (_me != null && !TombstackSession.IsCurrent(_meOrigin, _meToken)) dropLoadedStudios();
            refreshState();
            if (TombstackSession.IsSignedIn && _me == null) loadStudios(askUser: false);
        }

        /// <summary>Sync every panel/label with the current session + project link state.</summary>
        private void refreshState()
        {
            bool signedIn = TombstackSession.IsSignedIn;
            _signedOutPanel?.EnableInClassList("hidden", signedIn);
            _signedInPanel?.EnableInClassList("hidden", !signedIn);

            var settings = TombstackProjectSettingsSO.instance;
            bool linked = settings.IsLinked;
            bool hasToken = TombstackConfigWriter.HasSdkToken();

            if (_statusText != null)
                _statusText.text = !signedIn ? "SIGNED OUT" : linked ? "CONNECTED" : "SIGNED IN";
            _statusDot?.EnableInClassList("tmb-dot--ok", signedIn && linked && hasToken);
            _statusDot?.EnableInClassList("tmb-dot--warn", signedIn && (!linked || !hasToken));
            _statusDot?.EnableInClassList("tmb-dot--bad", !signedIn);

            if (_emailLabel != null) _emailLabel.text = signedIn ? TombstackSession.Email : "—";
            if (_linkedGameLabel != null)
                _linkedGameLabel.text = linked ? $"{settings.GameName}  ({settings.StudioName})" : "Not linked";
            if (_tokenStateLabel != null)
                _tokenStateLabel.text = TombstackConfigWriter.DescribeTokenState();
            if (_endpointLabel != null)
            {
                bool confirmed = TombstackEndpointTrust.IsTrusted(settings.ResolveOrigin());
                _endpointLabel.text = settings.ResolveEndpoint() + (confirmed ? "" : "  (not confirmed)");
            }
            _unlinkButton?.SetEnabled(linked);
            _dashboard?.SyncLinkState();
        }

        private void onOpenSignInClicked(ClickEvent evt) => TombstackSignInWindow.Open();

        private void onConnectionTabClicked(ClickEvent evt) => selectTab(dashboard: false);

        private void onDashboardTabClicked(ClickEvent evt) => selectTab(dashboard: true);

        private void selectTab(bool dashboard)
        {
            _tabConnection?.EnableInClassList("tmb-tab--active", !dashboard);
            _tabDashboard?.EnableInClassList("tmb-tab--active", dashboard);
            _panelConnection?.EnableInClassList("hidden", dashboard);
            _panelDashboard?.EnableInClassList("hidden", !dashboard);
            _dashboard?.SetActive(dashboard);
        }

        /// <param name="askUser">True only for a click: an unconfirmed server may then be confirmed
        /// through a dialog. Automatic loads never prompt and send nothing to such a server.</param>
        private async void loadStudios(bool askUser)
        {
            try
            {
                showError(null);
                if (!TombstackEndpointTrust.ConfirmCurrent(askUser))
                {
                    showError(TombstackEndpointTrust.NotConfirmedMessage(TombstackSession.ServerOrigin));
                    return;
                }
                // The session this request belongs to: the server and the sign-in can change meanwhile.
                var origin = TombstackSession.ServerOrigin;
                var token = TombstackSession.EditorToken;
                var result = await TombstackEditorApi.GetMeAsync(token);
                if (this == null) return;
                bool current = TombstackSession.IsCurrent(origin, token);
                if (!result.Ok && result.Status == 401) TombstackSession.HandleUnauthorized(origin, token);
                if (!current)
                {
                    // An answer for an earlier server or sign-in is neither shown nor kept. Load for the
                    // current session unless the change already started that: one made by another Unity
                    // process (EditorPrefs are per machine) raises no OnChanged in this one.
                    if (_me == null && TombstackSession.IsSignedIn) loadStudios(askUser: false);
                    return;
                }
                if (!result.Ok)
                {
                    showError(result.Error);
                    return;
                }
                _me = result.Data;
                _meOrigin = origin;
                _meToken = token;
                populateStudioDropdown();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] loading studios failed: {e.Message}");
                if (this != null) showError("Could not load your studios — see the Console.");
            }
        }

        private void populateStudioDropdown()
        {
            if (_studioDropdown == null || _me?.studios == null) return;
            var choices = new List<string>(_me.studios.Length);
            int preselect = 0;
            for (int i = 0; i < _me.studios.Length; i++)
            {
                choices.Add(_me.studios[i].name);
                if (_me.studios[i].id == TombstackProjectSettingsSO.instance.StudioId) preselect = i;
            }
            _studioDropdown.choices = choices;
            if (choices.Count > 0) _studioDropdown.index = preselect;
            populateGameDropdown();
        }

        /// <summary>Forget the studios loaded for an earlier server or sign-in, and stop showing their
        /// names: the lists show a placeholder until the current session's load fills them.</summary>
        private void dropLoadedStudios()
        {
            _me = null;
            _meOrigin = null;
            _meToken = null;
            if (_studioDropdown != null)
            {
                _studioDropdown.choices = new List<string> { NOT_LOADED_CHOICE };
                _studioDropdown.index = 0;
            }
            populateGameDropdown();
        }

        private void onStudioChanged(ChangeEvent<string> evt) => populateGameDropdown();

        private void populateGameDropdown()
        {
            if (_gameDropdown == null) return;
            var studio = selectedStudio();
            var choices = new List<string>();
            int preselect = 0;
            if (studio?.games != null)
            {
                for (int i = 0; i < studio.games.Length; i++)
                {
                    choices.Add(studio.games[i].name);
                    if (studio.games[i].id == TombstackProjectSettingsSO.instance.GameId) preselect = i;
                }
            }
            if (choices.Count == 0) choices.Add(NO_GAMES_CHOICE);
            _gameDropdown.choices = choices;
            _gameDropdown.index = preselect;
        }

        private EditorStudio selectedStudio()
        {
            if (_me?.studios == null || _studioDropdown == null) return null;
            int index = _studioDropdown.index;
            return index >= 0 && index < _me.studios.Length ? _me.studios[index] : null;
        }

        private EditorGame selectedGame(EditorStudio studio)
        {
            if (studio?.games == null || _gameDropdown == null) return null;
            int index = _gameDropdown.index;
            return index >= 0 && index < studio.games.Length ? studio.games[index] : null;
        }

        private async void onLinkClicked(ClickEvent evt)
        {
            if (_busy) return;
            var studio = selectedStudio();
            var game = selectedGame(studio);
            if (studio == null || game == null)
            {
                showError("Pick a studio and a game first (Refresh if the lists are empty).");
                return;
            }

            if (!TombstackEndpointTrust.ConfirmCurrent(askUser: true))
            {
                showError(TombstackEndpointTrust.NotConfirmedMessage(TombstackSession.ServerOrigin));
                return;
            }

            setBusy(true);
            showError(null);
            try
            {
                var result = await TombstackSession.LinkProjectAsync(studio.id, studio.name, game.id, game.name);
                if (this == null) return;
                if (!result.Ok) showError(result.Error);
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[Tombstack] linking failed: {e.Message}");
                if (this != null) showError("Linking failed — see the Console.");
            }
            finally
            {
                if (this != null) setBusy(false);
            }
        }

        private void onRefreshStudiosClicked(ClickEvent evt)
        {
            _me = null;
            loadStudios(askUser: true);
        }

        private void onUnlinkClicked(ClickEvent evt) => TombstackSession.UnlinkProject();

        private void onOpenWebClicked(ClickEvent evt)
        {
            var settings = TombstackProjectSettingsSO.instance;
            var url = settings.IsLinked
                ? settings.ResolveEndpoint() + "/games/" + settings.GameId
                : settings.ResolveEndpoint();
            Application.OpenURL(url);
        }

        private void setBusy(bool busy)
        {
            _busy = busy;
            _linkButton?.SetEnabled(!busy);
            _linkLoading?.EnableInClassList("hidden", !busy);
        }

        private void showError(string message)
        {
            if (_errorBanner == null) return;
            bool hasError = !string.IsNullOrEmpty(message);
            _errorBanner.text = hasError ? message : string.Empty;
            _errorBanner.EnableInClassList("hidden", !hasError);
        }
    }
}
