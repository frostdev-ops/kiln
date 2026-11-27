// SPDX-License-Identifier: MIT
// A pet's body is set up and ordered as the mod that defined the pet: Kiln.Pets makes bodies and gives orders from its own
// service tick, where no mod's code runs, and Kiln.Creatures keeps every change a mod's (a layer it reverts when that mod
// unloads). Review of dev.32 pets (LABBOT's bot owner): every summon was refused "BodyRefused: its faction could not be set
// (Faction: call it from mod code (changes belong to a mod))", for every owner; and an attack order's held target
// (PreferTarget) threw the same and was dropped without a word.
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Kiln;
using Kiln.TestKit;
using Xunit;

namespace Kiln.PetsTests;

public sealed unsafe class BodyOwnerTests
{
    public sealed class HoundMod : Mod
    {
        public override void OnLoad() =>
            Pets.Define(new PetDefinition { Id = "pets-owner:hound", Creature = "game:enemy_wildbeast_wolf_earlygame" });
    }

    private static readonly List<(string What, ulong Owner, uint Entity, uint Value)> Writes = [];

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeSet(ulong owner, uint e, uint field, uint index, CreaturesNative.Value* v)
    {
        lock (Writes) Writes.Add(("set", owner, e, field));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakePrefer(ulong owner, uint e, uint target, float seconds)
    {
        lock (Writes) Writes.Add(("prefer", owner, e, target));
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeCommand(CreaturesNative.Command* c) => 0;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int FakeBind(ulong owner, PetsNative.Binding* b) => PetsNative.Ok;

    [Fact]
    public void A_pets_body_is_set_up_as_the_mod_that_defined_it()
    {
        var (modOwner, body, _, writes) = Summon(attack: false);
        Assert.Contains(("set", modOwner, body.Value, CreaturesNative.Faction), writes);   // on the players' side
    }

    [Fact]
    public void An_attack_order_holds_its_target_as_the_mod_that_defined_the_pet()
    {
        var (modOwner, body, target, writes) = Summon(attack: true);
        Assert.Contains(("prefer", modOwner, body.Value, target.Value), writes);
    }

    /// <summary>A body made (and given an attack order) from Kiln's code, as Kiln.Pets' service tick makes it; the native
    /// owner the defining mod's own changes carry; every write the fake table saw.</summary>
    private static (ulong ModOwner, EntityId Body, EntityId Target, List<(string What, ulong Owner, uint Entity, uint Value)> Writes) Summon(bool attack)
    {
        using var kiln = new KilnHarness();
        kiln.Boot();
        kiln.Step(2);
        var template = kiln.Spawned.AddTemplate("Enemy_Wildbeast_wolf_earlygame");
        var mod = kiln.Load<HoundMod>(new ModManifest("pets-owner"));
        var target = kiln.Entities.Create(new WorldPosition(14, 0, 10));

        // kiln.creatures and kiln.pets' native tables: the kit has neither; only the calls these paths make are set
        var creatures = (CreaturesNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(CreaturesNative.Table));
        var pets = (PetsNative.Table*)NativeMemory.AllocZeroed((nuint)sizeof(PetsNative.Table));
        creatures->Set = &FakeSet;
        creatures->TargetPrefer = &FakePrefer;
        creatures->SendCommand = &FakeCommand;
        pets->Bind = &FakeBind;
        var restore = new List<(FieldInfo Field, object? Was)>();
        void Swap(Type owner, string field, object? value)
        {
            var f = owner.GetField(field, BindingFlags.Static | BindingFlags.NonPublic) ?? throw new MissingFieldException(owner.Name, field);
            restore.Add((f, f.GetValue(null)));
            f.SetValue(null, value);
        }
        try
        {
            // no step runs while these are in place (the world's own roster would call what is not set)
            Swap(typeof(CreaturesNative), "_t", Pointer.Box(creatures, typeof(CreaturesNative.Table*)));
            Swap(typeof(Creatures), "_listening", 1);   // no native listener on a fake table
            Swap(typeof(PetsNative), "_table", Pointer.Box(pets, typeof(PetsNative.Table*)));
            lock (Writes) Writes.Clear();

            // the native owner the mod's own changes carry
            kiln.As(mod, () => { Creatures.Of(target).Scale = 1; });
            ulong modOwner;
            lock (Writes)
            {
                modOwner = Assert.Single(Writes).Owner;
                Writes.Clear();
            }
            Assert.NotEqual(0ul, modOwner);

            // Kiln.Pets' service tick: Kiln's code, no mod's
            var (canMake, why, body, reason) = kiln.Run(() =>
            {
                var world = new GamePetWorld();
                var can = world.CanMakeBodies(out var off);
                var def = Pets.Definition("pets-owner:hound")!;
                var made = world.CreateBody(new PetBodyRequest
                {
                    Pet = new PetId(Guid.NewGuid()), Owner = new CharacterKey(1, Guid.NewGuid()), OwnerSession = 1, Definition = def,
                    Creature = new PetCreatureInfo(def.Creature, template, null, 105, "Wolf"), Position = new WorldPosition(10, 0, 10),
                    Level = 1, HealthFraction = 1,
                }, out var refused);
                if (made is not null && attack) new GamePetMover().Attack(made, target);
                return (can, off, made?.Entity.Id ?? EntityId.None, refused);
            });
            Assert.True(canMake, why);
            Assert.True(reason is null, reason);
            Assert.False(body.IsNone);
            lock (Writes) return (modOwner, body, target, [.. Writes]);
        }
        finally
        {
            foreach (var (f, was) in Enumerable.Reverse(restore)) f.SetValue(null, was);
            NativeMemory.Free(creatures);
            NativeMemory.Free(pets);
        }
    }
}
