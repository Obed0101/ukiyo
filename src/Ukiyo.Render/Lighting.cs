using System.Numerics;

namespace Ukiyo.Rendering;

/// <summary>
/// The G1Lit lighting model, written once for C# (docs/rendering-lighting.md). The CPU renderer calls it per pixel;
/// the WGSL in WgpuRenderer and the GLSL in web/three-adapter/lit-material.js are line-by-line copies of
/// <see cref="Shade"/>, in the same operation order, so all renderers quantize the same linear value.
/// </summary>
public static class LightingMath
{
    /// <summary>
    /// Matrix that takes object-space normals to world space with row vectors (<c>Vector3.TransformNormal(n, N)</c>):
    /// the transpose of the inverse of the world matrix's linear part. Equal to the world rotation for rigid transforms.
    /// </summary>
    public static Matrix4x4 NormalMatrix(Matrix4x4 world)
    {
        world.M41 = 0;
        world.M42 = 0;
        world.M43 = 0;
        if (!Matrix4x4.Invert(world, out var inverse))
        {
            throw new RenderException(RenderErrorCode.OutOfRange, "world matrix of a lit instance is not invertible, so its normals are undefined");
        }

        return Matrix4x4.Transpose(inverse);
    }

    /// <summary>Linear RGB a light delivers to a surface facing it: color × intensity.</summary>
    public static Vector3 Radiance(in Light light) => light.Color * light.Intensity;

    /// <summary>
    /// Lambert diffuse: albedo × (ambient + Σ radiance_i × max(N·L_i, 0) × attenuation_i), lights in packet order.
    /// Directional: L = normalize(−direction), attenuation 1. Point: v = position − P, d² = v·v, L = v / √d²,
    /// attenuation = t², t = clamp(1 − d² / range², 0, 1); a point exactly at the light (d² = 0) receives nothing from it.
    /// No clamping here: the sRGB encode at output clamps to [0, 1].
    /// </summary>
    public static Vector3 Shade(Vector3 albedo, Vector3 normal, Vector3 position, SceneLighting lighting)
    {
        var n = Vector3.Normalize(normal);
        var sum = lighting.Ambient;
        foreach (var light in lighting.Lights)
        {
            Vector3 l;
            var attenuation = 1f;
            if (light.Kind == LightKind.Directional)
            {
                l = Vector3.Normalize(-light.PositionOrDirection);
            }
            else
            {
                var v = light.PositionOrDirection - position;
                var d2 = Vector3.Dot(v, v);
                if (!(d2 > 0))
                {
                    continue;
                }

                l = v / MathF.Sqrt(d2);
                var t = Math.Clamp(1f - d2 / (light.Range * light.Range), 0f, 1f);
                attenuation = t * t;
            }

            sum += Radiance(light) * (MathF.Max(Vector3.Dot(n, l), 0f) * attenuation);
        }

        return albedo * sum;
    }
}
