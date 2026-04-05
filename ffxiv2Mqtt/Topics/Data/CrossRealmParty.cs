using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace Ffxiv2Mqtt.Topics.Data;

internal unsafe class CrossRealmParty : Topic, IDisposable
{
    private int crossRealmCount;

    protected override string TopicPath => "CrossRealmParty";
    protected override bool Retained => false;

    public CrossRealmParty()
    {
        Service.Framework.Update += FrameworkUpdate;
    }

    private void FrameworkUpdate(IFramework framework)
    {
        var proxy = InfoProxyCrossRealm.Instance();
        if (proxy == null)
            return;

        // GetPartyMemberCount() returns the total number of members
        // across all cross-realm groups (0-8 for a full party).
        var count = (int)InfoProxyCrossRealm.GetPartyMemberCount();

        if (count == crossRealmCount)
            return;

        crossRealmCount = count;
        Publish($"{TopicPath}/Count", crossRealmCount.ToString());
    }

    public void Dispose() { Service.Framework.Update -= FrameworkUpdate; }
}
