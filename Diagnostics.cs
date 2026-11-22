using System;
using MelonLoader;
using UnityEngine;

namespace ServerAntiEsp
{
    /// <summary>
    /// Provides real-time metrics, telemetry, and health diagnostics
    /// for the anti-ESP replication engine.
    /// </summary>
    public static class Diagnostics
    {
        private static int _raycastsThisSecond = 0;
        private static int _checksThisSecond = 0;
        private static float _lastMetricsTime = 0f;

        public static int RaycastsPerSecond { get; private set; }
        public static int VisibilityChecksPerSecond { get; private set; }
        public static int ActiveOcclusions { get; set; }
        public static int TotalSpawns { get; private set; }
        public static int TotalDespawns { get; private set; }
        public static int ConnectedPlayerCount { get; set; }
        public static int TrackedEntityCount { get; set; }

        public static void RecordRaycast()
        {
            if (Configuration.EnableDiagnostics)
            {
                _raycastsThisSecond++;
            }
        }

        public static void RecordVisibilityCheck()
        {
            if (Configuration.EnableDiagnostics)
            {
                _checksThisSecond++;
            }
        }

        public static void RecordSpawn()
        {
            TotalSpawns++;
        }

        public static void RecordDespawn()
        {
            TotalDespawns++;
        }

        public static void Update(float currentTime)
        {
            if (currentTime - _lastMetricsTime >= 1.0f)
            {
                RaycastsPerSecond = _raycastsThisSecond;
                VisibilityChecksPerSecond = _checksThisSecond;
                _raycastsThisSecond = 0;
                _checksThisSecond = 0;
                _lastMetricsTime = currentTime;

                if (Configuration.EnableDebugLogging)
                {
                    MelonLogger.Msg(string.Format("[AntiESP Stats] Players: {0} | Entities: {1} | Checks/s: {2} | Raycasts/s: {3} | Occluded: {4} | Spawns: {5} | Despawns: {6}",
                        ConnectedPlayerCount, TrackedEntityCount, VisibilityChecksPerSecond, RaycastsPerSecond, ActiveOcclusions, TotalSpawns, TotalDespawns));
                }
            }
        }

        public static string GetStatusReport()
        {
            return string.Format(
                "--- ServerAntiESP Diagnostics ---\n" +
                "Connected Players:       {0}\n" +
                "Tracked Entities:        {1}\n" +
                "Active Occlusions:       {2}\n" +
                "Visibility Checks/sec:   {3}\n" +
                "Raycasts/sec:            {4}\n" +
                "Lifetime Spawns:         {5}\n" +
                "Lifetime Despawns:       {6}\n" +
                "--------------------------------",
                ConnectedPlayerCount, TrackedEntityCount, ActiveOcclusions,
                VisibilityChecksPerSecond, RaycastsPerSecond, TotalSpawns, TotalDespawns);
        }

        public static void Reset()
        {
            _raycastsThisSecond = 0;
            _checksThisSecond = 0;
            RaycastsPerSecond = 0;
            VisibilityChecksPerSecond = 0;
            ActiveOcclusions = 0;
            ConnectedPlayerCount = 0;
            TrackedEntityCount = 0;
        }
    }
}
