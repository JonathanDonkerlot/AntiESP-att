using System;
using MelonLoader;
using UnityEngine;
using Alta.Networking;

namespace ServerAntiEsp
{
    /// <summary>
    /// Performs authoritative server-side line-of-sight and occlusion raycasts.
    /// Utilizes comprehensive multi-layer obstacle masking, multi-hit non-alloc raycasts,
    /// bidirectional penetration checks, and view-relative adaptive geometry sampling.
    /// </summary>
    public static class LineOfSight
    {
        // Complete registry of environmental collision layers in A Township Tale
        private static readonly string[] ObstacleLayerNames = new string[]
        {
            "Default",          // Layer 0: Generic static/dynamic colliders, doors, gates, world structures
            "Terrain",          // Layer 8: Landscape heightmaps, hills, ground surface
            "Ground",           // Layer 10: Wooden floors, stone floors, bridges, docks, walkways, stairs
            "GroundOverlay",    // Layer 11: Decking, platform overlays, walkways
            "Wall",             // Layer 13: Wooden walls, stone walls, buildings, stockades, barricades
            "BlockTeleport",    // Layer 16: Boundary walls, invisible barrier colliders
            "TerrainWallOnly",  // Layer 19: Cliffs, rock walls, cave walls, mine boundaries
            "DefaultOnly",      // Layer 22: Building pillars, structural frames, beams
            "YuME_TileMap"      // Layer 29: Modular tilemap structures, dungeons, town architecture
        };

        private static int _occlusionMask = 0;
        private static bool _maskInitialized = false;

        // Pre-allocated sample point buffers to ensure zero allocations during visibility checks
        private static readonly Vector3[] _samplePoints = new Vector3[5];

        // Pre-allocated multi-hit raycast buffers (zero GC pressure in hot path)
        private static readonly RaycastHit[] _forwardHits = new RaycastHit[32];
        private static readonly RaycastHit[] _reverseHits = new RaycastHit[32];

        /// <summary>
        /// Retrieves the cached physics layer mask for all environmental world geometry.
        /// Covers Default, Terrain, Ground, GroundOverlay, Wall, BlockTeleport,
        /// TerrainWallOnly, DefaultOnly, and YuME_TileMap.
        /// </summary>
        public static int GetOcclusionMask()
        {
            if (!_maskInitialized)
            {
                int mask = 0;
                string resolvedLayers = "";

                for (int i = 0; i < ObstacleLayerNames.Length; i++)
                {
                    string layerName = ObstacleLayerNames[i];
                    int layerIndex = LayerMask.NameToLayer(layerName);
                    if (layerIndex >= 0)
                    {
                        mask |= (1 << layerIndex);
                        resolvedLayers += (resolvedLayers.Length > 0 ? ", " : "") + layerName + "(" + layerIndex + ")";
                    }
                }

                if (mask == 0)
                {
                    mask = 1; // Fallback to Default layer (0)
                }

                _occlusionMask = mask;
                _maskInitialized = true;

                // Ensure single-sided mesh colliders (e.g. wall panels, roof underside) block rays from both sides
                if (Configuration.HitBackfaces)
                {
                    try
                    {
                        Physics.queriesHitBackfaces = true;
                    }
                    catch (Exception ex)
                    {
                        MelonLogger.Warning("[AntiESP] Could not set Physics.queriesHitBackfaces: " + ex.Message);
                    }
                }

                MelonLogger.Msg("[AntiESP] Active Occlusion Mask: " + mask + " [" + resolvedLayers + "]");
            }
            return _occlusionMask;
        }

        public static void ResetMaskCache()
        {
            _maskInitialized = false;
            _occlusionMask = 0;
        }

        /// <summary>
        /// Obtains the observer player's authoritative eye position and view forward vector.
        /// </summary>
        public static void GetObserverEye(PlayerController controller, out Vector3 eyePos, out Vector3 forward)
        {
            if (controller == null)
            {
                eyePos = Vector3.zero;
                forward = Vector3.forward;
                return;
            }

            if (controller.Head != null)
            {
                eyePos = controller.Head.position;
                forward = controller.Head.forward;
            }
            else
            {
                eyePos = controller.transform.position + new Vector3(0f, 1.6f, 0f);
                forward = controller.transform.forward;
            }
        }

        /// <summary>
        /// Evaluates line-of-sight from an observer eye to a target entity using
        /// adaptive multi-point sampling based on the entity's category and view direction.
        /// </summary>
        public static bool Evaluate(Vector3 eyePos, NetworkEntity target, EntityCategory category)
        {
            if (target == null)
            {
                return true;
            }

            Vector3 targetBasePos = target.transform.position;
            Vector3 toTarget = targetBasePos - eyePos;
            float distSqr = toTarget.sqrMagnitude;

            // 1. Maximum visibility cutoff
            float maxDist = Configuration.MaxVisibilityDistance;
            if (distSqr > maxDist * maxDist)
            {
                return false;
            }

            int layerMask = GetOcclusionMask();

            // 2. Personal bubble proximity check
            // Even in close range, ensure a solid wall isn't separating the player and entity
            float bubbleRadius = Configuration.PersonalBubbleRadius;
            if (distSqr <= bubbleRadius * bubbleRadius)
            {
                Vector3 centerTarget = targetBasePos + new Vector3(0f, 0.15f, 0f);
                if (IsPointDirectlyVisible(eyePos, centerTarget, layerMask, target))
                {
                    return true;
                }
            }

            // 3. Populate adaptive sample points based on category and view direction
            int pointCount = PopulateSamplePoints(target, eyePos, targetBasePos, category);

            // 4. Test sample points: if ANY legitimate point is visible, the entity is visible
            for (int i = 0; i < pointCount; i++)
            {
                if (IsPointDirectlyVisible(eyePos, _samplePoints[i], layerMask, target))
                {
                    if (Configuration.EnableRaycastDiagnostics)
                    {
                        MelonLogger.Msg(string.Format("[AntiESP-Ray] VISIBLE: Point #{0} ({1:F2},{2:F2},{3:F2}) unobstructed to Entity '{4}' (Cat={5})",
                            i, _samplePoints[i].x, _samplePoints[i].y, _samplePoints[i].z, target.SafeName, category));
                    }
                    return true;
                }
            }

            if (Configuration.EnableRaycastDiagnostics)
            {
                MelonLogger.Msg(string.Format("[AntiESP-Ray] OCCLUDED: All {0} sample points blocked for Entity '{1}' (Cat={2})",
                    pointCount, target.SafeName, category));
            }

            // All sample points occluded
            return false;
        }

        /// <summary>
        /// Populates the pre-allocated sample buffer with appropriate target points.
        /// Uses view-relative perpendicular offsets to prevent sample points from penetrating adjacent walls.
        /// </summary>
        private static int PopulateSamplePoints(NetworkEntity target, Vector3 eyePos, Vector3 basePos, EntityCategory category)
        {
            // Compute horizontal view direction and perpendicular lateral vector
            Vector3 toTarget = basePos - eyePos;
            Vector3 horizDir = new Vector3(toTarget.x, 0f, toTarget.z);
            float horizDist = horizDir.magnitude;

            Vector3 viewRight;
            if (horizDist > 0.05f)
            {
                horizDir /= horizDist;
                // Perpendicular to view line in horizontal plane
                viewRight = new Vector3(-horizDir.z, 0f, horizDir.x);
            }
            else
            {
                viewRight = Vector3.right;
            }

            switch (category)
            {
                case EntityCategory.LooseItem:
                    // Stones, sticks, ores, weapons lying on the ground:
                    // Small physical bounds (~3-12 cm). Points stay strictly along vertical centerline.
                    // ZERO horizontal offset to eliminate any possibility of poking through thin walls or fences.
                    _samplePoints[0] = basePos + new Vector3(0f, 0.04f, 0f); // Base center
                    _samplePoints[1] = basePos + new Vector3(0f, 0.10f, 0f); // Top surface
                    return 2;

                case EntityCategory.OtherPlayer:
                case EntityCategory.Creature:
                    // Humanoids and animals (Spriggulls, Wyrms, players):
                    _samplePoints[0] = basePos + new Vector3(0f, 0.30f, 0f);                  // Feet / lower legs
                    _samplePoints[1] = basePos + new Vector3(0f, 0.95f, 0f);                  // Center torso
                    _samplePoints[2] = basePos + new Vector3(0f, 1.50f, 0f);                  // Head height
                    // Lateral points: strictly view-perpendicular and conservative (15cm) so they cannot poke through walls behind the entity
                    _samplePoints[3] = basePos + new Vector3(0f, 0.95f, 0f) + viewRight * 0.15f; // Right flank
                    _samplePoints[4] = basePos + new Vector3(0f, 0.95f, 0f) - viewRight * 0.15f; // Left flank
                    return 5;

                case EntityCategory.MineableResource:
                default:
                    // Boulders, crystals, breakable boxes:
                    // Use collider bounds if available, clamped to avoid protruding outside the geometry
                    Collider col = target.GetComponentInChildren<Collider>();
                    if (col != null)
                    {
                        Vector3 center = col.bounds.center;
                        Vector3 extents = col.bounds.extents;

                        _samplePoints[0] = center;
                        _samplePoints[1] = center + new Vector3(0f, extents.y * 0.5f, 0f);
                        _samplePoints[2] = center - new Vector3(0f, extents.y * 0.5f, 0f);
                        float lateralRadius = Mathf.Min(extents.x, extents.z) * 0.5f;
                        _samplePoints[3] = center + viewRight * lateralRadius;
                        _samplePoints[4] = center - viewRight * lateralRadius;
                        return 5;
                    }
                    else
                    {
                        _samplePoints[0] = basePos + new Vector3(0f, 0.25f, 0f);
                        _samplePoints[1] = basePos + new Vector3(0f, 0.75f, 0f);
                        _samplePoints[2] = basePos + new Vector3(0f, 1.25f, 0f);
                        _samplePoints[3] = basePos + new Vector3(0f, 0.75f, 0f) + viewRight * 0.25f;
                        _samplePoints[4] = basePos + new Vector3(0f, 0.75f, 0f) - viewRight * 0.25f;
                        return 5;
                    }
            }
        }

        /// <summary>
        /// Casts non-alloc multi-hit raycasts between eye and target point.
        /// Performs bidirectional checks (forward and reverse) to catch inverted normals,
        /// backfaces of single-sided mesh colliders, and rays starting within bounds.
        /// </summary>
        private static bool IsPointDirectlyVisible(Vector3 eyePos, Vector3 targetPoint, int layerMask, NetworkEntity target)
        {
            Diagnostics.RecordRaycast();

            Vector3 rayVec = targetPoint - eyePos;
            float rayDist = rayVec.magnitude;

            if (rayDist < 0.1f)
            {
                return true;
            }

            Vector3 rayDir = rayVec / rayDist;

            // Start ray slightly ahead of eye to avoid self-intersection with observer rig
            Vector3 rayOrigin = eyePos + rayDir * 0.08f;
            float castLength = rayDist - 0.12f;

            if (castLength <= 0f)
            {
                return true;
            }

            // 1. FORWARD RAYCAST: Observer -> Target
            int hitCount = Physics.RaycastNonAlloc(
                rayOrigin, rayDir, _forwardHits, castLength, layerMask, QueryTriggerInteraction.Ignore);

            for (int i = 0; i < hitCount; i++)
            {
                RaycastHit hit = _forwardHits[i];
                Collider col = hit.collider;
                if (col == null || col.isTrigger)
                {
                    continue;
                }

                // If the collider belongs to the target entity, it does not block itself
                if (col.transform != null && col.transform.IsChildOf(target.transform))
                {
                    continue;
                }

                // Hit a solid environmental obstacle!
                if (Configuration.EnableRaycastDiagnostics)
                {
                    LogRaycastHit("FORWARD", eyePos, targetPoint, rayDist, hit, target);
                }

                return false; // Obstructed
            }

            // 2. REVERSE RAYCAST: Target -> Observer (Bidirectional Verification)
            // Catches single-sided wall colliders whose normals face away from the observer,
            // or instances where the observer's eye is adjacent to or within a concave boundary.
            if (Configuration.UseBidirectionalRaycasts)
            {
                Vector3 revOrigin = targetPoint - rayDir * 0.08f;
                Vector3 revDir = -rayDir;

                int revHitCount = Physics.RaycastNonAlloc(
                    revOrigin, revDir, _reverseHits, castLength, layerMask, QueryTriggerInteraction.Ignore);

                for (int i = 0; i < revHitCount; i++)
                {
                    RaycastHit revHit = _reverseHits[i];
                    Collider col = revHit.collider;
                    if (col == null || col.isTrigger)
                    {
                        continue;
                    }

                    if (col.transform != null && col.transform.IsChildOf(target.transform))
                    {
                        continue;
                    }

                    if (Configuration.EnableRaycastDiagnostics)
                    {
                        LogRaycastHit("REVERSE", targetPoint, eyePos, rayDist, revHit, target);
                    }

                    return false; // Obstructed by backface or internal surface
                }
            }

            // No environmental obstacle detected along path
            return true;
        }

        private static void LogRaycastHit(string direction, Vector3 origin, Vector3 destination, float totalDist, RaycastHit hit, NetworkEntity target)
        {
            Collider col = hit.collider;
            GameObject go = col != null ? col.gameObject : null;
            string objName = go != null ? go.name : "null";
            int layerIndex = go != null ? go.layer : -1;
            string layerName = layerIndex >= 0 ? LayerMask.LayerToName(layerIndex) : "Unknown";
            string colType = col != null ? col.GetType().Name : "null";
            string colName = col != null ? col.name : "null";
            bool isTrigger = col != null && col.isTrigger;
            bool isTargetChild = col != null && col.transform != null && col.transform.IsChildOf(target.transform);

            MelonLogger.Msg(string.Format(
                "[AntiESP-RayHit] [{0}] Dist={1:F2}m / HitDist={2:F2}m | Obj='{3}' | Layer={4} ('{5}') | ColType={6} ('{7}') | Trigger={8} | IsTarget={9} -> OBSTRUCTED",
                direction, totalDist, hit.distance, objName, layerIndex, layerName, colType, colName, isTrigger, isTargetChild));
        }
    }
}
