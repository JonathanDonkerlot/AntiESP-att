using System;
using UnityEngine;
using Alta.Networking;

namespace ServerAntiEsp
{
    public enum EntityCategory
    {
        Exempt,             // Never filtered: self player, docked items, parented items, embedded items
        OtherPlayer,        // Another connected player's avatar
        Creature,           // Animals, monsters, AI agents (Spriggulls, Wyrms, etc.)
        LooseItem,          // Loose ground items (stones, sticks, ores, weapons, food)
        MineableResource    // Destructible environmental objects (boulders, crystals, breakable boxes)
    }

    /// <summary>
    /// Inspects and classifies NetworkEntity instances to determine
    /// replication filtering rules and adaptive line-of-sight bounds.
    /// </summary>
    public static class EntityClassifier
    {
        /// <summary>
        /// Classifies a target entity relative to an observing player.
        /// Identifies exact category and whether filtering applies.
        /// </summary>
        public static EntityCategory Classify(NetworkEntity entity, Player observer)
        {
            if (entity == null || entity.IsBeingDestroyed)
            {
                return EntityCategory.Exempt;
            }

            // 1. Structural exemptions: parented entities follow their parent transform
            if (entity.Parent != null)
            {
                return EntityCategory.Exempt;
            }

            // 2. Embedded entities: attached to chunking containers/structures
            if (entity.ChunkingParent != null)
            {
                return EntityCategory.Exempt;
            }

            // 3. Player avatars
            PlayerController pc = entity.GetComponent<PlayerController>();
            if (pc != null)
            {
                // Always replicate the observer's own avatar and controller
                if (observer != null && (pc.NetworkPlayer == observer || pc == observer.PlayerController))
                {
                    return EntityCategory.Exempt;
                }
                return EntityCategory.OtherPlayer;
            }

            // 4. Pickup items (stones, sticks, weapons, tools, food, ores)
            Pickup pickup = entity.CommonPickup;
            if (pickup != null)
            {
                // Docked items (in backpack slots, chests, weapon holsters, crafting benches)
                if (pickup.IsDocked)
                {
                    return EntityCategory.Exempt;
                }

                // Sub-components of assembled/crafted items (blades, guards, attachments)
                if (pickup.IsCraftedChild)
                {
                    return EntityCategory.Exempt;
                }

                // Items currently held by an interactor/hand
                Interactable interactable = entity.CommonInteractable;
                if ((interactable != null && interactable.IsInteractedWith) || pickup.IsInteractedWith)
                {
                    // If held by the observer, never hide it
                    if (observer != null && interactable != null && interactable.LastPlayerInteractor == observer.PlayerController)
                    {
                        return EntityCategory.Exempt;
                    }
                    // If held by another player, it is moving with that player
                    return EntityCategory.LooseItem;
                }

                // Un-docked, un-parented ground item: Stones, Sticks, loose rocks, dropped gear
                return EntityCategory.LooseItem;
            }

            // 5. Destructibles, Creatures, and Resource Nodes
            HealthObject ho = entity.GetComponent<HealthObject>();
            if (ho != null)
            {
                // Check if creature / AI agent (Spriggull, Wyrm, Turabada, etc.)
                if (IsCreature(entity))
                {
                    return EntityCategory.Creature;
                }

                // Destructibles: Boulders, ore nodes, breakable boxes, trees
                return EntityCategory.MineableResource;
            }

            // Entities without health, pickups, or player components are static or non-sensitive
            return EntityCategory.Exempt;
        }

        /// <summary>
        /// Checks whether the entity is subject to line-of-sight replication filtering
        /// based on active configuration settings.
        /// </summary>
        public static bool ShouldFilter(NetworkEntity entity, Player observer, out EntityCategory category)
        {
            category = Classify(entity, observer);

            if (category == EntityCategory.Exempt)
            {
                return false;
            }

            switch (category)
            {
                case EntityCategory.OtherPlayer:
                    return Configuration.FilterPlayers;

                case EntityCategory.Creature:
                    return Configuration.FilterCreatures;

                case EntityCategory.LooseItem:
                    return Configuration.FilterLooseItems;

                case EntityCategory.MineableResource:
                    return Configuration.FilterMineables;

                default:
                    return false;
            }
        }

        private static bool IsCreature(NetworkEntity entity)
        {
            // Check for intelligence / agent components in Township
            var spriggull = entity.GetComponent("Spriggull");
            if (spriggull != null) return true;

            var agent = entity.GetComponent("Agent`1");
            if (agent != null) return true;

            var brain = entity.GetComponent("Brain");
            if (brain != null) return true;

            // Name-based fallback for known Township mob prefabs
            string name = entity.name;
            if (name != null)
            {
                if (name.IndexOf("Spriggull", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Wyrm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Turabada", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Gotera", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("Creature", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
