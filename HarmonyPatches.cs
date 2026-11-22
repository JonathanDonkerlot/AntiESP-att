using System;
using System.Reflection;
using HarmonyLib;
using MelonLoader;
using UnityEngine;
using Alta.Networking;
using Alta.Chunks;

namespace ServerAntiEsp
{
    /// <summary>
    /// Harmony hooks for Chunker replication methods.
    /// Intercepts entity spawning at the server authority boundary before network messages are emitted.
    /// </summary>
    public static class HarmonyPatches
    {
        public static bool IsHooked { get; private set; }

        public static void Apply(HarmonyLib.Harmony harmony, MelonLogger.Instance log)
        {
            try
            {
                MethodInfo[] methods = typeof(Chunker).GetMethods(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);

                MethodInfo startSpawn = null;
                MethodInfo endSpawn = null;

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    string name = m.Name;

                    if (name.EndsWith("StartSpawn") && startSpawn == null)
                    {
                        ParameterInfo[] pars = m.GetParameters();
                        if (pars.Length == 3 && pars[0].ParameterType == typeof(Player)
                            && pars[1].ParameterType == typeof(bool)
                            && pars[2].ParameterType == typeof(bool)
                            && m.ReturnType == typeof(bool))
                        {
                            startSpawn = m;
                        }
                    }

                    if (name.EndsWith("EndSpawn") && endSpawn == null)
                    {
                        ParameterInfo[] pars = m.GetParameters();
                        if (pars.Length == 2 && pars[0].ParameterType == typeof(Player)
                            && pars[1].ParameterType == typeof(bool)
                            && m.ReturnType == typeof(void))
                        {
                            endSpawn = m;
                        }
                    }
                }

                if (startSpawn == null)
                {
                    log.Error("[Harmony] FAILED to locate Chunker.IChunker.StartSpawn via reflection!");
                    return;
                }

                if (endSpawn == null)
                {
                    log.Error("[Harmony] FAILED to locate Chunker.IChunker.EndSpawn via reflection!");
                    return;
                }

                MethodInfo prefixStart = typeof(HarmonyPatches).GetMethod(
                    "Prefix_StartSpawn", BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(startSpawn, new HarmonyMethod(prefixStart), null, null, null, null);

                MethodInfo prefixEnd = typeof(HarmonyPatches).GetMethod(
                    "Prefix_EndSpawn", BindingFlags.Static | BindingFlags.Public);
                harmony.Patch(endSpawn, new HarmonyMethod(prefixEnd), null, null, null, null);

                IsHooked = true;
                log.Msg(string.Format("[Harmony] Patches successfully hooked: {0} & {1}",
                    startSpawn.Name, endSpawn.Name));
            }
            catch (Exception ex)
            {
                log.Error("[Harmony] Exception while hooking Chunker methods: " + ex);
                IsHooked = false;
            }
        }

        /// <summary>
        /// Intercepts Chunker.IChunker.StartSpawn.
        /// If the entity is occluded from the player, blocks addition to SpawnedForPlayers
        /// and suppresses the SpawnMessage (ID=20).
        /// </summary>
        public static bool Prefix_StartSpawn(
            Chunker __instance,
            Player target,
            bool hasMovedChunks,
            bool isChunkSync,
            ref bool __result)
        {
            if (ReplicationManager.BypassFilter || !Configuration.AntiEspEnabled)
            {
                return true;
            }

            if (target == null || __instance == null || !NetworkSceneManager.IsServer)
            {
                return true;
            }

            NetworkEntity entity = __instance.NetworkEntity;
            if (entity == null)
            {
                return true;
            }

            EntityCategory category;
            if (!EntityClassifier.ShouldFilter(entity, target, out category))
            {
                return true; // Exempt from filtering (e.g. self, docked item)
            }

            PlayerController pc = target.PlayerController;
            if (pc == null)
            {
                return true;
            }

            Vector3 eyePos;
            Vector3 forward;
            LineOfSight.GetObserverEye(pc, out eyePos, out forward);

            bool isVisible = LineOfSight.Evaluate(eyePos, entity, category);

            VisibilityState state = VisibilityManager.GetOrCreateEntry(target.Identifier, entity.Identifier);
            state.LastCheckTime = Time.time;

            if (isVisible)
            {
                state.IsVisible = true;
                state.LastSeenTime = Time.time;
                state.DespawnPending = false;
                return true; // Authorize replication
            }
            else
            {
                state.IsVisible = false;
                __result = false; // Entity withheld: skip adding to SpawnedForPlayers and sending SpawnMessage
                return false;
            }
        }

        /// <summary>
        /// Intercepts Chunker.IChunker.EndSpawn.
        /// Validates that an entity about to complete serialization is legitimately visible.
        /// </summary>
        public static bool Prefix_EndSpawn(
            Chunker __instance,
            Player target,
            bool isChunkSync)
        {
            if (ReplicationManager.BypassFilter || !Configuration.AntiEspEnabled)
            {
                return true;
            }

            if (target == null || __instance == null || !NetworkSceneManager.IsServer)
            {
                return true;
            }

            NetworkEntity entity = __instance.NetworkEntity;
            if (entity == null)
            {
                return true;
            }

            EntityCategory category;
            if (!EntityClassifier.ShouldFilter(entity, target, out category))
            {
                return true;
            }

            PlayerController pc = target.PlayerController;
            if (pc == null)
            {
                return true;
            }

            Vector3 eyePos;
            Vector3 forward;
            LineOfSight.GetObserverEye(pc, out eyePos, out forward);

            bool isVisible = LineOfSight.Evaluate(eyePos, entity, category);

            VisibilityState state = VisibilityManager.GetOrCreateEntry(target.Identifier, entity.Identifier);

            if (!isVisible)
            {
                // Entity is occluded but EndSpawn was reached: schedule immediate despawn
                state.IsVisible = false;
                state.IsSpawned = true;
                state.DespawnPending = true;
                state.DespawnStartTime = Time.time - Configuration.DespawnDelaySeconds;
            }
            else
            {
                state.IsVisible = true;
                state.IsSpawned = true;
                state.LastSeenTime = Time.time;
                state.DespawnPending = false;
            }

            return true;
        }
    }
}
