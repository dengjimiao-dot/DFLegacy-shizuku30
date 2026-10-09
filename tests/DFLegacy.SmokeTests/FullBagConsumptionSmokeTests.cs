using DFLegacy.Protocol;
using DFLegacy.Server;
using Microsoft.Extensions.Logging.Abstractions;

internal static class FullBagConsumptionSmokeTests
{
    public static async Task RunAsync(Action<bool, string> check, ItemCatalog items)
    {
        var inventory = Enumerable.Range(CharacterInventoryLayout.QuickSlotStart,
                CharacterInventoryLayout.QuestSlotEnd - CharacterInventoryLayout.QuickSlotStart)
            .ToDictionary(slot => (ushort)slot, slot => new CharacterItemRecord((ushort)slot,
                slot < CharacterInventoryLayout.QuickSlotEnd ? (ushort)8
                    : slot < CharacterInventoryLayout.EquipmentSlotEnd ? (ushort)10_000
                    : slot < CharacterInventoryLayout.ConsumableSlotEnd ? (ushort)8
                    : slot < CharacterInventoryLayout.MaterialSlotEnd ? (ushort)3037 : (ushort)4000,
                slot is >= CharacterInventoryLayout.EquipmentSlotStart
                    and < CharacterInventoryLayout.EquipmentSlotEnd ? 1u : 100u));
        var snapshot = inventory.ToDictionary();
        var grounds = new DungeonGroundItemState();
        var ground = grounds.Add(new DungeonGeneratedDrop(false, 3176, 1), 1, 0, 0, 0);
        var request = new PacketFrame(GameProtocolEngine.CommandPacketType,
            GameProtocolEngine.GetItemCommand, 0, [1, 0, (byte)ground.GroundId, 0]);

        check(!DungeonPickupPlanner.TryPlanItem(inventory, ground, items, uint.MaxValue,
                    out _, out var failure)
                && failure == InventoryPlacementFailure.Full
                && GameProtocolEngine.CreateGetItemRejection(request,
                    inventoryFull: failure == InventoryPlacementFailure.Full).Payload
                    .SequenceEqual(new byte[] { 0, 4 })
                && grounds.TryGet(ground.GroundId, 0, 0, out var retained) && retained == ground
                && inventory.Values.SequenceEqual(snapshot.Values),
            "full main and quick-slot bags reject a new ground item with CMD46 error 4 while retaining the same ground object and inventory");
        var silent = GameProtocolEngine.CreateGetItemRejection(request);
        check(silent.Type == GameProtocolEngine.CommandPacketType && silent.ProtocolId == 46
                && silent.Payload.SequenceEqual(new byte[] { 0, 0x15 })
                && GameProtocolEngine.CreateGetItemSilentRejection(request).Payload.SequenceEqual(silent.Payload),
            "other pickup failures keep the silent DF2008 rollback, not a false NOTI39 pickup or a new NOTI40 ground object");

        check(ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 5)], items, out var crystals)
                && crystals.Inventory[CharacterInventoryLayout.MaterialSlotStart].CountOrValue == 95
                && crystals.Inventory.Count == inventory.Count
                && inventory.Values.SequenceEqual(snapshot.Values)
                && GameProtocolEngine.CreateDeleteItemReply(0, crystals.CompletionEntries).Payload
                    .SequenceEqual(new byte[] { 1, 0, 1, 73, 0, 5, 0, 0, 0 }),
            "CMD18 skill-material consumption succeeds with every slot occupied and does not allocate a slot or deduct twice");
        check(ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(0, CharacterInventoryLayout.QuickSlotStart, 8, 1)], items, out var potion)
                && potion.Inventory[CharacterInventoryLayout.QuickSlotStart].CountOrValue == 99
                && potion.Inventory.Count == inventory.Count
                && GameProtocolEngine.CreateUseStackableItemReply(CharacterInventoryLayout.QuickSlotStart, 0)
                    .Payload.SequenceEqual(new byte[] { 1, 3, 0, 0 }),
            "pure potion consumption needs no free slot and CMD47 retains its slot/list completion layout");
        check(!DungeonPickupPlanner.TryPlanItem(potion.Inventory, ground, items, uint.MaxValue,
                    out _, out var stillFull) && stillFull == InventoryPlacementFailure.Full,
            "using one item from a remaining stack does not bypass capacity for new loot, boxes or gifts");
        check(ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 100)], items, out var lastStack)
                && !lastStack.Inventory.ContainsKey(CharacterInventoryLayout.MaterialSlotStart)
                && DungeonPickupPlanner.TryPlanItem(lastStack.Inventory, ground, items, uint.MaxValue,
                    out var afterConsumption, out _)
                && afterConsumption.DestinationSlot == CharacterInventoryLayout.MaterialSlotStart,
            "consuming the last material stack really frees its category slot for a later pickup");
        check(!ItemDeletionPlanner.TryCreate(inventory, 0,
                    [new(2, CharacterInventoryLayout.MaterialSlotStart, 3037, 101)], items, out _)
                && inventory.Values.SequenceEqual(snapshot.Values),
            "a full bag does not weaken item identity or available-count validation for consumption");

        var root = Path.Combine(Path.GetTempPath(), $"dflegacy-full-bag-consume-{Guid.NewGuid():N}");
        try
        {
            var options = new ServerOptions { DataPath = Path.Combine(root, "state.json") };
            var store = new JsonGameStore(options, NullLogger<JsonGameStore>.Instance);
            await store.InitializeAsync();
            var created = await store.CreateCharacterAsync("test", new CreateCharacterRequest("FullBagTest", 2));
            var character = created.Character!;
            _ = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold, inventory.Values, []);
            var saved = await store.SaveCharacterInventoryAsync("test", character.Id, character.Gold,
                crystals.Inventory.Values, []);
            var reloadedStore = new JsonGameStore(options, NullLogger<JsonGameStore>.Instance);
            await reloadedStore.InitializeAsync();
            var reloaded = (await reloadedStore.GetCharactersAsync("test"))
                .Single(candidate => candidate.Id == character.Id);
            check(saved is not null && reloaded.Inventory!.Count == inventory.Count
                    && reloaded.Inventory.Single(item => item.Slot == CharacterInventoryLayout.MaterialSlotStart)
                        .CountOrValue == 95,
                "full-bag skill-material consumption persists across a store reload without creating or discarding other items");
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
