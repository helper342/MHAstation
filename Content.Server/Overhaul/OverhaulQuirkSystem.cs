using Content.Server.Body.Components;
using Content.Shared.Body.Components;
using Content.Shared.Damage;
using Content.Shared.Interaction;
using Robust.Server.GameObjects;
using Robust.Shared.Audio;
using Robust.Shared.Random;
using Robust.Shared.Timing;
using Robust.Shared.GameObjects;
using Robust.Server.GameObjects;
using Content.Shared.Overhaul;
using System.Numerics;
using Robust.Shared.Map;
using Content.Server.Popups;
using Content.Shared.Maps;
using Robust.Shared.Log;
using Content.Server.Actions;
using Content.Shared.Actions;
using Content.Shared.Actions.Components;

namespace Content.Server.Overhaul
{
    public sealed class OverhaulQuirkSystem : EntitySystem
    {
        private static readonly ISawmill _sawmill = Logger.GetSawmill("overhaul");
        [Dependency] private readonly IEntityManager _entityManager = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IRobustRandom _random = default!;
    [Dependency] private readonly SharedTransformSystem _transformSystem = default!;
    [Dependency] private readonly ActionContainerSystem _actionContainer = default!;

        private const int AbilityCooldownSeconds = 5;
        private const int DamageBrute = 20;
        private const int DamagePiercing = 15;
        private const int HealAmount = 30;
        private const int CorpseHealAmount = 50;
        private const int BuffDurationSeconds = 30;


        public override void Initialize()
        {
            base.Initialize();
            SubscribeLocalEvent<OverhaulQuirkComponent, OverhaulQuirkActionEvent>(OnOverhaulAction);
            SubscribeLocalEvent<OverhaulQuirkComponent, InteractUsingEvent>(OnInteractUsing);

            // Grant/remove action on component add/remove
            SubscribeLocalEvent<OverhaulQuirkComponent, ComponentStartup>(OnComponentStartup);
            SubscribeLocalEvent<OverhaulQuirkComponent, ComponentShutdown>(OnComponentShutdown);
        }

        private void OnComponentStartup(EntityUid uid, OverhaulQuirkComponent component, ComponentStartup args)
        {
            // Grant the Overhaul action to the entity so it appears in the player's action bar
            if (component.ActionEntity != null)
                return;

            EntityUid? actionId = component.ActionEntity;

            // Prefer the ActionContainerSystem to ensure / spawn the action and attach it correctly.
            if (_actionContainer.EnsureAction(uid, ref actionId, out var actComp, "OverhaulQuirkAction"))
            {
                component.ActionEntity = actionId;
                if (actComp != null)
                    actComp.AttachedEntity = uid;
                return;
            }

            // Fallback: spawn manually if EnsureAction failed for some reason
            var created = _entityManager.SpawnEntity("OverhaulQuirkAction", Transform(uid).Coordinates);
            component.ActionEntity = created;

            if (_entityManager.TryGetComponent<ActionComponent>(created, out var actComp2))
            {
                actComp2.AttachedEntity = uid;
            }
        }

        private void OnComponentShutdown(EntityUid uid, OverhaulQuirkComponent component, ComponentShutdown args)
        {
            if (component.ActionEntity != null && _entityManager.EntityExists(component.ActionEntity.Value))
            {
                _entityManager.DeleteEntity(component.ActionEntity.Value);
                component.ActionEntity = null;
            }
        }

        /// <summary>
        /// Triggered when an entity with the quirk interacts using an item or action.
        /// </summary>
        private void OnInteractUsing(EntityUid uid, OverhaulQuirkComponent component, InteractUsingEvent args)
        {
            _sawmill.Info("OnInteractUsing triggered");

            var target = args.Target;
            var user = args.User;
            var curTime = _timing.CurTime;
            if (curTime < component.LastAbilityUse + TimeSpan.FromSeconds(AbilityCooldownSeconds))
                return;
            component.LastAbilityUse = curTime;

            // Wall/Tile Reconstruction using WallSolid sample
            // If the target is a grid, treat as tile interaction (no MapGridComponent needed)
            // You may want to check for grid type or use a different mechanic here
            // For now, just run the tile logic if the entity is not a mob or item
            if (!_entityManager.TryGetComponent<BodyComponent>(target, out var body))
            {
                if (_random.Prob(0.5f))
                    CloneTile(target);
                else
                    ReplaceTile(target);
                return;
            }

            // Player/corpse interaction
            var isDeadProp = body.GetType().GetProperty("IsDead")?.GetValue(body);
            if ((isDeadProp as bool?) ?? false)
            {
                HealOrBuffSelf(user, component);
                return;
            }

            if (_random.Prob(0.5f))
                DamageTarget(target, user);
            else
                HealTarget(target, user);
            return;
        }

        /// <summary>
        /// Handles the actual quirk action when triggered.
        /// This method differentiates between self-clicks and clicking on other entities.
        /// </summary>
        private void OnOverhaulAction(EntityUid uid, OverhaulQuirkComponent component, OverhaulQuirkActionEvent args)
        {
            _sawmill.Info($"OnOverhaulAction triggered by {args.Performer}");

            // The action event is a WorldTargetActionEvent: args.Target is EntityCoordinates, args.Entity is the optional EntityUid
            var entityTarget = args.Entity;

            // If the performer clicked themself (entity target equals performer)
            if (entityTarget.HasValue && entityTarget.Value == args.Performer)
            {
                if (component.FusionStacks > 0)
                {
                    // Deploy queued structures based on FusionStacks count
                    DeployWallsAhead(uid, component);
                    // Reset the queued stacks after deployment
                    component.FusionStacks = 0;
                }
                else
                {
                    // Perform self-heal action if no fusion stacks exist
                    HealSelf(uid, component);
                }

                return;
            }

            // If there is an entity target, affect that entity
            if (entityTarget.HasValue)
            {
                AffectTarget(uid, component, entityTarget.Value);
                return;
            }

            // Otherwise this is a world/tile target (EntityCoordinates). Handle world targeting here.
            var coords = args.Target;
            var mapCoords = _transformSystem.ToMapCoordinates(coords);

            // Example: spawn a wall or replace the tile at the targeted coordinates
            if (_random.Prob(0.5f))
            {
                _entityManager.SpawnEntity("WallSolid", mapCoords);
            }
            else
            {
                _entityManager.SpawnEntity("KitchenSpike", mapCoords);
            }
        }
        /// <summary>
        /// Deploys walls or spikes ahead of the performing entity based on its facing direction
        /// and the count of queued fusion stacks.
        /// </summary>
        private void DeployWallsAhead(EntityUid uid, OverhaulQuirkComponent component)
        {
            if (!_entityManager.TryGetComponent(uid, out TransformComponent? xform) || xform == null)
            {
                _sawmill.Warning("No transform component for deployment.");
                return;
            }
            var worldPos = _transformSystem.GetWorldPosition(xform);
            var rotation = _transformSystem.GetWorldRotation(xform);
            // These should not be nullable, so no check needed
            var forwardVec = rotation.ToWorldVec();
            const float deployDistance = 1.5f;
            for (int i = 1; i <= component.FusionStacks; i++)
            {
                var prototype = (i % 2 == 1) ? "WallSolid" : "KitchenSpike";
                var spawnOffset = forwardVec * (deployDistance * i);
                var spawnPos = worldPos + spawnOffset;
                var coordinates = new MapCoordinates(spawnPos, xform.MapID);
                _entityManager.SpawnEntity(prototype, coordinates);
                _sawmill.Info($"Spawned {prototype} at {spawnPos}");
            }
        }

        /// <summary>
        /// Heal self when no FusionStacks are queued.
        /// </summary>
        private void HealSelf(EntityUid uid, OverhaulQuirkComponent component)
        {
            // For demonstration: log healing and implement healing logic here
            _sawmill.Info("Healing self.");
            // ... Implement healing logic, e.g., DamageableSystem.Heal
        }

        /// <summary>
        /// Affect the target entity with damage or other effects.
        /// </summary>
        private void AffectTarget(EntityUid uid, OverhaulQuirkComponent component, EntityUid target)
        {
            // For demonstration: log target affected and implement effect logic here
            _sawmill.Info($"Affecting target {target} with quirk action.");
            // ... Implement target interaction logic, such as applying damage or status effects
        }

        /// <summary>
        /// Spawns an entity based on a prototype and map coordinates. This is a wrapper over the standard entity spawn call.
        /// </summary>
        private void SpawnEntity(string prototype, MapCoordinates coordinates)
        {
            // Actual spawn call; assume EntityManager or similar is available
            // This is pseudocode; replace with actual entity spawn logic
            _entityManager.SpawnEntity(prototype, coordinates);
        }

        private void CloneTile(EntityUid tile)
        {
            var coords = _entityManager.GetComponent<TransformComponent>(tile).Coordinates;
            _entityManager.SpawnEntity("WallSolid", coords);
        }


        private void ReplaceTile(EntityUid tile)
        {
            var xform = _entityManager.GetComponent<TransformComponent>(tile);
            var coords = xform.Coordinates;
            _entityManager.DeleteEntity(tile);
            // Use the transform's MapID for MapCoordinates
            _entityManager.SpawnEntity("WallSolid", new MapCoordinates(coords.Position, xform.MapID));
        }


        private void DamageTarget(EntityUid target, EntityUid user)
        {
            var damage = new DamageSpecifier();
            damage.DamageDict.Add("Brute", DamageBrute);
            damage.DamageDict.Add("Piercing", DamagePiercing);
            var dmgSystem = _entityManager.System<DamageableSystem>();
            dmgSystem.TryChangeDamage(target, damage, true, false);
        }


        private void HealTarget(EntityUid target, EntityUid user)
        {
            var heal = new DamageSpecifier();
            heal.DamageDict.Add("Healing", HealAmount);
            var dmgSystem = _entityManager.System<DamageableSystem>();
            dmgSystem.TryChangeDamage(target, heal, true, false);
        }


        private void HealOrBuffSelf(EntityUid user, OverhaulQuirkComponent comp)
        {
            var heal = new DamageSpecifier();
            heal.DamageDict.Add("Healing", CorpseHealAmount);
            var dmgSystem = _entityManager.System<DamageableSystem>();
            dmgSystem.TryChangeDamage(user, heal, true, false);
            comp.HasCorpseBuff = true;
            comp.FusionStacks += 1;
            if (!_entityManager.HasComponent<OverhaulBuffComponent>(user))
            {
                _entityManager.AddComponent<OverhaulBuffComponent>(user);
                Timer.Spawn(TimeSpan.FromSeconds(BuffDurationSeconds), () =>
                {
                    if (_entityManager.EntityExists(user))
                        _entityManager.RemoveComponent<OverhaulBuffComponent>(user);
                    comp.HasCorpseBuff = false;
                });
            }
        }
    }
}
