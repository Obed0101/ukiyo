using Xunit;

namespace Ukiyo.Tests;

/// <summary>
/// The game layout (templates/game, docs/game-layout.md) is a convention, not a build rule. These checks keep the
/// template, the migrated RotatingCube sample and every game that adopts the layout honest about it.
/// </summary>
public sealed class GameLayoutTests
{
    private static readonly string[] RequiredEntries =
    [
        "Shared/Program.cs", "Shared/Game.cs", "Shared/Scenes", "Shared/Entities", "Shared/Systems", "Shared/Lib",
        "Shared/gen", "Shared/assets", "Platform/Desktop", "Platform/Browser", "Hosts/Headless", "Hosts/Desktop",
        "Hosts/Browser", "Hosts/Export.Desktop", "Www/index.html", "Www/main.js",
    ];

    private static string Template => Path.Combine(CoreTests.RepoRoot(), "templates", "game");

    private static string RotatingCube => Path.Combine(CoreTests.RepoRoot(), "samples", "RotatingCube");

    /// <summary>Every game folder under samples/ or games/ that uses the layout (it has Hosts/), plus the template.</summary>
    public static TheoryData<string> LayoutGames()
    {
        var data = new TheoryData<string> { "templates/game" };
        foreach (var root in new[] { "samples", "games" })
        {
            var dir = Path.Combine(CoreTests.RepoRoot(), root);
            if (!Directory.Exists(dir))
            {
                continue;
            }

            foreach (var game in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                if (Directory.Exists(Path.Combine(game, "Hosts")))
                {
                    data.Add($"{root}/{Path.GetFileName(game)}");
                }
            }
        }

        return data;
    }

    [Fact]
    public void Template_has_every_folder_of_the_layout()
    {
        foreach (var entry in RequiredEntries)
        {
            var path = Path.Combine(Template, entry);
            Assert.True(File.Exists(path) || Directory.Exists(path), $"templates/game/{entry} is missing");
        }
    }

    [Fact]
    public void RotatingCube_is_the_template_with_its_names_filled_in()
    {
        var templateFiles = LayoutFiles(Template);
        var sampleFiles = LayoutFiles(RotatingCube);
        Assert.Equal(templateFiles.Select(ToRotatingCube), sampleFiles);
        foreach (var file in templateFiles)
        {
            var expected = ToRotatingCube(File.ReadAllText(Path.Combine(Template, file)));
            var actual = File.ReadAllText(Path.Combine(RotatingCube, ToRotatingCube(file)));
            Assert.True(expected == actual, $"samples/RotatingCube/{ToRotatingCube(file)} differs from templates/game/{file} after renaming");
        }
    }

    [Theory]
    [MemberData(nameof(LayoutGames))]
    public void Hosts_hold_one_project_each_and_link_the_shared_folder(string game)
    {
        var dir = Path.Combine(CoreTests.RepoRoot(), game);
        foreach (var host in Directory.GetDirectories(Path.Combine(dir, "Hosts")))
        {
            var name = Path.GetFileName(host);
            var files = LayoutFiles(host);
            Assert.True(files.Length == 1 && files[0].EndsWith($".{name}.csproj", StringComparison.Ordinal), $"{game}/Hosts/{name} must hold only <Game>.{name}.csproj, found: {string.Join(", ", files)}");

            var project = File.ReadAllText(Path.Combine(host, files[0]));
            Assert.Contains("<UkiyoSharedSource Include=\"$(UkiyoGameDir)Shared/**/*.cs\" />", project, StringComparison.Ordinal);
            Assert.Contains("<UkiyoGameDir>$([MSBuild]::NormalizeDirectory('$(MSBuildProjectDirectory)', '..', '..'))</UkiyoGameDir>", project, StringComparison.Ordinal);
            var platform = name switch { "Desktop" or "Export.Desktop" => "Desktop", "Browser" => "Browser", _ => null };
            foreach (var folder in new[] { "Desktop", "Browser" })
            {
                var linked = project.Contains($"$(UkiyoGameDir)Platform/{folder}/**/*.cs", StringComparison.Ordinal);
                Assert.True(linked == (folder == platform), $"{game}/Hosts/{name} {(linked ? "must not" : "must")} compile Platform/{folder}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(LayoutGames))]
    public void Shared_code_has_no_preprocessor_branches_or_platform_interop(string game)
    {
        var shared = Path.Combine(CoreTests.RepoRoot(), game, "Shared");
        foreach (var file in Directory.GetFiles(shared, "*.cs", SearchOption.AllDirectories))
        {
            var code = File.ReadAllLines(file).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)).ToArray();
            var relative = Path.GetRelativePath(shared, file);
            Assert.False(code.Any(line => line.TrimStart().StartsWith("#if", StringComparison.Ordinal)), $"{game}/Shared/{relative} has #if; host-specific code goes in Platform/<Host>/");
            foreach (var forbidden in new[] { "JSImport", "JSExport", "LibraryImport", "DllImport", "OperatingSystem.Is" })
            {
                Assert.False(code.Any(line => line.Contains(forbidden, StringComparison.Ordinal)), $"{game}/Shared/{relative} uses {forbidden}; host-specific code goes in Platform/<Host>/");
            }
        }
    }

    private static string ToRotatingCube(string text) =>
        text.Replace("Ukiyo.Games.MyGame", "Ukiyo.Samples.RotatingCube", StringComparison.Ordinal)
            .Replace("MyGame", "RotatingCube", StringComparison.Ordinal)
            .Replace("my-game", "rotating-cube", StringComparison.Ordinal);

    /// <summary>Files of a game folder relative to it, without build output and the synced web adapter.</summary>
    private static string[] LayoutFiles(string dir) =>
        Directory.GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(dir, file).Replace('\\', '/'))
            .Where(file => !file.Split('/').Any(part => part is "bin" or "obj") && !file.StartsWith("Www/lib/", StringComparison.Ordinal) && Path.GetFileName(file) != ".DS_Store")
            .Order(StringComparer.Ordinal)
            .ToArray();
}
