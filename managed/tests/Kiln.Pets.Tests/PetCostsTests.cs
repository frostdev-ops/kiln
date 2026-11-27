// SPDX-License-Identifier: MIT
// Revive costs through kiln.item_costs (dev.32 TAME, 19e 5.2): one public CostReceipt and ItemSelection (Kiln.Items').
// GamePetCosts chooses one slot when the caller selected none (ItemCosts.Choose), quotes with ItemCosts.Check and commits
// with ItemCosts.Take, under one request id per revive operation and world; a selection that is not the owner's plain stack
// of the cost's item never reaches the adapter.
using Kiln;
using Xunit;

namespace Kiln.PetsTests;

public sealed class PetCostsTests
{
    private static readonly EntityRef Owner = new(new EntityId(41), 3);
    private static readonly PetOwner Who = new(new CharacterKey(1, Guid.NewGuid()), Owner, new WorldPosition(0, 0, 0), 10, 5);
    private static readonly ItemId Bone = new(77);
    private static readonly PetCostDefinition Cost = new(Bone, 3);

    private readonly List<(ItemSelection Selection, int Count, Guid Request)> _takes = [];
    private readonly List<(EntityRef Owner, ItemId Item, int Count)> _chooses = [];
    private ItemSelection? _chosen;
    private string? _check;

    private static ItemSelection Stack(SlotId slot, int count = 5, EntityRef? owner = null, ItemId? item = null) =>
        new(owner ?? Owner, slot, item ?? Bone, count, ItemInstanceRef.None);

    private GamePetCosts Costs() => new()
    {
        Choose = (o, i, c) =>
        {
            _chooses.Add((o, i, c));
            return _chosen;
        },
        Check = (_, _) => _check,
        Take = (s, n, id) =>
        {
            _takes.Add((s, n, id));
            return Task.FromResult(new CostReceipt(s.ExpectedItem, n, n, true) { RequestId = id, Selection = s });
        },
    };

    [Fact]
    public async Task No_selection_chooses_one_slot_holding_the_whole_cost_and_takes_from_it_only()
    {
        _chosen = Stack(new SlotId(0x0001_0002), 4);
        var costs = Costs();
        Assert.Null(await costs.Quote(Who, Cost, null));
        var id = Guid.NewGuid();
        var r = await costs.Commit(Who, Cost, null, id);
        Assert.True(r.Committed);
        Assert.Equal((Owner, Bone, 3), _chooses[0]);
        var take = Assert.Single(_takes);
        Assert.Equal((_chosen, 3, id), take);
    }

    [Fact]
    public async Task No_single_stack_holding_the_cost_is_CostUnavailable_and_takes_nothing()
    {
        _chosen = null;
        var costs = Costs();
        Assert.StartsWith("CostUnavailable", await costs.Quote(Who, Cost, null));
        var r = await costs.Commit(Who, Cost, null, Guid.NewGuid());
        Assert.False(r.Committed);
        Assert.Equal(0, r.Taken);
        Assert.Empty(_takes);
    }

    [Fact]
    public async Task A_selected_slot_is_checked_by_the_adapter_and_taken_from_with_no_choice()
    {
        var sel = Stack(new SlotId(0x0001_0005), 7);
        var costs = Costs();
        _check = "StaleSelection: the slot holds 2, not the 7 selected";
        Assert.Equal("CostUnavailable: StaleSelection: the slot holds 2, not the 7 selected", await costs.Quote(Who, Cost, sel));
        _check = null;
        Assert.Null(await costs.Quote(Who, Cost, sel));
        await costs.Commit(Who, Cost, sel, Guid.NewGuid());
        Assert.Empty(_chooses);
        Assert.Same(sel, Assert.Single(_takes).Selection);
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("item")]
    [InlineData("gear")]
    [InlineData("count")]
    public async Task A_selection_that_cannot_pay_this_cost_is_refused_before_the_adapter(string what)
    {
        var sel = what switch
        {
            "owner" => Stack(new SlotId(1), owner: new EntityRef(new EntityId(99), 1)),
            "item" => Stack(new SlotId(1), item: new ItemId(78)),
            "gear" => new ItemSelection(Owner, new SlotId(1), Bone, 1, new ItemInstanceRef(new EntityRef(new EntityId(500), 9), Bone)),
            _ => Stack(new SlotId(1), 2),
        };
        var costs = Costs();
        Assert.StartsWith("CostUnavailable", await costs.Quote(Who, Cost, sel));
        var r = await costs.Commit(Who, Cost, sel, Guid.NewGuid());
        Assert.False(r.Committed);
        Assert.Equal(CostRefusal.StaleSelection, r.Refusal);
        Assert.Empty(_takes);
    }

    [Fact]
    public async Task An_off_adapter_is_CostUnavailable_never_a_throw()
    {
        var costs = new GamePetCosts
        {
            Choose = (_, _, _) => throw new KilnUnavailableException("items.costs.exact", "off"),
            Check = (_, _) => throw new KilnUnavailableException("items.costs.exact", "off"),
            Take = (_, _, _) => throw new KilnUnavailableException("items.costs.exact", "off"),
        };
        Assert.StartsWith("CostUnavailable", await costs.Quote(Who, Cost, null));
        Assert.StartsWith("CostUnavailable", await costs.Quote(Who, Cost, Stack(new SlotId(1))));
        var r = await costs.Commit(Who, Cost, Stack(new SlotId(1)), Guid.NewGuid());
        Assert.False(r.Committed);
        Assert.Equal(CostRefusal.NotRun, r.Refusal);
    }

    [Fact]
    public void A_revive_debit_request_id_is_one_per_operation_and_world()
    {
        var op = Guid.NewGuid();
        var id = GamePetCosts.RequestFor(op, 1);
        Assert.NotEqual(Guid.Empty, id);
        Assert.Equal(id, GamePetCosts.RequestFor(op, 1));
        Assert.NotEqual(id, GamePetCosts.RequestFor(op, 2));            // a new world never answers with an old receipt
        Assert.NotEqual(id, GamePetCosts.RequestFor(Guid.NewGuid(), 1));
        Assert.NotEqual(op, id);
    }
}
