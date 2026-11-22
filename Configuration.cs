using System;
using MelonLoader;

namespace ServerAntiEsp
{
    /// <summary>
    /// Manages user configuration via MelonPreferences.
    /// Provides validated runtime settings with sensible defaults.
    /// </summary>
    public static class Configuration
    {
        private const string CategoryName = "ServerAntiEsp";
        private const string CategoryDisplayName = "Server Anti-ESP Configuration";

        // Master Toggle
        public static bool AntiEspEnabled { get; set; }

        // Category Filters
        public static bool FilterPlayers { get; set; }
        public static bool FilterCreatures { get; set; }
        public static bool FilterLooseItems { get; set; }
        public static bool FilterMineables { get; set; }

        // Distance Settings (meters)
        public static float MaxVisibilityDistance { get; set; }
        public static float PersonalBubbleRadius { get; set; }
        public static float CloseRangeRadius { get; set; }
        public static float MidRangeRadius { get; set; }

        // Timing Settings (seconds)
        public static float DespawnDelaySeconds { get; set; }
        public static float VisibilityUpdateInterval { get; set; }
        public static float CloseRangeCheckInterval { get; set; }
        public static float MidRangeCheckInterval { get; set; }
        public static float FarRangeCheckInterval { get; set; }

        // Geometry & Raycast Features
        public static bool UseBidirectionalRaycasts { get; set; }
        public static bool HitBackfaces { get; set; }

        // Logging & Diagnostics
        public static bool EnableDebugLogging { get; set; }
        public static bool EnableRaycastDiagnostics { get; set; }
        public static bool EnableDiagnostics { get; set; }
        public static bool LogVisibilityTransitions { get; set; }

        private static MelonPreferences_Category _category;

        public static void Initialize(MelonLogger.Instance log)
        {
            try
            {
                _category = MelonPreferences.CreateCategory(CategoryName, CategoryDisplayName);

                var prefEnabled = _category.CreateEntry("AntiEspEnabled", true, "Master Toggle", "Enable or disable ServerAntiESP replication filtering entirely.");
                var prefPlayers = _category.CreateEntry("FilterPlayers", true, "Filter Other Players", "Hide other players when occluded behind walls/geometry.");
                var prefCreatures = _category.CreateEntry("FilterCreatures", true, "Filter Creatures", "Hide animals and monsters (Spriggulls, Wyrms, etc.) when occluded.");
                var prefItems = _category.CreateEntry("FilterLooseItems", true, "Filter Loose Items", "Hide un-docked ground items (stones, sticks, ores, weapons) when occluded.");
                var prefMineables = _category.CreateEntry("FilterMineables", true, "Filter Destructibles/Mineables", "Hide destructible resource nodes (boulders, breakable boxes) when occluded.");

                var prefMaxDist = _category.CreateEntry("MaxVisibilityDistance", 50.0f, "Max Visibility Range (m)", "Maximum distance beyond which entities are never replicated to clients.");
                var prefBubble = _category.CreateEntry("PersonalBubbleRadius", 1.5f, "Personal Bubble Radius (m)", "Close proximity radius for picking up items at your feet. Raycast is still checked if blocked by solid walls.");
                var prefClose = _category.CreateEntry("CloseRangeRadius", 15.0f, "Close Range Distance (m)", "Distance threshold for high-frequency visibility evaluation.");
                var prefMid = _category.CreateEntry("MidRangeRadius", 35.0f, "Mid Range Distance (m)", "Distance threshold for medium-frequency visibility evaluation.");

                var prefDespawnDelay = _category.CreateEntry("DespawnDelaySeconds", 0.15f, "Despawn Hysteresis Delay (s)", "Delay in seconds before an occluded entity is despawned. Prevents flickering at corners.");
                var prefUpdateInterval = _category.CreateEntry("VisibilityUpdateInterval", 0.05f, "Visibility Sweep Interval (s)", "Interval in seconds between full visibility sweeps across loaded chunks.");
                var prefCloseInterval = _category.CreateEntry("CloseRangeCheckInterval", 0.05f, "Close Range Check Interval (s)", "How often close entities (< 15m) are raychecked.");
                var prefMidInterval = _category.CreateEntry("MidRangeCheckInterval", 0.15f, "Mid Range Check Interval (s)", "How often mid-range entities (15-35m) are raychecked.");
                var prefFarInterval = _category.CreateEntry("FarRangeCheckInterval", 0.50f, "Far Range Check Interval (s)", "How often distant entities (> 35m) are raychecked.");

                var prefBidir = _category.CreateEntry("UseBidirectionalRaycasts", true, "Bidirectional Raycasts", "Performs reverse raycasts to detect inverted wall normals and single-sided mesh colliders.");
                var prefBackfaces = _category.CreateEntry("HitBackfaces", true, "Hit Mesh Backfaces", "Ensures Unity raycasts collide with single-sided wall mesh backfaces.");

                var prefDebugLog = _category.CreateEntry("EnableDebugLogging", false, "Enable Debug Logging", "Verbose per-entity evaluation messages in the server console.");
                var prefRaycastDiag = _category.CreateEntry("EnableRaycastDiagnostics", false, "Enable Raycast Diagnostics", "Detailed breakdown of raycast hits, obstacle names, layers, and distances.");
                var prefDiagnostics = _category.CreateEntry("EnableDiagnostics", true, "Enable Diagnostics", "Track real-time performance metrics (raycasts/sec, occlusions, checks/sec).");
                var prefTransitions = _category.CreateEntry("LogVisibilityTransitions", false, "Log Visibility Transitions", "Log individual spawn and despawn events to the console.");

                // Validate and assign
                AntiEspEnabled = prefEnabled.Value;
                FilterPlayers = prefPlayers.Value;
                FilterCreatures = prefCreatures.Value;
                FilterLooseItems = prefItems.Value;
                FilterMineables = prefMineables.Value;

                MaxVisibilityDistance = Clamp(prefMaxDist.Value, 10.0f, 150.0f);
                PersonalBubbleRadius = Clamp(prefBubble.Value, 0.5f, 5.0f);
                CloseRangeRadius = Clamp(prefClose.Value, 5.0f, 30.0f);
                MidRangeRadius = Clamp(prefMid.Value, CloseRangeRadius, MaxVisibilityDistance);

                DespawnDelaySeconds = Clamp(prefDespawnDelay.Value, 0.05f, 2.0f);
                VisibilityUpdateInterval = Clamp(prefUpdateInterval.Value, 0.02f, 0.5f);
                CloseRangeCheckInterval = Clamp(prefCloseInterval.Value, 0.02f, 0.5f);
                MidRangeCheckInterval = Clamp(prefMidInterval.Value, 0.05f, 1.0f);
                FarRangeCheckInterval = Clamp(prefFarInterval.Value, 0.1f, 2.0f);

                UseBidirectionalRaycasts = prefBidir.Value;
                HitBackfaces = prefBackfaces.Value;

                EnableDebugLogging = prefDebugLog.Value;
                EnableRaycastDiagnostics = prefRaycastDiag.Value;
                EnableDiagnostics = prefDiagnostics.Value;
                LogVisibilityTransitions = prefTransitions.Value;

                if (log != null)
                {
                    log.Msg(string.Format("[Config] Loaded v3.0.0: Enabled={0}, DespawnDelay={1:F2}s, UpdateInterval={2:F2}s, MaxDist={3:F0}m",
                        AntiEspEnabled, DespawnDelaySeconds, VisibilityUpdateInterval, MaxVisibilityDistance));
                    log.Msg(string.Format("[Config] Obstacle features: BidirRay={0}, HitBackfaces={1}, RayDiag={2}",
                        UseBidirectionalRaycasts, HitBackfaces, EnableRaycastDiagnostics));
                }
            }
            catch (Exception ex)
            {
                if (log != null)
                {
                    log.Error("[Config] Error loading preferences, using safe defaults: " + ex.Message);
                }
                ApplyDefaults();
            }
        }

        private static void ApplyDefaults()
        {
            AntiEspEnabled = true;
            FilterPlayers = true;
            FilterCreatures = true;
            FilterLooseItems = true;
            FilterMineables = true;

            MaxVisibilityDistance = 50.0f;
            PersonalBubbleRadius = 1.5f;
            CloseRangeRadius = 15.0f;
            MidRangeRadius = 35.0f;

            DespawnDelaySeconds = 0.15f;
            VisibilityUpdateInterval = 0.05f;
            CloseRangeCheckInterval = 0.05f;
            MidRangeCheckInterval = 0.15f;
            FarRangeCheckInterval = 0.50f;

            UseBidirectionalRaycasts = true;
            HitBackfaces = true;

            EnableDebugLogging = false;
            EnableRaycastDiagnostics = false;
            EnableDiagnostics = true;
            LogVisibilityTransitions = false;
        }

        private static float Clamp(float val, float min, float max)
        {
            if (val < min) return min;
            if (val > max) return max;
            return val;
        }
    }
}
