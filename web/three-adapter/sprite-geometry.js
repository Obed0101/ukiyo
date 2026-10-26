// Mirror of src/Ukiyo.Render/SpriteGeometry.cs. Every renderer turns sprites into pixel-space quads with this exact
// math, so the 2D layer lands on the same pixels on wgpu/Metal, the CPU renderer and three.js. Change both together.

export const SpriteSpace = Object.freeze({ World: 1, Screen: 2 });

const CORNERS = [
  [0, 0],
  [1, 0],
  [1, 1],
  [0, 1],
];

/** Corners (top-left, top-right, bottom-right, bottom-left of the image) as { x, y, u, v } in drawing-buffer pixels. */
export function spriteQuad(sprite, camera2D, width, height) {
  const sin = Math.fround(Math.sin(sprite.rotation));
  const cos = Math.fround(Math.cos(sprite.rotation));
  const pixelsPerUnit = height / camera2D.viewHeight;
  return CORNERS.map(([cx, cy]) => {
    const ox = (cx - sprite.pivot[0]) * sprite.size[0];
    const oy = (cy - sprite.pivot[1]) * sprite.size[1];
    let x;
    let y;
    if (sprite.space === SpriteSpace.Screen) {
      x = sprite.position[0] + (ox * cos + oy * sin);
      y = sprite.position[1] + (-ox * sin + oy * cos);
    } else {
      const ux = ox;
      const uy = -oy;
      const wx = sprite.position[0] + (ux * cos - uy * sin);
      const wy = sprite.position[1] + (ux * sin + uy * cos);
      x = (wx - camera2D.center[0]) * pixelsPerUnit + width * 0.5;
      y = height * 0.5 - (wy - camera2D.center[1]) * pixelsPerUnit;
    }
    const [u0, v0, u1, v1] = sprite.uv;
    return { x, y, u: u0 + (u1 - u0) * cx, v: v0 + (v1 - v0) * cy };
  });
}

/** Draw order: world before screen, then layer, then submission order (stable). */
export function spriteDrawOrder(sprites) {
  return sprites
    .map((_, i) => i)
    .sort((a, b) => sprites[a].space - sprites[b].space || sprites[a].layer - sprites[b].layer || a - b);
}
