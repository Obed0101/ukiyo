# Builds the ImportedMesh source models in Blender and exports them with Blender's own glTF 2.0 exporter, so the
# importer is exercised on real exporter output rather than on hand-written JSON. Nothing here is drawn by hand:
#   suzanne.gltf + suzanne.bin  Blender's built-in "Suzanne" primitive, smooth shaded, with a height gradient painted
#                               into a linear float color attribute (exported as COLOR_0) and no material.
#   pedestal.glb                an 8-sided cylinder, flat shaded, with a single-sided grey material and a node
#                               rotation and non-uniform scale (baked by the importer).
# Run from the repository root (Blender 5.1 was used for the committed files):
#   /Applications/Blender.app/Contents/MacOS/Blender -b --factory-startup --python samples/ImportedMesh/art/export_models.py
# then regenerate the C#: dotnet run --project tools/Ukiyo.Import -- samples/ImportedMesh/art
import math
import os

import bpy

HERE = os.path.dirname(os.path.abspath(__file__))
COOL = (0.06, 0.16, 0.55)
WARM = (0.92, 0.45, 0.12)


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def export(obj, filename, fmt):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(
        filepath=os.path.join(HERE, filename),
        export_format=fmt,
        use_selection=True,
        export_yup=True,
        export_texcoords=False,
        export_normals=True,
        export_tangents=False,
        export_vertex_color="ACTIVE",
        export_materials="EXPORT",
        export_animations=False,
        export_skins=False,
        export_morph=False,
        export_cameras=False,
        export_lights=False,
        export_extras=False,
        export_apply=False,
    )


def suzanne():
    reset()
    bpy.ops.mesh.primitive_monkey_add(size=1.0, calc_uvs=False)
    obj = bpy.context.active_object
    obj.name = "Suzanne"
    bpy.ops.object.shade_smooth()
    mesh = obj.data
    zs = [v.co.z for v in mesh.vertices]
    low, high = min(zs), max(zs)
    colors = mesh.color_attributes.new(name="Color", type="FLOAT_COLOR", domain="POINT")
    for vertex in mesh.vertices:
        t = (vertex.co.z - low) / (high - low)
        colors.data[vertex.index].color = (*[c + (w - c) * t for c, w in zip(COOL, WARM)], 1.0)
    mesh.color_attributes.active_color = colors
    export(obj, "suzanne.gltf", "GLTF_SEPARATE")


def pedestal():
    reset()
    bpy.ops.mesh.primitive_cylinder_add(vertices=8, radius=0.62, depth=0.3, location=(0.0, 0.0, -0.12))
    obj = bpy.context.active_object
    obj.name = "Pedestal"
    obj.rotation_euler = (0.0, 0.0, math.radians(22.5))
    obj.scale = (1.0, 1.0, 0.8)
    material = bpy.data.materials.new("Stone")
    material.use_backface_culling = True
    material.diffuse_color = (0.55, 0.52, 0.48, 1.0)
    if material.node_tree is not None:
        material.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (0.55, 0.52, 0.48, 1.0)
    obj.data.materials.append(material)
    export(obj, "pedestal.glb", "GLB")


suzanne()
pedestal()
