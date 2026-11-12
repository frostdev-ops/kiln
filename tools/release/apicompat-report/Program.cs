// Reads the findings of Microsoft.DotNet.ApiCompat.Tool (its suppression-file XML) and prints one line per finding:
//   BREAK <assembly>: <what>: <member> [<rule> <doc id>]          a break on API that is not experimental
//   EXPERIMENTAL <assembly>: <what>: <member> [<rule> <doc id> <KILNX id>]   allowed: [Experimental] in the baseline
//   ADD <assembly>: <member> [<doc id>]                             with --verbose; else one count per assembly
// Experimental: the member, its property or event (for an accessor), or any type around it carries
// System.Diagnostics.CodeAnalysis.ExperimentalAttribute in the BASELINE assembly.
// Exit codes: 0 no break, 1 one or more breaks, 2 bad arguments or unreadable input.
//   ApiCompatReport --baseline-refs <dir,...> --tree-refs <dir,...> --breaks <xml> --adds <xml> [--verbose]
// --breaks: the tool run with the baseline left and the tree right; --adds: the same run with the sides swapped (only its
// missing types and members are read: they are what the tree has and the baseline has not).
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

static class Program
{
    const string ExperimentalAttribute = "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";
    static readonly SymbolDisplayFormat Shown =
        SymbolDisplayFormat.CSharpErrorMessageFormat.AddMemberOptions(SymbolDisplayMemberOptions.IncludeType);
    const string RulesUrl = "https://learn.microsoft.com/dotnet/fundamentals/apicompat/diagnostic-ids";

    static int Main(string[] args)
    {
        string? baseRefs = null, treeRefs = null, breaks = null, adds = null;
        var verbose = false;
        for (var i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--baseline-refs": baseRefs = Next(); break;
                case "--tree-refs": treeRefs = Next(); break;
                case "--breaks": breaks = Next(); break;
                case "--adds": adds = Next(); break;
                case "--verbose": case "-v": verbose = true; break;
                default: Console.Error.WriteLine($"apicompat-report: unknown argument {args[i]}"); return 2;
            }
        }
        if (baseRefs == null || treeRefs == null || breaks == null || adds == null)
        {
            Console.Error.WriteLine("usage: ApiCompatReport --baseline-refs <dir,...> --tree-refs <dir,...> --breaks <xml> --adds <xml> [--verbose]");
            return 2;
        }
        try
        {
            var baseline = Compile("baseline", baseRefs);
            var tree = Compile("tree", treeRefs);
            var nBreaks = 0;
            foreach (var f in Read(breaks))
            {
                var sym = Resolve(baseline, f.Assembly, f.Target);
                var what = What(f.Rule);
                var shown = sym?.ToDisplayString(Shown) ?? f.Target;
                var exp = sym == null ? null : ExperimentalId(sym);
                if (exp != null)
                    Console.WriteLine($"EXPERIMENTAL {f.Assembly}: {what}: {shown} [{f.Rule} {f.Target} {exp}]");
                else
                {
                    Console.WriteLine($"BREAK {f.Assembly}: {what}: {shown} [{f.Rule} {f.Target}]");
                    nBreaks++;
                }
            }
            var added = Read(adds).Where(f => f.Rule is "CP0001" or "CP0002").ToList();
            foreach (var g in added.GroupBy(f => f.Assembly).OrderBy(g => g.Key, StringComparer.Ordinal))
            {
                Console.WriteLine($"ADDED {g.Key}: {g.Count()}");
                if (!verbose) continue;
                foreach (var f in g.OrderBy(f => f.Target, StringComparer.Ordinal))
                {
                    var sym = Resolve(tree, f.Assembly, f.Target);
                    Console.WriteLine($"ADD {f.Assembly}: {sym?.ToDisplayString(Shown) ?? f.Target} [{f.Target}]");
                }
            }
            return nBreaks > 0 ? 1 : 0;
        }
        catch (Exception e) when (e is IOException or ArgumentException or System.Xml.XmlException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"apicompat-report: {e.Message}");
            return 2;
        }
    }

    record Finding(string Rule, string Target, string Assembly);

    static List<Finding> Read(string path)
    {
        if (!File.Exists(path)) return [];  // the tool writes no file when it found nothing
        return XDocument.Load(path).Root!.Elements("Suppression")
            .Select(s => new Finding((string?)s.Element("DiagnosticId") ?? "", (string?)s.Element("Target") ?? "",
                Path.GetFileNameWithoutExtension(((string?)s.Element("Left") ?? "").Trim())))
            .ToList();
    }

    static string What(string rule) => rule switch
    {
        "CP0001" => "type removed",
        "CP0002" => "member removed or its signature changed",
        _ => $"breaking change, see {RulesUrl}",
    };

    static CSharpCompilation Compile(string name, string dirs)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var refs = new List<MetadataReference>();
        foreach (var dir in dirs.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Directory.Exists(dir)) throw new ArgumentException($"no such folder: {dir}");
            foreach (var dll in Directory.EnumerateFiles(dir, "*.dll").Order(StringComparer.Ordinal))
            {
                if (!seen.Add(Path.GetFileName(dll))) continue;  // first folder wins, as with the tool's references
                try { refs.Add(MetadataReference.CreateFromFile(dll)); }
                catch (BadImageFormatException) { }  // a native DLL
            }
        }
        return CSharpCompilation.Create(name, references: refs);
    }

    static ISymbol? Resolve(CSharpCompilation c, string assembly, string docId)
    {
        var syms = DocumentationCommentId.GetSymbolsForDeclarationId(docId, c);
        return syms.FirstOrDefault(s => s.ContainingAssembly?.Name == assembly) ?? (syms.Length == 1 ? syms[0] : null);
    }

    static string? ExperimentalId(ISymbol sym)
    {
        for (ISymbol? s = sym; s != null; s = s.ContainingType)
        {
            if (Attr(s) is { } id) return id;
            if (s is IMethodSymbol { AssociatedSymbol: { } assoc } && Attr(assoc) is { } aid) return aid;
        }
        return null;
    }

    static string? Attr(ISymbol s)
    {
        foreach (var a in s.GetAttributes())
            if (a.AttributeClass?.ToDisplayString() == ExperimentalAttribute)
                return a.ConstructorArguments is [{ Value: string id }, ..] ? id : "experimental";
        return null;
    }
}
