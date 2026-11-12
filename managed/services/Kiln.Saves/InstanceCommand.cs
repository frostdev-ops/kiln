// SPDX-License-Identifier: MIT
// The admin command /instances [player|entity], Kiln's own (owner "kiln"). Commands live in Kiln.Chat, and a mod that only
// stores data must not pull the chat library in, so Kiln.Saves has no reference to it: the command is registered late-bound,
// by name, the way Kiln.Api's ModScan.RegisterCommands reaches Kiln.Chat. The listing itself is InstanceStore.Listing, which
// needs no chat. If Kiln.Chat is not loaded or its shape changed, this logs once and nothing else depends on it.
using System.Reflection;
using System.Runtime.Loader;

namespace Kiln;

internal static class InstanceCommand
{
    internal const string Name = "instances";
    private static bool _registered, _gaveUp, _tickAdded, _warned;
    private static IDisposable? _tick;
    private static PropertyInfo? _args;
    private static MethodInfo? _reply;
    private static Type? _commandException;

    /// <summary>The first key was declared: register now if Kiln.Chat is loaded, else again at the first service tick (it is
    /// auto-started there).</summary>
    internal static void Ensure()
    {
        if (_registered || _gaveUp) return;
        if (TryRegister(final: false)) return;
        if (_tickAdded) return;
        _tickAdded = true;
        _tick = ServiceTick.Add("kiln.saves /instances", static (in TickInfo _) =>
        {
            TryRegister(final: true);
            _tick?.Dispose();
            _tick = null;
        });
    }

    /// <summary>True when the command is registered (now or before).</summary>
    internal static bool TryRegister(bool final)
    {
        if (_registered) return true;
        if (_gaveUp) return false;
        var chat = FindChat();
        if (chat is null)
        {
            if (final) GiveUp("Kiln.Chat is not loaded, so there is no /instances command (the data itself works)");
            return false;
        }
        try
        {
            var commands = chat.GetType("Kiln.Commands");
            var handlerType = chat.GetType("Kiln.CommandHandler");
            var permissionType = chat.GetType("Kiln.CommandPermission");
            var contextType = chat.GetType("Kiln.CommandContext");
            _commandException = chat.GetType("Kiln.CommandException");
            if (commands is null || handlerType is null || permissionType is null || contextType is null)
                return GiveUp("Kiln.Chat has no Commands, CommandHandler, CommandPermission or CommandContext");
            var register = commands.GetMethod("Register", BindingFlags.Public | BindingFlags.Static,
                [typeof(string), handlerType, permissionType, typeof(string), typeof(string), typeof(string[])]);
            _args = contextType.GetProperty("Args", BindingFlags.Public | BindingFlags.Instance);
            _reply = contextType.GetMethod("Reply", BindingFlags.Public | BindingFlags.Instance, [typeof(string)]);
            if (register is null || _args is null || _reply is null)
                return GiveUp("Kiln.Chat's Commands.Register, CommandContext.Args or Reply has another shape");
            // A handler of ours that takes the context as object: the delegate binds it to CommandHandler(CommandContext).
            var handler = Delegate.CreateDelegate(handlerType, typeof(InstanceCommand).GetMethod(nameof(Handle), BindingFlags.NonPublic | BindingFlags.Static)!);
            var admin = Enum.Parse(permissionType, "Admin");
            var outer = ModContext.CurrentMod;
            ModContext.CurrentMod = null;  // Kiln's command, not the calling mod's: it stays when that mod unloads
            try
            {
                register.Invoke(null, [Name, handler, admin, "[player|entity]",
                    "instance data: what is attached to items, what waits and what is set aside, per mod", Array.Empty<string>()]);
            }
            finally
            {
                ModContext.CurrentMod = outer;
            }
            _registered = true;
            return true;
        }
        catch (TargetInvocationException e) when (e.InnerException is not null)
        {
            return GiveUp($"registering /{Name} failed: {e.InnerException.Message}");
        }
        catch (Exception e) when (e is ArgumentException or MissingMethodException or AmbiguousMatchException or TypeLoadException)
        {
            return GiveUp($"Kiln.Chat's command API has another shape: {e.Message}");
        }
    }

    private static Assembly? FindChat()
    {
        var own = AssemblyLoadContext.GetLoadContext(typeof(InstanceCommand).Assembly);
        foreach (var a in own?.Assemblies ?? [])
            if (a.GetName().Name == "Kiln.Chat") return a;
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name == "Kiln.Chat") return a;
        return null;
    }

    private static bool GiveUp(string why)
    {
        _gaveUp = true;
        if (!_warned)
        {
            _warned = true;
            SavesRuntime.Log(LogLevel.Warn, $"Kiln.Saves: {why}");
        }
        return false;
    }

    /// <summary>Runs as Kiln's command handler: the context is Kiln.Chat's CommandContext.</summary>
    private static void Handle(object context)
    {
        var args = (IReadOnlyList<string>?)_args!.GetValue(context) ?? [];
        EntityId? entity = null;
        CharacterKey? character = null;
        if (args.Count > 0)
        {
            var a = args[0];
            if (uint.TryParse(a, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var id)) entity = new EntityId(id);
            else if (Players.Available && Players.Find(a) is { } p)
            {
                entity = p.Entity;
                var key = CharacterStore.KeyOf(p);
                if (!key.IsNone) character = key;
            }
            else
                throw (Exception)Activator.CreateInstance(_commandException!, $"no player or entity '{a}'; usage: /{Name} [player|entity]")!;
        }
        IReadOnlyList<string> lines;
        if (!SavesRuntime.Ensure()) lines = ["kiln.saves is not loaded"];
        else if (character is { } c && entity is { } e)
        {
            // a player: the items they hold now, and what waits for their character
            lines = SavesRuntime.Store.Instances.Listing(entity: e).Concat(SavesRuntime.Store.Instances.Listing(character: c).Skip(1)).ToList();
        }
        else lines = SavesRuntime.Store.Instances.Listing(entity: entity);
        foreach (var line in lines) _reply!.Invoke(context, [line]);
    }

    /// <summary>Tests: forget the registration state (the test registers against a fresh Commands table).</summary>
    internal static void ResetForTests()
    {
        _registered = _gaveUp = _tickAdded = _warned = false;
        _tick?.Dispose();
        _tick = null;
    }
}
