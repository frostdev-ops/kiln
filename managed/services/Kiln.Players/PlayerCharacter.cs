// SPDX-License-Identifier: MIT
// The character a player plays: the game's CharacterId, read by kiln.characters (native/services/characters) from the
// player record. Evidence: docs/re/characters.md. Usage: docs/kiln/services/character-data.md.
using System.Runtime.InteropServices;

namespace Kiln;

public sealed partial record Player
{
    /// <summary>The game's id of the character this player plays (<c>keen::CharacterId</c>: a GUID in the header of
    /// <c>character&lt;N&gt;.ksd</c>, in the login message and in the world's <c>known_users</c>). Stable for the
    /// character's life, renames included; a copy made with the game's copy function gets a new one.
    /// <see cref="Guid.Empty"/> when unknown: a build where kiln.characters found no offset, or a player known only from
    /// the game log.</summary>
    public Guid CharacterId => PlayerCharacters.IdOf(this);
}

/// <summary>What kiln.characters can do on this build.</summary>
public sealed record PlayerCharactersStatus(bool Resolved, bool Records, bool Simulated, ulong Scans, int Known, int RecordOffset);

/// <summary>kiln.characters: the game's CharacterId of each connected player. Any thread; values are as of the last
/// server-world step, and stay readable after the player left until another connection takes the slot.</summary>
public static unsafe class PlayerCharacters
{
    [StructLayout(LayoutKind.Sequential)]
    private struct Info
    {
        public uint Size, Slot, Handle, Flags;
        public ulong Account;
        public fixed byte Id[16];
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeStatus
    {
        public uint Size, Resolved, Records, Simulated;
        public ulong Scans;
        public uint Known, Offset;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Table
    {
        public ServiceHeader Header;
        public delegate* unmanaged[Cdecl]<NativeStatus*, int> Status;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> ByHandle;
        public delegate* unmanaged[Cdecl]<uint, Info*, int> BySlot;
        public delegate* unmanaged[Cdecl]<uint, ulong, byte*, int> Simulate;
    }

    private static Table* _table;
    private static bool _looked;

    private static Table* T
    {
        get
        {
            if (_table != null || _looked) return _table;
            _looked = true;
            if (sizeof(Info) != 40 || sizeof(NativeStatus) != 32) return null;
            _table = Services.Get<Table>("kiln.characters", 1);
            return _table;
        }
    }

    /// <summary>True when the CharacterId can be read on this build.</summary>
    public static bool Available => T != null && Read().Resolved != 0;

    /// <summary>Null when kiln.characters is not loaded.</summary>
    public static PlayerCharactersStatus? Status
    {
        get
        {
            if (T == null) return null;
            var s = Read();
            return new PlayerCharactersStatus(s.Resolved != 0, s.Records != 0, s.Simulated != 0, s.Scans, (int)s.Known, (int)s.Offset);
        }
    }

    private static NativeStatus Read()
    {
        var s = new NativeStatus { Size = (uint)sizeof(NativeStatus) };
        return T != null && T->Status(&s) == 0 ? s : default;
    }

    private static bool Lookup(Player p, out Info info)
    {
        var i = new Info { Size = (uint)sizeof(Info) };
        info = default;
        var t = T;
        if (t == null) return false;
        // a player from the game log has no handle: only a lab simulation has an entry for it
        var rc = p.Handle != 0 ? t->ByHandle(p.Handle, &i) : t->BySlot((uint)p.Slot, &i);
        if (rc != 0 || (p.Handle == 0 && (i.Flags & 2) == 0)) return false;
        info = i;
        return true;
    }

    /// <summary>The player's character id; <see cref="Guid.Empty"/> when unknown.</summary>
    public static Guid IdOf(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return Lookup(p, out var i) ? new Guid(new ReadOnlySpan<byte>(i.Id, 16)) : Guid.Empty;
    }

    /// <summary>The account key the game files the character under (record +0x48, the world's <c>known_users</c> key).</summary>
    public static ulong AccountOf(Player p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.AccountId != 0 ? p.AccountId : Lookup(p, out var i) ? i.Account : 0;
    }

    /// <summary>Lab only (<c>KILN_CHARACTERS_SIMULATE=1</c>): a slot holds this character, for players replayed by
    /// <c>KILN_PLAYERS_SIMULATE</c>. Null clears it. False when simulation is off.</summary>
    public static bool Simulate(int slot, ulong account, Guid? id)
    {
        var t = T;
        if (t == null) return false;
        if (id is not { } g) return t->Simulate((uint)slot, 0, null) == 0;
        var b = g.ToByteArray();
        fixed (byte* p = b) return t->Simulate((uint)slot, account, p) == 0;
    }
}
