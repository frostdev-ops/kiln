// SPDX-License-Identifier: MIT
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace Kiln.Host;

internal static class RegistryDeclarations
{
    internal static RegistryEntryPoint PointsOf(Type type)
    {
        var method = type.GetMethods(BindingFlags.Instance | BindingFlags.Public).FirstOrDefault(m =>
            m.Name == nameof(Mod.OnRegistry) && m.GetParameters() is [{ ParameterType: var p }] && p == typeof(RegistryContext) &&
            m.IsVirtual && m.GetBaseDefinition().DeclaringType == typeof(Mod) && m.DeclaringType != typeof(Mod));
        CustomAttributeData? declaration = null;
        for (var t = type; t is not null && t != typeof(Mod) && declaration is null; t = t.BaseType)
            declaration = t.GetCustomAttributesData().FirstOrDefault(a => a.AttributeType == typeof(RegistryEntryPointsAttribute));
        if (declaration is not null)
        {
            if (method is null) throw new InvalidDataException("registry entry points were declared without an OnRegistry override");
            var value = Convert.ToInt64(declaration.ConstructorArguments[0].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (value < 0 || value > 15 || !RegistryEntryPointsAttribute.Valid((RegistryEntryPoint)value))
                throw new InvalidDataException("registry entry points contain zero, Manual or an unknown flag");
            return (RegistryEntryPoint)value;
        }
        return method is null ? 0 : RegistryEntryPoint.GameStart;
    }

    internal static Assembly? GameStartExport(string mod, IReadOnlyDictionary<string, string> exports, AssemblyName name)
    {
        if (name.Name is { } simple && exports.TryGetValue(simple + ".dll", out var dependency))
            throw new RegistryContextException(RegistryContextError.RuntimeExportUnavailable,
                $"GameStart author {mod} cannot use runtime export {simple} from {dependency}; declare ModLoad or later instead (dependency edits still run first)");
        return null; // private assemblies from the author's own folder remain ordinary dependencies
    }

    // review4 recheck nit (NS1): a mod that only calls Registries.Change, Registries.Read or Registries.Ready (Kiln.Registry's
    // static entry class) never names RegistryContext, so MightOverride misses it, and its first use decoded the base on
    // the tick thread. The order's "registry": true flag asks for either; GameStart authors stay MightOverride's. The entry
    // class has two names (review of api-shape): Registries since 0.2.0-dev.32, Registry (its forwarder) before.
    private static readonly string[] EntryClasses = ["Registries", "Registry"];
    internal static bool UsesRegistry(string assembly, IEnumerable<string> directories)
    {
        try { if (!File.Exists(assembly)) return false; }  // no entry assembly: no code to use the API
        catch (Exception) { return false; }
        var dirs = directories as string[] ?? directories.ToArray();
        if (MightOverride(assembly, dirs)) return true;
        // the entry assembly, then the other DLLs of its own folder (a helper of the mod that makes the calls)
        if (References(assembly, "Kiln.Registry", "Kiln.Registry", EntryClasses)) return true;
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(assembly));
            if (folder is null) return false;
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
                if (!string.Equals(Path.GetFullPath(dll), Path.GetFullPath(assembly), StringComparison.OrdinalIgnoreCase) &&
                    References(dll, "Kiln.Registry", "Kiln.Registry", EntryClasses)) return true;
        }
        catch (Exception) { /* an unreadable folder: the entry assembly decided */ }
        return false;
    }
    private static bool References(string file, string assemblyName, string ns, string[] names)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var md = pe.GetMetadataReader();
            foreach (var handle in md.TypeReferences)
            {
                var type = md.GetTypeReference(handle);
                if (md.GetString(type.Namespace) == ns && Array.IndexOf(names, md.GetString(type.Name)) >= 0 &&
                    type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                    md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == assemblyName) return true;
            }
        }
        catch (Exception) { /* An unreadable path or malformed metadata is not a registry user. */ }
        return false;
    }

    // Old mods have no reference to this newly introduced API type. Inspect the PE, not their constructors or initializers.
    // The directory search also follows an inherited author in a private/exported dependency assembly.
    internal static bool MightOverride(string assembly, IEnumerable<string> directories)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Inspect(string file)
        {
            try
            {
                if (!seen.Add(Path.GetFullPath(file))) return false;
                using var stream = File.OpenRead(file);
                using var pe = new PEReader(stream);
                if (!pe.HasMetadata) return false;
                var md = pe.GetMetadataReader();
                foreach (var handle in md.TypeReferences)
                {
                    var type = md.GetTypeReference(handle);
                    if (md.GetString(type.Namespace) == "Kiln" && md.GetString(type.Name) == nameof(RegistryContext) &&
                        type.ResolutionScope.Kind == HandleKind.AssemblyReference &&
                        md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope).Name) == "Kiln.Api") return true;
                }
                foreach (var handle in md.TypeDefinitions)
                {
                    var type = md.GetTypeDefinition(handle);
                    var baseType = type.BaseType;
                    if (baseType.Kind == HandleKind.TypeSpecification)
                    {
                        // A generic external base is encoded as GENERICINST CLASS/VALUETYPE TypeDefOrRef, then its arguments.
                        var signature = md.GetBlobReader(md.GetTypeSpecification((TypeSpecificationHandle)baseType).Signature);
                        if (signature.ReadByte() != 0x15 || signature.ReadByte() is not (0x11 or 0x12)) continue;
                        baseType = signature.ReadTypeHandle();
                    }
                    if (baseType.Kind != HandleKind.TypeReference) continue;
                    var parent = md.GetTypeReference((TypeReferenceHandle)baseType);
                    if (parent.ResolutionScope.Kind != HandleKind.AssemblyReference) continue;
                    var name = md.GetString(md.GetAssemblyReference((AssemblyReferenceHandle)parent.ResolutionScope).Name);
                    if (name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal) || name == "Kiln.Api") continue;
                    foreach (var directory in directories)
                    {
                        var candidate = Path.Combine(directory, name + ".dll");
                        if (File.Exists(candidate) && Inspect(candidate)) return true;
                    }
                }
            }
            catch (Exception) { /* An unreadable path or malformed metadata is not a registry author. */ }
            return false;
        }
        return Inspect(assembly);
    }
}
