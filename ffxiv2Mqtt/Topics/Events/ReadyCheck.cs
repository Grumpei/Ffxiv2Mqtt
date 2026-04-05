using System;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace Ffxiv2Mqtt.Topics.Events;

// Publishes MQTT messages when a Ready Check begins or ends.
// Uses Dalamud's IAddonLifecycle to detect when the game's
// "_ReadyCheck" UI addon is opened (Begin) or closed (End).
//
// MQTT topic: ffxiv/Event/ReadyCheck
// Payloads:   "Begin"  - a ready check was initiated
//             "End"    - the ready check window was closed
internal sealed class ReadyCheck : Topic, IDisposable
{
    protected override string TopicPath => "Event/ReadyCheck";
    protected override bool Retained => false;

    public ReadyCheck()
    {
        Service.AddonLifecycle.RegisterListener(
            AddonEvent.PostSetup, "_ReadyCheck", OnReadyCheckBegin);

        Service.AddonLifecycle.RegisterListener(
            AddonEvent.PreFinalize, "_ReadyCheck", OnReadyCheckEnd);
    }

    private void OnReadyCheckBegin(AddonEvent type, AddonArgs args)
        => Publish("Begin");

    private void OnReadyCheckEnd(AddonEvent type, AddonArgs args)
        => Publish("End");

    public void Dispose()
    {
        Service.AddonLifecycle.UnregisterListener(OnReadyCheckBegin);
        Service.AddonLifecycle.UnregisterListener(OnReadyCheckEnd);
    }
}
