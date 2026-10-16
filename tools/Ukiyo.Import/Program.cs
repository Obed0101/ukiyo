// ukiyo-import: turns source art into C# that games compile in (see docs/assets.md).
//   dotnet run --project tools/Ukiyo.Import -- <spec.import.json | folder>...          write generated files
//   dotnet run --project tools/Ukiyo.Import -- --check <spec.import.json | folder>...  fail if any is stale
// Exit codes: 0 ok, 1 import error or stale output, 2 usage.
using Ukiyo.Import;

var check = args.Contains("--check", StringComparer.Ordinal);
var targets = args.Where(a => a != "--check").ToList();
if (targets.Count == 0 || targets.Any(a => a.StartsWith("--", StringComparison.Ordinal)))
{
    Console.Error.WriteLine("usage: ukiyo-import [--check] <spec.import.json | folder>...");
    return 2;
}

try
{
    var specs = targets.SelectMany(t => Directory.Exists(t) ? ImportSpec.Find(t) : [t]).ToList();
    if (specs.Count == 0)
    {
        throw new ImportException(ImportErrorCode.MissingFile, $"no *{ImportSpec.Suffix} under {string.Join(", ", targets)}");
    }

    foreach (var specPath in specs)
    {
        var spec = ImportSpec.Load(specPath);
        var result = AtlasImporter.Import(spec.Request);
        var current = File.Exists(spec.OutputPath) ? File.ReadAllText(spec.OutputPath) : null;
        var summary = $"{result.Atlas.Width}x{result.Atlas.Height}, {result.Atlas.Frames.Count} frames, {result.Atlas.Animations.Count} animations, content {result.ContentHash[..12]}";
        var output = Path.GetRelativePath(Environment.CurrentDirectory, spec.OutputPath);
        if (current == result.Code)
        {
            Console.WriteLine($"[import] up to date {output} ({summary})");
        }
        else if (check)
        {
            throw new ImportException(ImportErrorCode.Stale, $"{output} is {(current is null ? "missing" : "out of date")}; run: {spec.Request.RegenerateCommand}");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(spec.OutputPath)!);
            File.WriteAllText(spec.OutputPath, result.Code);
            Console.WriteLine($"[import] wrote {output} ({summary})");
        }
    }

    return 0;
}
catch (ImportException error)
{
    Console.Error.WriteLine(error.Message);
    return 1;
}
