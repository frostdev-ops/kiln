// SPDX-License-Identifier: MIT
// psparse parse <file>...               PowerShell's own parser: every syntax error with its line; exit 1 on any
// psparse run <script> [-Name value]... the script in a fresh runspace ("$true" passes a switch); its host, warning and
//                                       error output on stdout; exit 1 when it threw or wrote an error
using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;

if (args.Length >= 2 && args[0] == "parse") {
    int bad = 0;
    foreach (var f in args.Skip(1)) {
        Parser.ParseFile(f, out Token[] tokens, out ParseError[] errors);
        foreach (var e in errors) { Console.WriteLine($"{f}:{e.Extent.StartLineNumber}: {e.Message}"); bad++; }
        Console.WriteLine($"{Path.GetFileName(f)}: {tokens.Length} tokens, {errors.Length} parse errors");
    }
    return bad == 0 ? 0 : 1;
}
if (args.Length >= 2 && args[0] == "run") {
    using var rs = RunspaceFactory.CreateRunspace(InitialSessionState.CreateDefault2());
    rs.Open();
    using var ps = PowerShell.Create(rs);
    ps.AddCommand(args[1]);
    for (int i = 2; i + 1 < args.Length; i += 2) {
        if (args[i + 1] == "$true") ps.AddParameter(args[i].TrimStart('-'), true);
        else ps.AddParameter(args[i].TrimStart('-'), args[i + 1]);
    }
    ps.Streams.Information.DataAdded += (s, e) => Console.WriteLine("info: " + ps.Streams.Information[e.Index]);
    ps.Streams.Warning.DataAdded += (s, e) => Console.WriteLine("warning: " + ps.Streams.Warning[e.Index]);
    try {
        foreach (var o in ps.Invoke()) Console.WriteLine("out: " + o);
    } catch (Exception ex) {
        Console.WriteLine("exception: " + ex.Message);
        return 1;
    }
    foreach (var e in ps.Streams.Error) Console.WriteLine("error: " + e);
    return ps.Streams.Error.Count > 0 ? 1 : 0;
}
Console.WriteLine("usage: psparse parse <file>... | run <script> [-Name value]...");
return 2;
