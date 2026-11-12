// SPDX-License-Identifier: MIT
using Kiln;

namespace ItemIdConsumer;

/// <summary>Uses ItemId the ways a mod does: a field, a parameter, the constructor, FromGuidBytes and typeof.</summary>
public static class OldLayoutUser
{
    public static ItemId Field = new ItemId(5);

    public static Type TypeSeen() => typeof(ItemId);

    public static uint FieldValue() => Field.Value;

    public static uint Echo(ItemId id) => id.Value;

    public static object Make(uint value) => new ItemId(value);

    public static uint FromGuid(byte[] guid) => ItemId.FromGuidBytes(guid).Value;
}
