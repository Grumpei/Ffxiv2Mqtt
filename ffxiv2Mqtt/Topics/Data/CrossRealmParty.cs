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

        var count = (int)proxy->GetTotalMemberCount();

        if (count == crossRealmCount)
            return;

        crossRealmCount = count;
        Publish($"{TopicPath}/Count", crossRealmCount.ToString());
    }

    public void Dispose() { Service.Framework.Update -= FrameworkUpdate; }
}
