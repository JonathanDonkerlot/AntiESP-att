using System;
using MelonLoader;
using UnityEngine;
using Alta.Networking;

[assembly: MelonInfo(typeof(ServerAntiEsp.ServerAntiEspMod), "ServerAntiEsp", "3.0.0", "Security")]
[assembly: MelonGame("Alta", "A Township Tale")]

namespace ServerAntiEsp
{
    /// <summary>
    /// ServerAntiESP v3.0.0 — Authoritative Anti-ESP & World Geometry Occlusion Engine
    /// for A Township Tale servers.
    /// 
    /// Combines complete modular architecture, universal entity classification,
    /// 9-layer world geometry occlusion, multi-hit bidirectional raycasting,
    /// and zero-allocation performance.
    /// </summary>
    public class ServerAntiEspMod : MelonMod
    {
        public static ServerAntiEspMod Instance { get; private set; }
        public static MelonLogger.Instance Log { get; private set; }

        private float _lastUpdateTime = 0f;

        public override void OnInitializeMelon()
        {
            Instance = this;
            Log = LoggerInstance;

            Log.Msg("==========================================================");
            Log.Msg(" ServerAntiESP v3.0.0 — Major Architecture & Occlusion Engine");
            Log.Msg(" Authoritative Server-Side Anti-ESP for A Township Tale");
            Log.Msg("==========================================================");

            // 1. Initialize configuration from MelonPreferences
            Configuration.Initialize(Log);

            // 2. Resolve world obstacle layers and initialize occlusion mask
            LineOfSight.GetOcclusionMask();

            // 3. Apply Harmony patches to Chunker replication hooks
            HarmonyPatches.Apply(HarmonyInstance, Log);

            Log.Msg("[ServerAntiEsp] Initialization complete. Universal server authority active.");
        }

        public override void OnUpdate()
        {
            if (!Configuration.AntiEspEnabled)
            {
                return;
            }

            // Only run replication filtering on authoritative servers (dedicated or host)
            if (!NetworkSceneManager.IsServer)
            {
                return;
            }

            float currentTime = Time.time;
            if (currentTime - _lastUpdateTime < Configuration.VisibilityUpdateInterval)
            {
                return;
            }
            _lastUpdateTime = currentTime;

            // Execute scheduled visibility and replication sweep
            VisibilityManager.ExecuteVisibilityCycle(currentTime);

            // Update real-time performance telemetry
            Diagnostics.Update(currentTime);
        }

        public override void OnSceneWasUnloaded(int buildIndex, string sceneName)
        {
            // Flush visibility caches when transitioning scenes/levels
            VisibilityManager.Clear();
            LineOfSight.ResetMaskCache();
            Diagnostics.Reset();

            if (Log != null)
            {
                Log.Msg("[ServerAntiEsp] Scene unloaded: visibility and occlusion state cleared.");
            }
        }

        public override void OnApplicationQuit()
        {
            VisibilityManager.Clear();
            if (Log != null)
            {
                Log.Msg("[ServerAntiEsp] Server shutting down. Clean state exit.");
            }
        }
    }
}
