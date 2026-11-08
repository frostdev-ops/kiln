// SPDX-License-Identifier: MIT
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Kiln.Analyzers;

/// <summary>What a mod's source says about its blocks: read the same way by the rules and by the generator that writes the mod's need.</summary>
internal static class BlockFacts
{
    public const int None = 0, Kiln = 1, Mod = 2;
    public static readonly string[] LevelNames = { "none", "kiln", "mod" };

    /// <summary><c>Blocks.Define&lt;T&gt;(...)</c>: its block type, or null.</summary>
    public static INamedTypeSymbol? DefinedType(InvocationExpressionSyntax call, SemanticModel model)
    {
        if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Define" }) return null;
        if (model.GetSymbolInfo(call).Symbol is not IMethodSymbol { ContainingType: { Name: "Blocks", ContainingNamespace.Name: "Kiln" } } m) return null;
        // Define<T>, or an overload for one block type, whose handle does more
        return m.TypeArguments.Length == 1 ? m.TypeArguments[0] as INamedTypeSymbol : m.Parameters.Length == 2 ? m.Parameters[1].Type as INamedTypeSymbol : null;
    }

    /// <summary>What a block type asks of players' games on its own: its [BlockClients], else Kiln.</summary>
    public static int LevelOf(ITypeSymbol block)
    {
        foreach (var a in block.GetAttributes())
            if (a.AttributeClass?.Name == "BlockClientsAttribute" && a.ConstructorArguments.Length == 1 && a.ConstructorArguments[0].Value is int level)
                return level;
        return Kiln;
    }

    /// <summary>An assignment in a block's initializer whose value is the mod's own file or cue ([ClientsModWhen]): its property, else null.</summary>
    public static IPropertySymbol? OwnFiles(AssignmentExpressionSyntax assignment, SemanticModel model)
    {
        if (model.GetSymbolInfo(assignment.Left).Symbol is not IPropertySymbol p) return null;
        foreach (var a in p.GetAttributes())
        {
            if (a.AttributeClass?.Name != "ClientsModWhenAttribute" || a.ConstructorArguments.Length != 1 || a.ConstructorArguments[0].Value is not string pattern) continue;
            if (model.GetConstantValue(assignment.Right) is { HasValue: true, Value: string value } && Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant)) return p;
        }
        return null;
    }

    /// <summary>The block kind a string converts to where a block is named, or null. The kind is <see cref="KindOf"/>'s for that block type
    /// ("effect", "status", "vfx", "sound", "perk", "ability", "material", "station", "screeneffect"), plus "item" (any item-like block) and
    /// "template" (game templates only). <c>IBlockRef&lt;T&gt;</c> names a T.</summary>
    public static string? NamedKind(ITypeSymbol? converted)
    {
        if (converted is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable) converted = nullable.TypeArguments[0];
        switch (converted?.Name)
        {
            case "EffectRef": return "effect";
            case "StatusRef" or "StatusApply": return "status";
            case "VfxRef": return "vfx";
            case "SoundRef": return "sound";
            case "ItemRef": return "item";
            case "PerkRef": return "perk";
            case "AbilityRef": return "ability";
            case "MaterialRef": return "material";
            case "StationRef": return "station";
            case "ScreenRef": return "screeneffect";
            case "TemplateRef": return "template";
        }
        if (converted is not INamedTypeSymbol named) return null;
        foreach (var i in named.AllInterfaces)
            if (i is { Name: "IBlockRef", TypeArguments.Length: 1 } && i.TypeArguments[0] is { } t) return KindOf(t);
        return null;
    }

    // Defined block types an ItemRef must not name: those the compiler's ItemName refuses, and sounds, materials and screen effects, which make no item.
    // A spell is an item (its charges).
    private static readonly HashSet<string> NotItems = new(StringComparer.Ordinal)
    {
        "EffectBlock", "StatusBlock", "VfxBlock", "VisualBlock", "OnHitBlock", "WeaponStats", "ArmorStats", "WeaponClass", "ArmorSlot", "ArmorSet", "WornLook",
        "PlacementBlock", "ComfortBlock", "ProcessingBlock", "MenuBlock", "PerkBlock", "AbilityBlock", "TalentBlock", "TalentTreeBlock", "TriggerBlock",
        "SoundBlock", "MaterialBlock", "ScreenEffectBlock",
    };

    /// <summary>Whether a block of this type answers a reference of this kind.</summary>
    public static bool Answers(string kind, ITypeSymbol block) => kind == "item" ? !NotItems.Contains(block.Name) : KindOf(block) == kind;

    public static string Article(string kind) => (kind.Length > 0 && "aeiou".IndexOf(kind[0]) >= 0 ? "an " : "a ") + kind;

    public static string KindOf(ITypeSymbol block) => block.Name.EndsWith("Block", StringComparison.Ordinal) ? block.Name.Substring(0, block.Name.Length - 5).ToLowerInvariant() : block.Name;
}

/// <summary>KILN110 (bad or duplicate block name), KILN111 (a name that is no block) and KILN112 (clients too low for the blocks).
/// All from one pass over the compilation: names defined later in a file, in another file or in blocks/*.json count.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BlocksAnalyzer : DiagnosticAnalyzer
{
    private static readonly Regex ValidName = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex GameName = new("^(0x|buff_)|^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-", RegexOptions.CultureInvariant);
    private static readonly Regex JsonClients = new("\"clients\"\\s*:\\s*\"(none|kiln|mod)\"", RegexOptions.CultureInvariant);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Descriptors.BlockName, Descriptors.BlockReference, Descriptors.BlockClients);

    private sealed class Defined(string? name, ITypeSymbol type, Location where, bool conditional)
    {
        public string? Name { get; } = name;
        public ITypeSymbol Type { get; } = type;
        public Location Where { get; } = where;
        public bool Conditional { get; } = conditional;
    }

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            var defined = new ConcurrentBag<Defined>();
            var named = new ConcurrentBag<(string Name, string Kind, Location Where)>();
            var own = new ConcurrentBag<(IPropertySymbol Property, Location Where)>();
            start.RegisterSyntaxNodeAction(ctx =>
            {
                var call = (InvocationExpressionSyntax)ctx.Node;
                if (BlockFacts.DefinedType(call, ctx.SemanticModel) is not { } type || call.ArgumentList.Arguments.Count == 0) return;
                var arg = call.ArgumentList.Arguments[0].Expression;
                var value = ctx.SemanticModel.GetConstantValue(arg);
                var conditional = call.Ancestors().TakeWhile(a => a is not MemberDeclarationSyntax).Any(a => a is IfStatementSyntax or SwitchStatementSyntax or ConditionalExpressionSyntax or ForEachStatementSyntax or ForStatementSyntax or WhileStatementSyntax);
                defined.Add(new Defined(value is { HasValue: true, Value: string s } ? s : null, type, arg.GetLocation(), conditional));
            }, SyntaxKind.InvocationExpression);
            start.RegisterSyntaxNodeAction(ctx =>
            {
                var literal = (LiteralExpressionSyntax)ctx.Node;
                if (BlockFacts.NamedKind(ctx.SemanticModel.GetTypeInfo(literal).ConvertedType) is { } kind)
                    named.Add((literal.Token.ValueText, kind, literal.GetLocation()));
            }, SyntaxKind.StringLiteralExpression);
            start.RegisterSyntaxNodeAction(ctx =>
            {
                var assignment = (AssignmentExpressionSyntax)ctx.Node;
                if (assignment.Parent is InitializerExpressionSyntax && BlockFacts.OwnFiles(assignment, ctx.SemanticModel) is { } p)
                    own.Add((p, assignment.GetLocation()));
            }, SyntaxKind.SimpleAssignmentExpression);
            var options = start.Options;
            start.RegisterCompilationEndAction(ctx => Finish(ctx, options, defined.ToList(), named.ToList(), own.ToList()));
        });
    }

    private static void Finish(CompilationAnalysisContext ctx, AnalyzerOptions options, List<Defined> defined, List<(string Name, string Kind, Location Where)> named, List<(IPropertySymbol Property, Location Where)> own)
    {
        void Report(DiagnosticDescriptor rule, Location where, string text) => ctx.ReportDiagnostic(Diagnostic.Create(rule, where, text));

        // ---- names
        foreach (var d in defined.Where(d => d.Name != null && !ValidName.IsMatch(d.Name)))
            Report(Descriptors.BlockName, d.Where, $"\"{d.Name}\" cannot be a block name: a name takes letters, digits, '-', '_' and '.'");
        foreach (var group in defined.Where(d => d.Name != null && !d.Conditional).GroupBy(d => d.Name!))
            foreach (var again in group.OrderBy(d => d.Where.SourceSpan.Start).Skip(1))
                Report(Descriptors.BlockName, again.Where, $"block \"{group.Key}\" is defined more than once: its ids come from its name, so these are one block");

        // ---- references: only when every name is known (a name built at run time could be any)
        // the mod's own data files; not the local game catalog (KilnCatalog, megabytes, read by the GameIds generator): game names come from GameIds
        var jsonText = options.AdditionalFiles.Where(f => string.Equals(Path.GetExtension(f.Path), ".json", StringComparison.OrdinalIgnoreCase) && !string.Equals(Path.GetFileName(f.Path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)
                && !(options.AnalyzerConfigOptionsProvider.GetOptions(f).TryGetValue("build_metadata.AdditionalFiles.KilnCatalog", out var catalog) && string.Equals(catalog, "true", StringComparison.OrdinalIgnoreCase)))
            .Select(f => f.GetText(ctx.CancellationToken)?.ToString() ?? "").ToList();
        var gameCache = new Dictionary<string, HashSet<string>?>();
        if (defined.All(d => d.Name != null))
            foreach (var (name, kind, where) in named)
            {
                if (name.Contains(':') || GameName.IsMatch(name) || Guid.TryParse(name, out _)) continue;
                if (Special(kind, name)) continue;
                var same = defined.FirstOrDefault(d => d.Name == name);
                if (same != null)
                {
                    if (!BlockFacts.Answers(kind, same.Type)) Report(Descriptors.BlockReference, where, $"\"{name}\" is {BlockFacts.Article(BlockFacts.KindOf(same.Type))} block, and this takes {BlockFacts.Article(kind)}");
                    continue;
                }
                if (jsonText.Any(t => t.Contains($"\"{name}\""))) continue;
                // a game name: known only from the generated GameIds; without them any name could be the game's
                if (GameClass(kind) is { } cls)
                {
                    var game = GameNames(ctx.Compilation, cls, gameCache);
                    if (game == null || game.Contains(name)) continue;
                    Report(Descriptors.BlockReference, where, kind == "template"
                        ? $"the game has no template \"{name}\": use a GameTemplates constant or a GUID"
                        : $"this mod has no {kind} block \"{name}\", and the game has none by that name ({cls}): define it, or say \"other-mod:{name}\"");
                    continue;
                }
                Report(Descriptors.BlockReference, where, kind == "sound"
                    ? $"this mod has no sound \"{name}\" (a SoundBlock, or a cue of assets/cook.json): define it, say \"other-mod:{name}\", or give a game cue's GUID (GameSounds.Guids)"
                    : $"this mod has no block \"{name}\": define it, or say \"other-mod:{name}\"");
            }

        // ---- clients: what the project says against what its blocks need
        var said = Said(options);
        if (said == null) return;
        foreach (var (property, where) in own)
            if (said < BlockFacts.Mod)
                Report(Descriptors.BlockClients, where, $"{property.Name} names the mod's own file, which has to be on every player's game, and the mod says clients \"{BlockFacts.LevelNames[said.Value]}\": say \"mod\", or leave it out and the build works it out");
        foreach (var d in defined)
            if (BlockFacts.LevelOf(d.Type) > said)
                Report(Descriptors.BlockClients, d.Where, $"a new {BlockFacts.KindOf(d.Type)} is drawn by the player's game, and the mod says clients \"{BlockFacts.LevelNames[said.Value]}\": say \"{BlockFacts.LevelNames[BlockFacts.LevelOf(d.Type)]}\", or leave it out and the build works it out");
    }

    // Values a reference of a kind takes that are no block's name.
    private static bool Special(string kind, string name) => kind switch
    {
        "perk" => name is "none" or "" || name.StartsWith("item:", StringComparison.Ordinal),
        "station" => name == "hand",
        _ => false,
    };

    // The generated GameIds class for kinds that also take a game name. Not sounds: a game cue is named by its GUID only (a name is the mod's own cue).
    private static string? GameClass(string kind) => kind switch
    {
        "item" => "GameItems",
        "perk" => "GamePerks",
        "template" => "GameTemplates",
        _ => null,
    };

    // The string constants of Kiln.GameIds.<cls> (and its nested classes), or null when the mod has no generated GameIds.
    private static HashSet<string>? GameNames(Compilation compilation, string cls, Dictionary<string, HashSet<string>?> cache)
    {
        if (cache.TryGetValue(cls, out var known)) return known;
        HashSet<string>? names = null;
        if (compilation.GetTypeByMetadataName("Kiln.GameIds." + cls) is { } type)
        {
            names = new HashSet<string>(StringComparer.Ordinal);
            var todo = new Stack<INamedTypeSymbol>();
            todo.Push(type);
            while (todo.Count > 0)
            {
                var t = todo.Pop();
                foreach (var m in t.GetMembers())
                    if (m is IFieldSymbol { HasConstantValue: true, ConstantValue: string v }) names.Add(v);
                    else if (m is INamedTypeSymbol nested) todo.Push(nested);
            }
        }
        return cache[cls] = names;
    }

    // kiln.mod.json wins, as in the SDK's manifest task; then KilnClients. Null: nothing said, the build works it out.
    private static int? Said(AnalyzerOptions options)
    {
        foreach (var file in options.AdditionalFiles)
        {
            if (!string.Equals(Path.GetFileName(file.Path), "kiln.mod.json", StringComparison.OrdinalIgnoreCase)) continue;
            var m = JsonClients.Match(file.GetText()?.ToString() ?? "");
            if (m.Success) return Array.IndexOf(BlockFacts.LevelNames, m.Groups[1].Value);
        }
        return options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue("build_property.KilnClients", out var v) && Array.IndexOf(BlockFacts.LevelNames, v.Trim()) is >= 0 and var i ? i : null;
    }
}

/// <summary>Writes what a mod's blocks ask of players' games into the built assembly: the SDK's manifest task reads it when the project does not say
/// <c>KilnClients</c>. The lowest level that works, so a mod asks no more of players than it must.</summary>
[Generator(LanguageNames.CSharp)]
public sealed class ModClientsGenerator : IIncrementalGenerator
{
    public const string MetadataKey = "Kiln.Clients";

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        var levels = context.SyntaxProvider.CreateSyntaxProvider(
            static (n, _) => n is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Define" } } || n is AssignmentExpressionSyntax { Parent: InitializerExpressionSyntax },
            static (c, _) => c.Node switch
            {
                InvocationExpressionSyntax call => BlockFacts.DefinedType(call, c.SemanticModel) is { } t ? BlockFacts.LevelOf(t) : -1,
                AssignmentExpressionSyntax a => BlockFacts.OwnFiles(a, c.SemanticModel) != null ? BlockFacts.Mod : -1,
                _ => -1,
            }).Where(static l => l >= 0).Collect();
        context.RegisterSourceOutput(levels, static (spc, all) =>
        {
            if (all.IsEmpty) return;
            var text = $"// What this mod's blocks ask of players' games.\n[assembly: global::System.Reflection.AssemblyMetadata(\"{MetadataKey}\", \"{BlockFacts.LevelNames[all.Max()]}\")]\n";
            spc.AddSource("KilnClients.g.cs", SourceText.From(text, Encoding.UTF8));
        });
    }
}
