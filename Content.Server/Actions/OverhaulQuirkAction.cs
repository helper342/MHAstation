using Content.Shared.Actions;
using Content.Shared.Overhaul;

namespace Content.Server.Actions
{
        [DataDefinition]
        public sealed partial class OverhaulQuirkAction : InstantActionEvent
    {
        // This action itself does not handle targeting.
        // The system (OverhaulQuirkSystem) will subscribe to AfterInteractEvent or similar.
    }
}
