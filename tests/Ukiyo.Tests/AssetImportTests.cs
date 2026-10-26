using System.Globalization;
using System.Text.RegularExpressions;
using Ukiyo.Import;
using Ukiyo.Rendering;
using Ukiyo.Rendering.Software;
using Ukiyo.Samples.TexturedQuad;
using Xunit;

namespace Ukiyo.Tests;

public sealed class AssetImportTests
{
    private static readonly string ArtDir = Path.Combine(CoreTests.RepoRoot(), "samples", "TexturedQuad", "art");

    /// <summary>A small image with distinct, partly translucent texels, so any shift, flip or channel swap shows.</summary>
    private static byte[] Gradient(int width, int height, int seed = 0)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            rgba[i * 4] = (byte)(i * 7 + seed);
            rgba[i * 4 + 1] = (byte)(i * 13 + 40 + seed);
            rgba[i * 4 + 2] = (byte)(255 - i * 3 - seed);
            rgba[i * 4 + 3] = (byte)(i % 3 == 0 ? 255 : 64 + i);
        }

        return rgba;
    }

    private static ImportSource Png(string path, int width, int height, int seed = 0, string? atlas = null) =>
        new(path, PngEncoder.EncodeRgba(Gradient(width, height, seed), width, height), atlas is null ? null : System.Text.Encoding.UTF8.GetBytes(atlas));

    private static ImportResult Import(params ImportSource[] sources) => AtlasImporter.Import(new ImportRequest("TestArt", "Ukiyo.Tests.Gen", sources));

    /// <summary>The bytes the generated C# literally contains, read back from its source text.</summary>
    private static byte[] EmittedRgba(string code)
    {
        var body = code[code.IndexOf("Rgba => new byte[]", StringComparison.Ordinal)..];
        return [.. Regex.Matches(body, "0x([0-9A-F]{2}),").Select(m => byte.Parse(m.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture))];
    }

    private static byte[] Texel(byte[] rgba, int width, int x, int y) => rgba.AsSpan((y * width + x) * 4, 4).ToArray();

    [Fact]
    public void A_single_png_without_extrusion_round_trips_to_the_same_rgba_bytes_in_the_generated_code()
    {
        var source = Png("hero.png", 5, 3);
        var result = AtlasImporter.Import(new ImportRequest("Hero", "Ukiyo.Tests.Gen", [source]) { Extrude = 0 });

        var decoded = PngDecoder.Decode(source.Png);
        Assert.Equal((decoded.Width, decoded.Height), (result.Atlas.Width, result.Atlas.Height));
        Assert.Equal(decoded.Rgba, result.Atlas.Rgba);
        Assert.Equal(decoded.Rgba, EmittedRgba(result.Code));
        Assert.Equal([new PackedFrame("hero", 0, 0, 5, 3)], result.Atlas.Frames);
        Assert.Contains("public static class Hero", result.Code, StringComparison.Ordinal);
    }

    [Fact]
    public void The_checked_in_generated_atlas_holds_exactly_the_source_png_pixels_for_every_frame()
    {
        // QuadArt.g.cs is compiled into this test assembly: compare its compiled bytes with a fresh decode of the art.
        var strip = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(ArtDir, "wisp_float.png")));
        var glass = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(ArtDir, "glass.png")));
        var atlas = QuadArt.Rgba.ToArray();
        Assert.Equal(QuadArt.Width * QuadArt.Height * 4, atlas.Length);
        foreach (var frame in QuadArt.Frames)
        {
            var (image, offsetX) = frame.Name == "glass" ? (glass, 0) : (strip, int.Parse(frame.Name[^1..], CultureInfo.InvariantCulture) * 18);
            for (var y = 0; y < frame.Height; y++)
            {
                for (var x = 0; x < frame.Width; x++)
                {
                    Assert.Equal(Texel(image.Rgba, image.Width, offsetX + x, y), Texel(atlas, QuadArt.Width, frame.X + x, frame.Y + y));
                }
            }
        }
    }

    [Fact]
    public void Importing_the_same_sources_twice_is_byte_identical_and_matches_the_checked_in_file()
    {
        var spec = ImportSpec.Load(Path.Combine(ArtDir, "quad.import.json"));
        var first = AtlasImporter.Import(spec.Request);
        var second = AtlasImporter.Import(ImportSpec.Load(Path.Combine(ArtDir, "quad.import.json")).Request);

        Assert.Equal(first.Code, second.Code);
        Assert.Equal(first.ContentHash, second.ContentHash);
        Assert.DoesNotContain(CoreTests.RepoRoot(), first.Code, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', first.Code);
        // The same check as `ukiyo-import --check`: the committed file is what the art produces today.
        Assert.Equal(File.ReadAllText(spec.OutputPath), first.Code);
        Assert.Equal(QuadArt.ContentHash, first.ContentHash);
    }

    [Fact]
    public void Atlas_layout_packs_tallest_first_then_by_name_with_one_pixel_edge_extrusion()
    {
        var strip = Png("run.png", 8, 4, atlas: """{"frames":{"run_b":{"x":4,"y":0,"w":4,"h":4},"run_a":{"x":0,"y":0,"w":4,"h":4}},"animations":{"run":{"frames":["run_a","run_b"],"fps":10,"loop":false}}}""");
        var tall = Png("tall.png", 2, 6, seed: 9);
        var result = Import(strip, tall);
        var atlas = result.Atlas;

        // Cells (frame + 1 px each side): tall 4x8, run_a 6x6, run_b 6x6. Area 104 -> shelf limit max(6, pow2(11)) = 16:
        // tall at x 0..3, run_a at 4..9, run_b at 10..15 on one shelf; atlas trimmed to 16x8.
        Assert.Equal((16, 8), (atlas.Width, atlas.Height));
        Assert.Equal(
            [new PackedFrame("run_a", 5, 1, 4, 4), new PackedFrame("run_b", 11, 1, 4, 4), new PackedFrame("tall", 1, 1, 2, 6)],
            atlas.Frames);
        var run = Assert.Single(atlas.Animations);
        Assert.Equal(("run", 10f, false), (run.Name, run.FramesPerSecond, run.Loop));
        Assert.Equal(["run_a", "run_b"], run.Frames);

        var source = PngDecoder.Decode(strip.Png);
        // Inside: run_b's top-left texel is source (4,0). Extrusion: the ring repeats the nearest edge texel, corners too.
        Assert.Equal(Texel(source.Rgba, 8, 4, 0), Texel(atlas.Rgba, 16, 11, 1));
        Assert.Equal(Texel(source.Rgba, 8, 4, 0), Texel(atlas.Rgba, 16, 10, 0));
        Assert.Equal(Texel(source.Rgba, 8, 7, 3), Texel(atlas.Rgba, 16, 15, 5));
        Assert.Equal(Texel(source.Rgba, 8, 2, 0), Texel(atlas.Rgba, 16, 5 + 2, 0));
        // Rows below the run cells (y 6..7) are not covered by them: transparent black.
        Assert.Equal(new byte[4], Texel(atlas.Rgba, 16, 8, 7));
    }

    [Fact]
    public void Generated_sheet_has_the_same_frames_and_animations_without_parsing_anything_at_runtime()
    {
        var context = new GameContext();
        var sheet = QuadArt.Load(context);
        Assert.Equal((QuadArt.Width, QuadArt.Height), (sheet.Width, sheet.Height));
        Assert.Equal(["glass", "wisp_float_0", "wisp_float_1", "wisp_float_2", "wisp_float_3"], sheet.Frames.Keys.Order(StringComparer.Ordinal));
        var animation = sheet.Animation("wisp_float");
        Assert.Equal(4, animation.Frames.Count);
        Assert.Equal(6f, animation.FramesPerSecond);
        Assert.Equal(sheet.Frame("wisp_float_1"), animation.At(10));
    }

    [Fact]
    public void Invalid_inputs_fail_with_a_typed_import_error()
    {
        ImportErrorCode Code(Action action) => Assert.Throws<ImportException>(action).Code;

        Assert.Equal(ImportErrorCode.InvalidPng, Code(() => Import(new ImportSource("bad.png", [1, 2, 3, 4]))));
        Assert.Equal(ImportErrorCode.FrameOutOfBounds, Code(() => Import(Png("a.png", 4, 4, atlas: """{"frames":{"f":{"x":2,"y":0,"w":4,"h":4}}}"""))));
        Assert.Equal(ImportErrorCode.InvalidAtlas, Code(() => Import(Png("a.png", 4, 4, atlas: """{"frames":{"f":{"x":0,"y":0,"w":4}}}"""))));
        Assert.Equal(ImportErrorCode.InvalidAtlas, Code(() => Import(Png("a.png", 4, 4, atlas: "{not json"))));
        Assert.Equal(ImportErrorCode.UnknownFrame, Code(() => Import(Png("a.png", 4, 4, atlas: """{"frames":{"f":{"x":0,"y":0,"w":4,"h":4}},"animations":{"r":{"frames":["g"]}}}"""))));
        Assert.Equal(ImportErrorCode.InvalidAtlas, Code(() => Import(Png("a.png", 4, 4, atlas: """{"frames":{"f":{"x":0,"y":0,"w":4,"h":4}},"animations":{"r":{"frames":["f"],"fps":0}}}"""))));
        Assert.Equal(ImportErrorCode.DuplicateName, Code(() => Import(Png("dir1/same.png", 2, 2), Png("dir2/same.png", 2, 2))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => Import(Png("has space.png", 2, 2))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => AtlasImporter.Import(new ImportRequest("lowercase", "Ukiyo.Gen", [Png("a.png", 2, 2)]))));
        Assert.Equal(ImportErrorCode.InvalidName, Code(() => AtlasImporter.Import(new ImportRequest("Art", "Ukiyo..Gen", [Png("a.png", 2, 2)]))));
        Assert.Equal(ImportErrorCode.AtlasTooLarge, Code(() => AtlasImporter.Import(new ImportRequest("Art", "Ukiyo.Gen", [Png("a.png", 16, 2)]) { MaxWidth = 8 })));
        Assert.Equal(ImportErrorCode.InvalidSpec, Code(() => AtlasImporter.Import(new ImportRequest("Art", "Ukiyo.Gen", [Png("a.png", 2, 2)]) { Extrude = 9 })));
        Assert.Equal(ImportErrorCode.InvalidSpec, Code(() => AtlasImporter.Import(new ImportRequest("Art", "Ukiyo.Gen", []))));
        Assert.StartsWith("[IMPORT:InvalidPng]:", Assert.Throws<ImportException>(() => Import(new ImportSource("bad.png", [0]))).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Spec_files_reject_unknown_fields_and_missing_inputs()
    {
        var dir = Directory.CreateTempSubdirectory("ukiyo-import-");
        try
        {
            var spec = Path.Combine(dir.FullName, "x.import.json");
            File.WriteAllText(spec, """{"class":"X","namespace":"N","output":"X.g.cs","inputs":["a.png"],"padding":2}""");
            Assert.Equal(ImportErrorCode.InvalidSpec, Assert.Throws<ImportException>(() => ImportSpec.Load(spec)).Code);
            File.WriteAllText(spec, """{"class":"X","namespace":"N","output":"X.g.cs","inputs":["a.png"]}""");
            Assert.Equal(ImportErrorCode.MissingFile, Assert.Throws<ImportException>(() => ImportSpec.Load(spec)).Code);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task TexturedQuad_draws_the_imported_texels_on_the_cpu_renderer()
    {
        var game = new TexturedQuadGame();
        var runtime = new GameRuntime(game, new SourceIdentity(new Dictionary<string, string>()));
        var extent = new RenderExtent(960, 640, 1f);
        using var renderer = new SoftwareRenderer();
        await runtime.StartAsync(renderer, new RenderConfiguration("test", extent));
        runtime.RenderFrame(extent);
        var capture = PngDecoder.Decode((await runtime.CaptureAsync())!.Png);

        // Tick 0 shows wisp_float_0 centered at (480, 288), 18 texels of 8 px: texel (tx, ty) is centered at
        // (408 + 8 tx + 4, 216 + 8 ty + 4). Opaque texels outside the glass overlay must come back unchanged.
        var frame = QuadArt.Frames.Single(f => f.Name == "wisp_float_0");
        var atlas = QuadArt.Rgba.ToArray();
        var checkedTexels = 0;
        for (var ty = 0; ty < 8; ty++)
        {
            for (var tx = 0; tx < frame.Width; tx++)
            {
                var texel = Texel(atlas, QuadArt.Width, frame.X + tx, frame.Y + ty);
                if (texel[3] != 255)
                {
                    continue;
                }

                Assert.Equal(texel[..3], Texel(capture.Rgba, 960, 408 + 8 * tx + 4, 216 + 8 * ty + 4)[..3]);
                checkedTexels++;
            }
        }

        Assert.True(checkedTexels > 20, $"only {checkedTexels} opaque texels checked");
    }
}
