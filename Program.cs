using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;
using BfresLibrary;
using ZstdSharp;
using ZstdSharp.Unsafe;

namespace BfresMcUpdaterTest;

internal static class Program
{
    private const string InputFileName  = "ModdedTestFile.bfres.mc";
    private const string OutputFileName = "ModdedTestFile.out.bfres.mc";
    private const string RomfsDumpRoot = @"F:\TOTK\1.4.3";

    private static int Main()
    {
        var projectDir = GetProjectDirectory();
        var inputPath = Path.Combine(projectDir, InputFileName);
        var outputPath = Path.Combine(projectDir, OutputFileName);
        var materialDiffPath = Path.Combine(projectDir, "MaterialDiff.json");

        var rom = new RomfsDump(RomfsDumpRoot);
        var proc = new BfresMcProcessor(rom, materialDiffPath);

        var input = File.ReadAllBytes(inputPath);
        var output = proc.ProcessBfresMc(input);

        File.WriteAllBytes(outputPath, output);
        Console.WriteLine($"Written {output.Length} bytes to {outputPath}");
        return 0;
    }

    private static string GetProjectDirectory()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
            if (File.Exists(Path.Combine(dir.FullName, "BfresMcUpdaterTest.csproj")))
                return dir.FullName;
        }

        throw new InvalidOperationException(
            "Could not find BfresMcUpdaterTest.csproj above the application base directory.");
    }
}

internal sealed class RomfsDump(string root)
{
    private readonly string _root = Path.GetFullPath(root);

    public byte[]? TryRead(string relativePath)
    {
        var norm = relativePath.TrimStart('/').Replace('\\', '/');
        var combined = Path.GetFullPath(Path.Combine(_root, norm.Replace('/', Path.DirectorySeparatorChar)));
        
        if (!combined.StartsWith(_root, StringComparison.OrdinalIgnoreCase)) {
            return null;
        }
        
        return !File.Exists(combined) ? null : File.ReadAllBytes(combined);
    }
}

internal sealed class BfresMcProcessor
{
    private const string OptionPreNormal  = "o_expression_pre_normal";
    private const string OptionPostNormal = "o_expression_post_normal";
    private static readonly string[] Options = [OptionPreNormal, OptionPostNormal];
    private const uint McPkMagicLe = 0x4B50434D;
    private const int HeaderSize = 0xC;

    private readonly RomfsDump _rom;
    private readonly Dictionary<string, List<Dictionary<int, long>>> _materialDiff;
    private readonly int[] _materialShaderVersions;
    private bool _stringCacheLoaded;

    public BfresMcProcessor(RomfsDump romfs, string materialDiffJsonPath)
    {
        _rom = romfs;
        _materialDiff = LoadMaterialDiff(materialDiffJsonPath, out _materialShaderVersions);
    }

    private void LoadStringCache()
    {
        if (_stringCacheLoaded) return;
        _stringCacheLoaded = true;
        var bytes = _rom.TryRead("Shader/ExternalBinaryString.bfres.mc");
        if (bytes is { Length: > 0 })
            _ = new ResFile(new MemoryStream(DecompressMcpk(bytes)));
    }

    public byte[] ProcessBfresMc(byte[] mcData) => ProcessBfresMc(new ArraySegment<byte>(mcData));

    private byte[] ProcessBfresMc(ArraySegment<byte> mcData)
    {
        LoadStringCache();

        byte[] bfresData;
        
        try {
            bfresData = DecompressMcpk(mcData);
        }
        catch {
            return mcData.ToArray();
        }

        ResFile resFile;
        
        try {
            resFile = new ResFile(new MemoryStream(bfresData));
        }
        catch {
            return mcData.ToArray();
        }

        if (!TryGetShaderProductVersion(out var userVersion)) {
            return mcData.ToArray();
        }

        var anyApplied = false;
        foreach (var model in resFile.Models.Values) {
            if (model is null) {
                continue;
            }
            
            foreach (var mat in model.Materials.Values) {
                if (mat is null) {
                    continue;
                }
                
                foreach (var opt in Options) {
                    if (!TryGetOptionValue(mat, opt, out var cur)) {
                        continue;
                    }
                    
                    var row = FindDiffRow(opt, cur);
                    
                    if (row is null) {
                        continue;
                    }
                    
                    var next = ValueForVersion(row, userVersion);
                    
                    if (next == cur) {
                        continue;
                    }
                    
                    SetOptionValue(mat, opt, next);
                    anyApplied = true;
                }
            }
        }

        if (!anyApplied) {
            return mcData.ToArray();
        }

        using var ms = new MemoryStream();
        resFile.Save(ms);
        return CompressMcpk(ms.ToArray());
    }

    private bool TryGetShaderProductVersion(out int version)
    {
        foreach (var v in _materialShaderVersions) {
            var name = v.ToString(CultureInfo.InvariantCulture);
            var probe = _rom.TryRead($"Shader/material.Product.{name}.product.Nin_NX_NVN.bfsha.zs");
            
            if (probe is not { Length: > 0 }) {
                continue;
            }
            
            version = v;
            return true;
        }

        version = 0;
        return false;
    }

    private Dictionary<int, long>? FindDiffRow(string optionName, long value)
    {
        return !_materialDiff.TryGetValue(optionName, out var rows)
            ? null
            : rows.FirstOrDefault(row => row.ContainsValue(value));
    }

    private static long ValueForVersion(Dictionary<int, long> row, int userVersion)
    {
        if (row.TryGetValue(userVersion, out var x)) {
            return x;
        }
        
        var best = int.MinValue;
        
        foreach (var k in row.Keys.Where(k => k <= userVersion && k > best)) {
            best = k;
        }
        
        if (best != int.MinValue) {
            return row[best];
        }
        
        best = row.Keys.Prepend(int.MaxValue).Min();
        return row[best];
    }

    private static bool TryGetOptionValue(Material mat, string optionName, out long value)
    {
        value = 0;
        var opts = mat.ShaderAssign?.ShaderOptions;

        if (opts is not { Count: > 0 }) {
            return false;
        }
        
        foreach (var kv in opts) {
            if (string.Equals(kv.Key, optionName, StringComparison.OrdinalIgnoreCase))
                return TryParseOptionString(kv.Value?.String, out value);
        }
        
        return false;
    }

    private static bool TryParseOptionString(string? s, out long value)
    {
        value = 0;
        s = s?.Trim();
        
        if (string.IsNullOrEmpty(s) || s.Equals("<Default Value>", StringComparison.OrdinalIgnoreCase))
            return false;
        
        if (s.Equals("True", StringComparison.OrdinalIgnoreCase)) {
            value = 1;
            return true;
        }
        
        if (s.Equals("False", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hx)) {
            value = unchecked((long)hx);
            return true;
        }
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value)) {
            return true;
        }
        
        if (!ulong.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ul) || ul > long.MaxValue) {
            return false;
        }
        
        value = (long)ul;
        return true;
    }

    private static void SetOptionValue(Material mat, string optionName, long value)
    {
        var opts = mat.ShaderAssign?.ShaderOptions;
        
        if (opts is null) {
            return;
        }
        
        foreach (var kv in opts) {
            if (string.Equals(kv.Key, optionName, StringComparison.OrdinalIgnoreCase)) {
                kv.Value.String = value.ToString(CultureInfo.InvariantCulture);
                return;
            }
        }
    }

    private static Dictionary<string, List<Dictionary<int, long>>> LoadMaterialDiff(string path, out int[] shaderVersions)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream);
        var result = new Dictionary<string, List<Dictionary<int, long>>>(StringComparer.Ordinal);
        var versions = new List<int>();

        foreach (var prop in doc.RootElement.EnumerateObject()) {
            var rows = new List<Dictionary<int, long>>();
            foreach (var item in prop.Value.EnumerateArray()) {
                var row = new Dictionary<int, long>();
                foreach (var kv in item.EnumerateObject()) {
                    var vk = int.Parse(kv.Name, CultureInfo.InvariantCulture);
                    row[vk] = kv.Value.GetInt64();
                    versions.Add(vk);
                }
                rows.Add(row);
            }
            result[prop.Name] = rows;
        }

        shaderVersions = versions.Distinct().OrderDescending().ToArray();
        return result;
    }

    private static byte[] DecompressMcpk(ReadOnlySpan<byte> data)
    {
        if (data.Length < HeaderSize)
            throw new InvalidDataException("MCPK file too small.");
        if (BinaryPrimitives.ReadUInt32LittleEndian(data) != McPkMagicLe)
            throw new InvalidDataException("Not a MCPK file.");

        var flags      = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        var decompSize = (uint)((flags >> 5) << (flags & 0xF));
        if (decompSize == 0)
            throw new InvalidDataException("MCPK header declares zero decompressed size.");

        var output = new byte[(int)decompSize];
        using var dec = new Decompressor();
        dec.SetParameter(ZSTD_dParameter.ZSTD_d_experimentalParam1, (int)ZSTD_format_e.ZSTD_f_zstd1_magicless);
        
        try {
            dec.Unwrap(data[HeaderSize..], output);
        }
        catch (ZstdException) { }
        
        return output;
    }

    private static byte[] CompressMcpk(byte[] src)
    {
        using var ms     = new MemoryStream();
        using var writer = new BinaryWriter(ms);
        writer.Write(McPkMagicLe);
        writer.Write((byte)1); writer.Write((byte)1); writer.Write((byte)0); writer.Write((byte)0);
        writer.Write(GetMcpkFlags((uint)src.Length));
        writer.Write(CompressMagiclessZstd(src));
        return ms.ToArray();
    }

    private static uint GetMcpkFlags(uint decompSize)
    {
        var aligned = (uint)(-decompSize % 0x1000 + 0x1000) % 0x1000;
        decompSize += aligned;
        return ((decompSize >> 0xC) << 5) + 0xC;
    }

    private static byte[] CompressMagiclessZstd(byte[] src)
    {
        using var comp = new Compressor(20);
        comp.SetParameter(ZSTD_cParameter.ZSTD_c_contentSizeFlag, 0);
        comp.SetParameter(ZSTD_cParameter.ZSTD_c_checksumFlag,    0);
        comp.SetParameter(ZSTD_cParameter.ZSTD_c_dictIDFlag,      0);
        comp.SetParameter(ZSTD_cParameter.ZSTD_c_experimentalParam2, 1);
        return comp.Wrap(src).ToArray();
    }
}