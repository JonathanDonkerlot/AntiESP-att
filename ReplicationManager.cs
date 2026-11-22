using System;
using MelonLoader;
using Alta.Networking;
using Alta.Chunks;

namespace ServerAntiEsp
{
    /// <summary>
    /// Gateway for authoritative server-side entity replication.
    /// Manages per-player spawning and despawning via ChunkerHelper while
    /// preventing re-entrancy and state corruption.
    /// </summary>
    public static class ReplicationManager
    {
        /// <summary>
        /// Re-entrancy guard flag. Set to true when ServerAntiESP itself invokes
        /// ChunkerHelper.SpawnFor, allowing the StartSpawn patch to let the spawn proceed.
        /// </summary>
        [ThreadStatic]
        public static bool BypassFilter = false;

        public static bool IsSpawnedFor(Chunker chunker, Player observer)
        {
            if (chunker == null || observer == null)
            {
                return false;
            }

            return chunker.SpawnedForPlayers.Contains(observer);
        }

        public static void SpawnForObserver(Chunker chunker, Player observer, NetworkEntity entity)
        {
            if (chunker == null || observer == null)
            {
                return;
            }

            // Verify not already spawned
            if (chunker.SpawnedForPlayers.Contains(observer))
            {
                return;
            }

            BypassFilter = true;
            try
            {
                ChunkerHelper.SpawnFor(chunker, observer);
                Diagnostics.RecordSpawn();

                if (Configuration.LogVisibilityTransitions)
                {
                    MelonLogger.Msg(string.Format("[AntiESP] REPLICATED: Entity {0} ({1}) spawned for Player {2}",
                        entity != null ? entity.Identifier : 0,
                        entity != null ? entity.SafeName : "unknown",
                        observer.Identifier));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(string.Format("[AntiESP] Error spawning entity {0} for player {1}: {2}",
                    entity != null ? entity.Identifier : 0, observer.Identifier, ex.Message));
            }
            finally
            {
                BypassFilter = false;
            }
        }

        public static void DespawnForObserver(Chunker chunker, Player observer, NetworkEntity entity)
        {
            if (chunker == null || observer == null)
            {
                return;
            }

            // Verify currently spawned
            if (!chunker.SpawnedForPlayers.Contains(observer))
            {
                return;
            }

            try
            {
                // DespawnFor invokes:
                // 1. IChunker.StartDespawn -> removes player from SpawnedForPlayers (closes replication gate)
                // 2. IChunker.EndDespawn   -> sends DestroyMessage (ID=21) to remove entity on client
                ChunkerHelper.DespawnFor(chunker, observer, false);
                Diagnostics.RecordDespawn();

                if (Configuration.LogVisibilityTransitions)
                {
                    MelonLogger.Msg(string.Format("[AntiESP] OBFUSCATED: Entity {0} ({1}) despawned from Player {2}",
                        entity != null ? entity.Identifier : 0,
                        entity != null ? entity.SafeName : "unknown",
                        observer.Identifier));
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error(string.Format("[AntiESP] Error despawning entity {0} for player {1}: {2}",
                    entity != null ? entity.Identifier : 0, observer.Identifier, ex.Message));
            }
        }
    }
}
