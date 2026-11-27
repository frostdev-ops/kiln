using System.IO.Compression;
using System.Text;
using Xunit;

namespace XmlDump;

public class Dump
{
    [Fact]
    public void Xml()
    {
        var bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var sb = new StringBuilder();
        foreach (var dir in Directory.GetDirectories(bin).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            var xml = Path.Combine(dir, "release", name + ".xml");
            if (!name.StartsWith("Kiln.") || name.EndsWith(".Tests") || !File.Exists(xml)) continue;
            using var ms = new MemoryStream();
            using (var gz = new GZipStream(ms, CompressionLevel.Optimal, true)) gz.Write(File.ReadAllBytes(xml));
            var b64 = Convert.ToBase64String(ms.ToArray());
            var n = (b64.Length + 6999) / 7000;
            for (var i = 0; i < n; i++) sb.AppendLine($"error XDOC1: {name} {i}/{n} {b64.Substring(i * 7000, Math.Min(7000, b64.Length - i * 7000))}");
        }
        Assert.Fail(sb.ToString());
    }
}
