using Robust.Shared.GameStates;
using Robust.Shared.Serialization;

namespace Content.Shared.Overhaul
{
    [RegisterComponent]
    public sealed partial class OverhaulQuirkComponent : Component
    {
        [ViewVariables] public bool HasCorpseBuff { get; set; }
        [ViewVariables] public int FusionStacks { get; set; } = 0;
    [ViewVariables] public TimeSpan LastAbilityUse { get; set; } = TimeSpan.Zero;

    // The action entity granted to this entity (so they get the Overhaul action in their UI).
    [ViewVariables] public EntityUid? ActionEntity { get; set; }
    }
}
