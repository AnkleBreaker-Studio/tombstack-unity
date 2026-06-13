using System.Collections.Generic;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace AnkleBreaker.Tombstack.Editor
{
    /// <summary>
    /// UI Toolkit Project Settings page (<c>Edit ▸ Project Settings ▸ Tombstack</c>):
    /// endpoint URL override, SDK defaults (heartbeat interval, consent requirement —
    /// written into the runtime config asset), unlink, and sign out. Account/linking
    /// happens in the Hub; this page is the project-level knob panel.
    /// </summary>
    public static class TombstackSettingsProvider
    {
        private const string UXML_FILE = "TombstackSettings.uxml";
        private const string SETTINGS_PATH = "Project/Tombstack";

        private static TextField _endpointField;
        private static Button _endpointApplyButton;
        private static FloatField _heartbeatField;
        private static Toggle _consentToggle;
        private static Button _signOutButton;
        private static Button _unlinkButton;
        private static Button _openHubButton;
        private static Label _accountLabel;
        private static Label _linkedGameLabel;
        private static Label _statusBanner;

        /// <summary>Register the settings page with the Project Settings window.</summary>
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider(SETTINGS_PATH, SettingsScope.Project)
            {
                label = "Tombstack",
                keywords = new HashSet<string>(new[] { "tombstack", "crash", "telemetry", "anklebreaker" }),
                activateHandler = activate,
                deactivateHandler = deactivate,
            };
        }

        private static void activate(string searchContext, VisualElement rootElement)
        {
            if (!TombstackEditorUi.BuildWindow(rootElement, UXML_FILE)) return;

            _endpointField = rootElement.Q<TextField>("set-endpoint");
            _endpointApplyButton = rootElement.Q<Button>("set-endpoint-apply");
            _heartbeatField = rootElement.Q<FloatField>("set-heartbeat");
            _consentToggle = rootElement.Q<Toggle>("set-consent");
            _signOutButton = rootElement.Q<Button>("set-signout");
            _unlinkButton = rootElement.Q<Button>("set-unlink");
            _openHubButton = rootElement.Q<Button>("set-open-hub");
            _accountLabel = rootElement.Q<Label>("set-account");
            _linkedGameLabel = rootElement.Q<Label>("set-linked-game");
            _statusBanner = rootElement.Q<Label>("set-status");

            _endpointApplyButton?.RegisterCallback<ClickEvent>(onEndpointApplyClicked);
            _heartbeatField?.RegisterValueChangedCallback(onHeartbeatChanged);
            _consentToggle?.RegisterValueChangedCallback(onConsentChanged);
            _signOutButton?.RegisterCallback<ClickEvent>(onSignOutClicked);
            _unlinkButton?.RegisterCallback<ClickEvent>(onUnlinkClicked);
            _openHubButton?.RegisterCallback<ClickEvent>(onOpenHubClicked);
            TombstackSession.OnChanged += refreshValues;

            refreshValues();
        }

        private static void deactivate()
        {
            TombstackSession.OnChanged -= refreshValues;
            _endpointApplyButton?.UnregisterCallback<ClickEvent>(onEndpointApplyClicked);
            _heartbeatField?.UnregisterValueChangedCallback(onHeartbeatChanged);
            _consentToggle?.UnregisterValueChangedCallback(onConsentChanged);
            _signOutButton?.UnregisterCallback<ClickEvent>(onSignOutClicked);
            _unlinkButton?.UnregisterCallback<ClickEvent>(onUnlinkClicked);
            _openHubButton?.UnregisterCallback<ClickEvent>(onOpenHubClicked);
            _endpointField = null;
            _endpointApplyButton = null;
            _heartbeatField = null;
            _consentToggle = null;
            _signOutButton = null;
            _unlinkButton = null;
            _openHubButton = null;
            _accountLabel = null;
            _linkedGameLabel = null;
            _statusBanner = null;
        }

        private static void refreshValues()
        {
            var settings = TombstackProjectSettingsSO.instance;
            if (_endpointField != null) _endpointField.value = settings.EndpointOverride;
            if (_accountLabel != null)
                _accountLabel.text = TombstackSession.IsSignedIn ? TombstackSession.Email : "Signed out";
            if (_linkedGameLabel != null)
                _linkedGameLabel.text = settings.IsLinked
                    ? $"{settings.GameName}  ({settings.StudioName})"
                    : "Not linked";
            _unlinkButton?.SetEnabled(settings.IsLinked);
            _signOutButton?.SetEnabled(TombstackSession.IsSignedIn);

            var config = TombstackConfigWriter.FindConfig();
            bool hasConfig = config != null;
            _heartbeatField?.SetEnabled(hasConfig);
            _consentToggle?.SetEnabled(hasConfig);
            if (hasConfig)
            {
                _heartbeatField?.SetValueWithoutNotify(config.HeartbeatIntervalSeconds);
                _consentToggle?.SetValueWithoutNotify(config.RequireConsent);
                showStatus(null);
            }
            else
            {
                showStatus("No TombstackConfig asset yet — link the project from the Hub to create one.");
            }
        }

        private static void onEndpointApplyClicked(ClickEvent evt)
        {
            TombstackProjectSettingsSO.instance.SetEndpointOverride(_endpointField?.value);
            showStatus("Endpoint saved. Re-link the project to push it into the config asset.");
        }

        private static void onHeartbeatChanged(ChangeEvent<float> evt)
            => TombstackConfigWriter.WriteHeartbeatInterval(evt.newValue);

        private static void onConsentChanged(ChangeEvent<bool> evt)
            => TombstackConfigWriter.WriteRequireConsent(evt.newValue);

        private static async void onSignOutClicked(ClickEvent evt)
        {
            try { await TombstackSession.SignOutAsync(); }
            catch (System.Exception e) { Debug.LogWarning($"[Tombstack] sign-out failed: {e.Message}"); }
        }

        private static void onUnlinkClicked(ClickEvent evt) => TombstackSession.UnlinkProject();

        private static void onOpenHubClicked(ClickEvent evt) => TombstackHubWindow.Open();

        private static void showStatus(string message)
        {
            if (_statusBanner == null) return;
            bool has = !string.IsNullOrEmpty(message);
            _statusBanner.text = has ? message : string.Empty;
            _statusBanner.EnableInClassList("hidden", !has);
        }
    }
}
