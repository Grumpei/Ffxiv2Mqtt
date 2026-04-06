using System;
using System.Linq;
using Dalamud.Game.Inventory;
using Ffxiv2Mqtt.Services;

namespace Ffxiv2Mqtt.Topics.Data.Player;

/// <summary>
/// Publishes inventory fullness state to MQTT.
/// Topic: Player/Inventory
///
/// Payload:
/// {
///   "FreeSlots": 12,
///   "TotalSlots": 140,
///   "IsFull": false
/// }
///
/// Only publishes when the free slot count changes.
/// Triggered by IGameInventory.InventoryChanged event.
/// </summary>
internal class PlayerInventory : Topic, IDisposable
{
    // The 4 main inventory bags = 35 slots each = 140 total
    private static readonly GameInventoryType[] InventoryBags =
    {
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    };

    private const int TotalSlots = 140;

    protected override string TopicPath => "Player/Inventory";
    protected override bool Retained => true;

    private int lastFreeSlots = -1;

    public PlayerInventory()
    {
        Service.GameInventory.InventoryChanged += OnInventoryChanged;
    }

    private void OnInventoryChanged(
        System.Collections.Generic.IReadOnlyCollection<Dalamud.Game.Inventory.InventoryEventArgTypes.InventoryEventArgs> events)
    {
        // Only care about changes to the main player bags
        var relevant = events.Any(e =>
            e.Item.ContainerType == GameInventoryType.Inventory1 ||
            e.Item.ContainerType == GameInventoryType.Inventory2 ||
            e.Item.ContainerType == GameInventoryType.Inventory3 ||
            e.Item.ContainerType == GameInventoryType.Inventory4);

        if (!relevant) return;

        PublishInventoryState();
    }

    private void PublishInventoryState()
    {
        var freeSlots = InventoryBags
            .SelectMany(bag => Service.GameInventory.GetInventoryItems(bag).ToArray())
            .Count(item => item.ItemId == 0);

        // Only publish if changed
        if (freeSlots == lastFreeSlots) return;
        lastFreeSlots = freeSlots;

        var isFull  = freeSlots == 0;
        var payload = $"{{" +
                      $"\"FreeSlots\":{freeSlots}," +
                      $"\"TotalSlots\":{TotalSlots}," +
                      $"\"IsFull\":{isFull.ToString().ToLower()}" +
                      $"}}";

        Publish(payload);
    }

    public void Dispose()
    {
        Service.GameInventory.InventoryChanged -= OnInventoryChanged;
    }
}