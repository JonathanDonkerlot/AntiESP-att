using System;
using System.Collections.Generic;
using UnityEngine;
using Alta.Networking;
using Alta.Chunks;

namespace ServerAntiEsp
{
    public class VisibilityState
    {
        public bool IsVisible;
        public float LastCheckTime;
        public float LastSeenTime;
        public bool DespawnPending;
        public float DespawnStartTime;
        public bool IsSpawned;
    }

    /// <summary>
    /// Manages independent per-player visibility states, distance-based scheduling,
    /// and despawn hysteresis to eliminate flickering.
    /// </summary>
    public static class VisibilityManager
    {
        private struct PendingAction
        {
            public Chunker Chunker;
            public Player Observer;
            public NetworkEntity Entity;

            public PendingAction(Chunker chunker, Player observer, NetworkEntity entity)
            {
                Chunker = chunker;
                Observer = observer;
                Entity = entity;
            }
        }

        // Map: Observer Player ID -> (Entity ID -> VisibilityState)
        private static readonly Dictionary<uint, Dictionary<uint, VisibilityState>> _playerStates
            = new Dictionary<uint, Dictionary<uint, VisibilityState>>();

        // Pre-allocated action queues to prevent GC allocations
        private static readonly List<PendingAction> _pendingSpawns = new List<PendingAction>();
        private static readonly List<PendingAction> _pendingDespawns = new List<PendingAction>();

        // Reusable cleanup buffers
        private static readonly List<uint> _stalePlayers = new List<uint>();
        private static readonly List<uint> _staleEntities = new List<uint>();

        private static float _lastCleanupTime = 0f;

        /// <summary>
        /// Executes a single full visibility sweep across all players and loaded chunks.
        /// </summary>
        public static void ExecuteVisibilityCycle(float currentTime)
        {
            HashSet<Alta.Networking.Scripts.Player.IPlayer> allPlayers = Player.AllPlayers;
            if (allPlayers == null || allPlayers.Count == 0)
            {
                Diagnostics.ConnectedPlayerCount = 0;
                Diagnostics.TrackedEntityCount = 0;
                Diagnostics.ActiveOcclusions = 0;
                return;
            }

            _pendingSpawns.Clear();
            _pendingDespawns.Clear();

            int totalEntitiesChecked = 0;
            int totalOccluded = 0;
            int activePlayers = 0;

            foreach (Alta.Networking.Scripts.Player.IPlayer iPlayer in allPlayers)
            {
                Player observer = iPlayer as Player;
                if (observer == null || observer.ConnectionToRemotePlayer == null)
                {
                    continue;
                }

                PlayerController obsController = observer.PlayerController;
                if (obsController == null)
                {
                    continue;
                }

                activePlayers++;
                uint observerId = observer.Identifier;
                Dictionary<uint, VisibilityState> entityMap = GetOrCreatePlayerMap(observerId);

                Vector3 eyePos;
                Vector3 forward;
                LineOfSight.GetObserverEye(obsController, out eyePos, out forward);

                HashSet<Chunk> loadedChunks = observer.LoadedChunks;
                if (loadedChunks == null || loadedChunks.Count == 0)
                {
                    continue;
                }

                float closeRadiusSqr = Configuration.CloseRangeRadius * Configuration.CloseRangeRadius;
                float midRadiusSqr = Configuration.MidRangeRadius * Configuration.MidRangeRadius;
                float despawnDelay = Configuration.DespawnDelaySeconds;

                foreach (Chunk chunk in loadedChunks)
                {
                    if (chunk == null) continue;

                    ChunkEntityManager entityManager = chunk.Entities;
                    if (entityManager == null) continue;

                    IEnumerable<NetworkEntity> entities = entityManager.Entities;
                    if (entities == null) continue;

                    foreach (NetworkEntity targetEntity in entities)
                    {
                        if (targetEntity == null || targetEntity.IsBeingDestroyed)
                        {
                            continue;
                        }

                        EntityCategory category;
                        if (!EntityClassifier.ShouldFilter(targetEntity, observer, out category))
                        {
                            continue;
                        }

                        totalEntitiesChecked++;

                        uint entityId = targetEntity.Identifier;
                        VisibilityState state;
                        if (!entityMap.TryGetValue(entityId, out state))
                        {
                            state = new VisibilityState();
                            entityMap[entityId] = state;
                        }

                        // Determine distance-based scheduling interval
                        Vector3 targetPos = targetEntity.transform.position;
                        float distSqr = (targetPos - eyePos).sqrMagnitude;

                        float checkInterval;
                        if (distSqr <= closeRadiusSqr)
                        {
                            checkInterval = Configuration.CloseRangeCheckInterval;
                        }
                        else if (distSqr <= midRadiusSqr)
                        {
                            checkInterval = Configuration.MidRangeCheckInterval;
                        }
                        else
                        {
                            checkInterval = Configuration.FarRangeCheckInterval;
                        }

                        // Throttle raycast checks if too frequent, unless a despawn countdown is active
                        if (!state.DespawnPending && (currentTime - state.LastCheckTime < checkInterval))
                        {
                            if (!state.IsVisible && state.IsSpawned) totalOccluded++;
                            continue;
                        }

                        state.LastCheckTime = currentTime;
                        Diagnostics.RecordVisibilityCheck();

                        IChunkedEntity chunked = targetEntity as IChunkedEntity;
                        if (chunked == null) continue;
                        Chunker chunker = chunked.Chunker;
                        if (chunker == null) continue;

                        bool isCurrentlySpawned = ReplicationManager.IsSpawnedFor(chunker, observer);
                        state.IsSpawned = isCurrentlySpawned;

                        // Authoritative Line-of-Sight Evaluation
                        bool isVisible = LineOfSight.Evaluate(eyePos, targetEntity, category);

                        if (isVisible)
                        {
                            // Entity is Visible
                            state.IsVisible = true;
                            state.LastSeenTime = currentTime;
                            state.DespawnPending = false;

                            if (!isCurrentlySpawned)
                            {
                                // Entity became visible: replicate immediately
                                _pendingSpawns.Add(new PendingAction(chunker, observer, targetEntity));
                            }
                        }
                        else
                        {
                            // Entity is Occluded
                            state.IsVisible = false;
                            totalOccluded++;

                            if (isCurrentlySpawned)
                            {
                                if (!state.DespawnPending)
                                {
                                    // Start hysteresis delay countdown
                                    state.DespawnPending = true;
                                    state.DespawnStartTime = currentTime;
                                }
                                else if (currentTime - state.DespawnStartTime >= despawnDelay)
                                {
                                    // Hysteresis expired: withhold replication and despawn entity
                                    _pendingDespawns.Add(new PendingAction(chunker, observer, targetEntity));
                                    state.DespawnPending = false;
                                    state.IsSpawned = false;
                                }
                            }
                        }
                    }
                }
            }

            // Execute queued spawns and despawns
            for (int i = 0; i < _pendingSpawns.Count; i++)
            {
                PendingAction a = _pendingSpawns[i];
                ReplicationManager.SpawnForObserver(a.Chunker, a.Observer, a.Entity);
            }

            for (int i = 0; i < _pendingDespawns.Count; i++)
            {
                PendingAction a = _pendingDespawns[i];
                ReplicationManager.DespawnForObserver(a.Chunker, a.Observer, a.Entity);
            }

            // Update telemetry metrics
            Diagnostics.ConnectedPlayerCount = activePlayers;
            Diagnostics.TrackedEntityCount = totalEntitiesChecked;
            Diagnostics.ActiveOcclusions = totalOccluded;

            // Periodic cleanup of stale references
            if (currentTime - _lastCleanupTime >= 10.0f)
            {
                _lastCleanupTime = currentTime;
                CleanupStaleEntries(currentTime);
            }
        }

        public static VisibilityState GetOrCreateEntry(uint playerId, uint entityId)
        {
            Dictionary<uint, VisibilityState> map = GetOrCreatePlayerMap(playerId);
            VisibilityState state;
            if (!map.TryGetValue(entityId, out state))
            {
                state = new VisibilityState();
                map[entityId] = state;
            }
            return state;
        }

        private static Dictionary<uint, VisibilityState> GetOrCreatePlayerMap(uint playerId)
        {
            Dictionary<uint, VisibilityState> map;
            if (!_playerStates.TryGetValue(playerId, out map))
            {
                map = new Dictionary<uint, VisibilityState>();
                _playerStates[playerId] = map;
            }
            return map;
        }

        private static void CleanupStaleEntries(float currentTime)
        {
            if (_playerStates.Count == 0) return;

            _stalePlayers.Clear();

            foreach (var kvp in _playerStates)
            {
                var entityMap = kvp.Value;
                if (entityMap.Count == 0)
                {
                    _stalePlayers.Add(kvp.Key);
                    continue;
                }

                _staleEntities.Clear();
                foreach (var entKvp in entityMap)
                {
                    if (currentTime - entKvp.Value.LastCheckTime > 45.0f)
                    {
                        _staleEntities.Add(entKvp.Key);
                    }
                }

                for (int i = 0; i < _staleEntities.Count; i++)
                {
                    entityMap.Remove(_staleEntities[i]);
                }
            }

            for (int i = 0; i < _stalePlayers.Count; i++)
            {
                _playerStates.Remove(_stalePlayers[i]);
            }
        }

        public static void Clear()
        {
            _playerStates.Clear();
            _pendingSpawns.Clear();
            _pendingDespawns.Clear();
            _stalePlayers.Clear();
            _staleEntities.Clear();
        }
    }
}
