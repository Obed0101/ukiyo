// G1Lit material for the three.js adapter. three's built-in lights use a different BRDF (1/π Lambert, physical
// intensities), so lit materials are a ShaderMaterial whose fragment shader is LightingMath.Shade from
// src/Ukiyo.Render/Lighting.cs and fs_lit in WgpuRenderer.cs, in the same operation order (docs/rendering-lighting.md).
// It writes linear color; the adapter's SRGB8_ALPHA8 target encodes it, like the unlit MeshBasicMaterial path.
import * as THREE from "three";
import { LightKind, MAX_LIGHTS } from "./protocol.js";

const vertexShader = /* glsl */ `
uniform vec3 uBaseColor;
uniform float uUseVertexColors;
varying vec3 vAlbedo;
varying vec3 vWorldPosition;
varying vec3 vNormal;

void main() {
  vAlbedo = (uUseVertexColors != 0.0 ? color : vec3(1.0)) * uBaseColor;
  vWorldPosition = (modelMatrix * vec4(position, 1.0)).xyz;
  vNormal = transpose(inverse(mat3(modelMatrix))) * normal;
  gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}
`;

const fragmentShader = /* glsl */ `
#define MAX_LIGHTS ${MAX_LIGHTS}
uniform vec3 uAmbient;
uniform int uLightCount;
uniform vec4 uLightVector[MAX_LIGHTS];
uniform vec4 uLightRadiance[MAX_LIGHTS];
varying vec3 vAlbedo;
varying vec3 vWorldPosition;
varying vec3 vNormal;

void main() {
  vec3 n = normalize(vNormal);
  vec3 sum = uAmbient;
  for (int i = 0; i < MAX_LIGHTS; i++) {
    if (i >= uLightCount) break;
    vec4 light = uLightVector[i];
    vec4 radiance = uLightRadiance[i];
    vec3 l;
    float attenuation = 1.0;
    if (light.w == 1.0) {
      l = normalize(-light.xyz);
    } else {
      vec3 v = light.xyz - vWorldPosition;
      float d2 = dot(v, v);
      if (!(d2 > 0.0)) continue;
      l = v / sqrt(d2);
      float t = clamp(1.0 - d2 / (radiance.w * radiance.w), 0.0, 1.0);
      attenuation = t * t;
    }
    sum = sum + radiance.rgb * (max(dot(n, l), 0.0) * attenuation);
  }
  gl_FragColor = vec4(vAlbedo * sum, 1.0);
}
`;

/** One set of scene-light uniforms shared by every lit material, rewritten from each frame's lighting section. */
export function createSceneLightUniforms() {
  return {
    uAmbient: { value: new THREE.Vector3() },
    uLightCount: { value: 0 },
    uLightVector: { value: Array.from({ length: MAX_LIGHTS }, () => new THREE.Vector4()) },
    uLightRadiance: { value: Array.from({ length: MAX_LIGHTS }, () => new THREE.Vector4()) },
  };
}

/** Same packing as WgpuRenderer.WriteLights: (vector, kind) and (color × intensity, range). */
export function writeSceneLights(uniforms, lighting) {
  if (!lighting) {
    uniforms.uLightCount.value = 0;
    uniforms.uAmbient.value.set(0, 0, 0);
    return;
  }
  const [ar, ag, ab] = lighting.ambient;
  uniforms.uAmbient.value.set(ar, ag, ab);
  uniforms.uLightCount.value = lighting.lights.length;
  lighting.lights.forEach((light, i) => {
    // color and intensity are float32; their product is exact in float64, so rounding it to float32 on upload gives
    // the same value as the float32 multiply in C# (LightingMath.Radiance).
    const kind = light.kind === LightKind.Directional ? 1 : 2;
    uniforms.uLightVector.value[i].set(light.vector[0], light.vector[1], light.vector[2], kind);
    uniforms.uLightRadiance.value[i].set(light.color[0] * light.intensity, light.color[1] * light.intensity, light.color[2] * light.intensity, light.range);
  });
}

/** A lit material: base color and the vertex-color switch are per material, the scene lights are shared. */
export function createLitMaterial(baseColor, useVertexColors, sceneLights) {
  const material = new THREE.ShaderMaterial({
    uniforms: {
      ...sceneLights,
      uBaseColor: { value: new THREE.Vector3(baseColor[0], baseColor[1], baseColor[2]) },
      uUseVertexColors: { value: useVertexColors ? 1 : 0 },
    },
    vertexShader,
    fragmentShader,
    vertexColors: true,
    side: THREE.FrontSide,
  });
  material.userData.lit = true;
  return material;
}
