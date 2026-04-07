using System;
using System.Linq;
using Dalamud.Game.ClientState.Party;
using Ffxiv2Mqtt.Services;

namespace Ffxiv2Mqtt.Topics.Data;

/// <summary>
/// Publishes the total party count including cross-world party members.
/// Topic: CrossRealmParty/Count
///
/// This extends the existing Party topic by also counting cross-realm members
/// that are not visible in the regular IPartyList when in a cross-world party.
/// </summary>
internal class CrossRealmParty : Topic, IDisposable
{
    protected override string TopicPath => "CrossRealmParty/Count";
    protected override bool Retained    => true;

    private int lastCount = -1;

    public CrossRealmParty()
    {
        Service.Framework.Update += OnFrameworkUpdate;
    }

    private void OnFrameworkUpdate(Dalamud.Plugin.Services.IFramework framework)
    {
        var crossRealmGroups = Service.PartyList.GetPartyMembers()
            .OfType<IPartyMember>()
            .Count();

        // Also check cross-realm via the native party list count
        var totalCount = Service.PartyList.Count > 0
            ? Service.PartyList.Count
            : crossRealmGroups;

        if (totalCount == lastCount) return;
        lastCount = totalCount;

        Publish(totalCount.ToString());
    }

    public void Dispose()
    {
        Service.Framework.Update -= OnFrameworkUpdate;
    }
}