using UnityEngine;
using AnkleBreaker.Tombstack;

/// <summary>
/// Two ways to start Tombstack:
///   1) Zero-code: create a TombstackConfig asset (Create ▸ Tombstack ▸ Config), fill in the
///      token + endpoint, and drop it under any Resources/ folder. It auto-inits on load.
///   2) Manual: put this component on a GameObject in your first scene (or call from your own
///      bootstrap). Replace the token + endpoint with your game's values from the dashboard.
/// </summary>
public sealed class TombstackBootstrap : MonoBehaviour
{
    [SerializeField] private string _gameToken = "tmb_REPLACE_ME";
    [SerializeField] private string _endpoint = "https://your-tenant.example.com";

    private void Awake()
    {
        Tombstack.Init(_gameToken, _endpoint);
        // Once your auth resolves the player:
        // Tombstack.SetUser("user-123", steamId: "7656119...");
        // Analytics events (events & funnels screens):
        // Tombstack.TrackEvent("level_complete", new Dictionary<string, string> { { "level", "3" } });
        // Mark interesting moments for the crash/bug trail:
        // Tombstack.AddBreadcrumb("matchmaking started", BreadcrumbLevel.Info, category: "net");
        // From an in-game feedback form:
        // Tombstack.ReportBug("Quest log is empty after loading a save.", category: "ui");
    }
}
