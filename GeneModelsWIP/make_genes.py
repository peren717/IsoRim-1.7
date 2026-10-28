# Builds IsoRim-style gene part models (Biotech) in Blender and shows them on one of IsoRim's own 3D people.
# Parts are built in IsoRim's figure space (as Models/person_head.obj: one unit = one map cell, +Y up, -Z = the face's
# side in the .obj; in Blender +Y is the face's side and +Z is up), so they sit where they would on a figure of size 1.
# Skin parts use stuff_plain (the material IsoRim tints with the skin, as the head), the tail stuff_plain too (to be
# tinted with the hair colour), horns an_horn.
import math
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(HERE, "..", "WeaponModelsWIP"))   # make_weapons.py, in the repo
import make_weapons as mw   # noqa: E402  (the Model class, export and the material colours)
import bpy                  # noqa: E402
from mathutils import Matrix, Vector   # noqa: E402

MODELS_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Models")
if not os.path.isdir(MODELS_DIR):
    MODELS_DIR = "/home/user/IsoRim-1.7/Models"

SKIN = (0.93, 0.78, 0.68)     # preview only: a pale skin, as the pigskin villager's
HAIR = (0.40, 0.26, 0.16)     # preview only: brown hair


def placed(m, matrix):
    m.xf = lambda v: tuple(matrix @ Vector(v))


def frustum(m, mat, p0, p1, r0, r1, sides=8):
    # A tapered tube from p0 to p1 (any direction), radius r0 to r1.
    p0, p1 = Vector(p0), Vector(p1)
    d = p1 - p0
    rot = d.normalized().to_track_quat("Z", "Y").to_matrix().to_4x4()
    placed(m, Matrix.Translation(p0) @ rot)
    m.tube(mat, "z", (0.0, 0.0, 0.0), d.length, r0, r1, sides=sides)
    m.xf = None


# ---------------------------------------------------------------------------------------------------------------------

def pig_ears():
    # Two big pointed pig ears standing out from the top sides of the head (outside the hair), leaning out and a little
    # forward, with a lighter (pink) inner face toward the front.
    m = mw.Model("gene_pig_ears")
    leaf = [(-0.080, 0.0), (-0.088, 0.080), (-0.058, 0.160), (0.0, 0.232), (0.042, 0.160), (0.080, 0.070), (0.074, 0.0)]
    inner = [(w * 0.62, l * 0.78 + 0.020) for w, l in leaf]
    for side in (1, -1):
        mat = (Matrix.Translation((side * 0.185, 0.030, 1.190)) @ Matrix.Rotation(math.radians(12), 4, "X")
               @ Matrix.Rotation(math.radians(-side * 32), 4, "Y"))
        placed(m, mat)
        # the leaf stands in local x (width) / z (length), thickness along local y (the face's side +y)
        m.prism("stuff_plain", [(w, l) for w, l in reversed(leaf)], -0.014, 0.014)
        m.prism("stuff_plain_light", [(w, l) for w, l in reversed(inner)], 0.014, 0.019)
    m.xf = None
    return m


def pig_nose():
    # A big oval snout on the middle of the face, its flat front lighter, with two dark nostrils.
    m = mw.Model("gene_pig_nose")
    placed(m, Matrix.Translation((0.0, 0.175, 0.880)) @ Matrix.Diagonal((0.092, 1.0, 0.066, 1.0)))
    m.tube("stuff_plain", "y", (0.0, 0.0, 0.0), 0.085, 1.0, 0.95, sides=12)
    placed(m, Matrix.Translation((0.0, 0.260, 0.880)) @ Matrix.Diagonal((0.084, 1.0, 0.060, 1.0)))
    m.tube("stuff_plain_light", "y", (0.0, 0.0, 0.0), 0.004, 1.0, sides=12)       # the flat front
    for x in (-0.38, 0.38):
        placed(m, Matrix.Translation((x * 0.092, 0.264, 0.880)) @ Matrix.Diagonal((0.018, 1.0, 0.026, 1.0)))
        m.tube("an_dark", "y", (0.0, 0.0, 0.0), 0.004, 1.0, sides=8)
    m.xf = None
    return m


def mini_horns():
    # Two small horns from the forehead, curving out and up (the gene icon's shape).
    m = mw.Model("gene_mini_horns")
    for side in (1, -1):
        pts = [(side * 0.075, 0.150, 1.165), (side * 0.120, 0.150, 1.205), (side * 0.155, 0.135, 1.250),
               (side * 0.170, 0.115, 1.300), (side * 0.168, 0.095, 1.335)]
        radii = [0.034, 0.028, 0.021, 0.013, 0.004]
        for i in range(len(pts) - 1):
            frustum(m, "an_horn", pts[i], pts[i + 1], radii[i], radii[i + 1], sides=8)
    return m


def furry_tail():
    # A thick, fluffy tail from the lower back, sweeping back and curling up (the gene icon's crescent).
    m = mw.Model("gene_furry_tail")
    pts = [(0.0, -0.100, 0.250), (0.015, -0.190, 0.205), (0.040, -0.285, 0.215), (0.060, -0.355, 0.280),
           (0.068, -0.390, 0.370), (0.060, -0.395, 0.460), (0.045, -0.375, 0.530)]
    radii = [0.040, 0.062, 0.078, 0.080, 0.070, 0.050, 0.012]
    for i in range(len(pts) - 1):
        frustum(m, "stuff_plain", pts[i], pts[i + 1], radii[i], radii[i + 1], sides=10)
    return m


def furskin(build, part, src_name=None, name=None, keep=None, out=0.016, top_tufts=False):
    # Yttakin fur over IsoRim's body part person_<build>_<part>: a shell a little out from the body (covering the
    # skin), and on it a shaggy coat of small tufts hanging slightly down. stuff_plain: tinted with the hair colour.
    import bmesh
    import random
    import zlib
    rnd = random.Random(zlib.crc32((build + part).encode()))   # the same coat on every run
    src = import_obj(os.path.join(MODELS_DIR, (src_name or "person_%s_%s" % (build, part)) + ".obj"), "src")
    bm = bmesh.new()
    bm.from_mesh(src.data)
    bm.transform(src.matrix_world)   # the importer turns the object (OBJ +Y up), not its vertices
    bpy.data.objects.remove(src)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bmesh.ops.subdivide_edges(bm, edges=bm.edges, cuts=1, use_grid_fill=True)
    bm.normal_update()
    centre = sum((v.co for v in bm.verts), Vector()) / len(bm.verts)
    m = mw.Model(name or "gene_furskin_%s_%s" % (build, part))
    if keep:
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if not keep(f)], context="FACES")
    shell = {}
    for v in bm.verts:
        n = v.normal if v.normal.length > 0 else (v.co - centre).normalized()
        shell[v.index] = tuple(v.co + n * out)
    for f in bm.faces:
        idx = [v.index for v in f.verts]
        m._faces([shell[i] for i in idx], [tuple(range(len(idx)))], "stuff_plain")
    down = Vector((0.0, 0.0, -1.0))
    for f in bm.faces:
        n = f.normal
        if abs(n.z) > 0.85 and not (top_tufts and n.z > 0):
            continue   # no tufts on the flat top and bottom caps
        c = f.calc_center_median() + n * out
        base = [c + (v.co + n * out - c) * 0.85 for v in f.verts]
        length = 0.010 + rnd.random() * 0.010
        hang = down if n.z < 0.85 else Vector((0.0, -1.0, 0.0))   # on top of the head: lying back
        tip = c + n * length + hang * (0.040 + rnd.random() * 0.025)   # long and flat: the coat hangs down
        k = len(base)
        verts = [tuple(b) for b in base] + [tuple(tip)]
        faces = [tuple(range(k - 1, -1, -1))] + [(i, (i + 1) % k, k) for i in range(k)]
        m._faces(verts, faces, "stuff_plain")
    bm.free()
    return m


def head_fur():
    # Yttakin fur on the head, as a mane: the top, back and sides of IsoRim's head (person_head) down round the cheeks
    # to the jaw; the face (eyes, nose, mouth: the front from the chin to the brow) and under the chin stay open.
    def keep(f):
        c, n = f.calc_center_median(), f.normal
        if n.z < -0.45:
            return False                                   # under the chin and the neck
        if n.y > 0.30 and c.z < 1.075 and abs(c.x) < 0.17:
            return False                                   # the face
        return True
    return furskin("head", "", src_name="person_head", name="gene_furskin_head", keep=keep, out=0.014, top_tufts=True)


BUILDS = ["male", "female", "thin", "fat", "hulk"]
FUR = [(lambda b=b, p=p: furskin(b, p)) for b in BUILDS for p in ("upper", "lower")]


PARTS = [pig_ears, pig_nose, mini_horns, furry_tail]


# ---------------------------------------------------------------------------------------------------------------------
# The full picture: IsoRim's own male figure (upper and lower body, head, face, short hair) wearing the parts.

def import_obj(path, name):
    bpy.ops.wm.obj_import(filepath=path, forward_axis="NEGATIVE_Z", up_axis="Y")
    ob = bpy.context.selected_objects[0]
    ob.name = name
    return ob


def preview_material(name, rgb, glow=False):
    m = bpy.data.materials.get(name) or bpy.data.materials.new(name)
    m.use_nodes = True
    b = m.node_tree.nodes.get("Principled BSDF")
    lin = tuple(v ** 2.2 for v in rgb)
    b.inputs["Base Color"].default_value = lin + (1.0,)
    b.inputs["Roughness"].default_value = 0.6
    return m


def paint(ob, role):
    # IsoRim's tint for preview: stuff_plain* as skin or hair, the rest by the material's own colour.
    base = SKIN if role == "skin" else HAIR
    for slot in ob.material_slots:
        n = slot.material.name.split(".")[0] if slot.material else ""
        if n.startswith("stuff_plain"):
            k = 1.12 if n.endswith("light") else (0.8 if n.endswith("dark") else 1.0)
            if n.endswith("light") and role == "skin":
                rgb = (0.98, 0.72, 0.72)   # the pig ear's pink inner face
            else:
                rgb = tuple(min(1.0, c * k) for c in base)
            slot.material = preview_material("pv_%s_%s" % (role, n), rgb)
        else:
            slot.material = preview_material("pv_" + n, mw.COLOURS.get(n, (0.5, 0.5, 0.5)))


def pawn(parts, out_png, views, build="male", hair="person_hair_short", extras=(), hair_rgb=None, head_role="skin"):
    mw.clear()
    for ob in list(bpy.data.objects):
        if ob.name != "sun":
            bpy.data.objects.remove(ob)
    figure = []
    global HAIR
    HAIR = hair_rgb or (0.40, 0.26, 0.16)
    base = [("person_%s_lower" % build, "skin"), ("person_%s_upper" % build, "skin"), ("person_head", head_role),
            ("person_face", "skin")] + ([(hair, "hair")] if hair else []) + [(e, "hair") for e in extras]
    for f, role in base:
        ob = import_obj(os.path.join(MODELS_DIR, f + ".obj"), f)
        paint(ob, role)
        figure.append(ob)
    for path, role in parts:
        ob = import_obj(path, os.path.basename(path)[:-4])
        paint(ob, role)
        figure.append(ob)
    lo = Vector([min((o.matrix_world @ v.co)[k] for o in figure for v in o.data.vertices) for k in range(3)])
    hi = Vector([max((o.matrix_world @ v.co)[k] for o in figure for v in o.data.vertices) for k in range(3)])
    centre = (lo + hi) / 2
    scene = bpy.context.scene
    scene.render.resolution_x = 700
    scene.render.resolution_y = 900
    images = []
    for i, d in enumerate(views):
        cam_data = bpy.data.cameras.new("cam")
        cam_data.type = "ORTHO"
        cam_data.ortho_scale = (hi.z - lo.z) * 1.25
        cam = bpy.data.objects.new("cam", cam_data)
        scene.collection.objects.link(cam)
        d = Vector(d).normalized()
        cam.location = centre + d * 4.0
        cam.rotation_euler = (-d).to_track_quat("-Z", "Y").to_euler()
        scene.camera = cam
        p = out_png[:-4] + "_%d.png" % i
        scene.render.filepath = p
        bpy.ops.render.render(write_still=True)
        bpy.data.objects.remove(cam)
        images.append(p)
    return images


if __name__ == "__main__":
    os.makedirs(mw.OUT, exist_ok=True)
    built = {}
    for build in PARTS:
        built[build.__name__] = mw.build_one(build).name
    # Lighting / world as make_weapons' renders.
    role = {"pig_ears": "skin", "pig_nose": "skin", "mini_horns": "skin", "furry_tail": "hair"}
    path = lambda n: os.path.join(mw.OUT, built[n] + ".obj")
    os.makedirs(os.path.join(mw.OUT, "pawns"), exist_ok=True)
    front, back, side = (0.55, 1.0, 0.35), (-0.6, -1.0, 0.35), (1.0, 0.0, 0.1)
    for build in FUR + [head_fur]:
        ob = mw.build_one(build)
        built[ob.name] = ob.name
    fur = lambda b: [(path("gene_furskin_%s_lower" % b), "hair"), (path("gene_furskin_%s_upper" % b), "hair")]
    ytta = (0.78, 0.52, 0.24)   # the Yttakin sprite's orange-brown fur
    pawn(fur("hulk") + [(path("gene_furskin_head"), "hair"), (path("furry_tail"), "hair")],
         os.path.join(mw.OUT, "pawns", "yttakin_furface.png"), [front, side, back, (0.0, 1.0, 0.15)],
         build="hulk", hair=None, extras=("person_beard_full",), hair_rgb=ytta, head_role="hair")
    pawn(fur("hulk") + [(path("gene_furskin_head"), "hair"), (path("furry_tail"), "hair")],
         os.path.join(mw.OUT, "pawns", "yttakin_headfur.png"), [front, side, back, (0.0, 1.0, 0.15)],
         build="hulk", hair=None, extras=("person_beard_full",), hair_rgb=ytta)
    pawn(fur("hulk") + [(path("furry_tail"), "hair")], os.path.join(mw.OUT, "pawns", "yttakin_fur.png"),
         [front, side, back], build="hulk", hair=None, extras=("person_beard_full",), hair_rgb=ytta)
    for b in BUILDS:
        pawn(fur(b), os.path.join(mw.OUT, "pawns", "fur_" + b + ".png"), [front], build=b, hair=None, hair_rgb=ytta)
    only = os.environ.get("PAWNS", "pigskin,impid,yttakin,plain").split(",")
    for name, parts in (("pigskin", ["pig_ears", "pig_nose"]), ("impid", ["mini_horns"]),
                        ("yttakin", ["furry_tail"]), ("plain", [])):
        if name not in only:
            continue
        imgs = pawn([(path(p), role[p]) for p in parts], os.path.join(mw.OUT, "pawns", name + ".png"),
                    [front, side, back])
        print("pawn", name, imgs)
