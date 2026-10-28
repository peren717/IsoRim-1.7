# Builds IsoRim-style weapon models in Blender (bpy) and exports them as .obj.
# Conventions (as Models/*.obj of IsoRim): one unit = one map cell, the hand / grip at the origin, the muzzle or
# blade tip along +X, up along +Y in the .obj (Blender +Z), thickness along Z. Materials are names from IsoRim's
# Models/isorim.mtl; the exported file references isorim.mtl and ships no .mtl of its own. Faces are written as
# triangles: IsoRim fans each polygon from its first corner, which is only right for convex ones.
import math
import os
import sys

import bpy
import bmesh
from mathutils import Vector

OUT = sys.argv[sys.argv.index("--") + 1] if "--" in sys.argv else "out"
os.makedirs(OUT, exist_ok=True)

# Colours of the isorim.mtl materials used, for the preview renders only.
COLOURS = {
    "gun_metal": (0.36, 0.37, 0.40), "gun_dark": (0.20, 0.20, 0.22), "gun_light": (0.66, 0.68, 0.70),
    "gun_white": (0.84, 0.86, 0.88), "gun_olive": (0.36, 0.40, 0.28), "gun_wood": (0.50, 0.34, 0.21),
    "grip": (0.30, 0.22, 0.16), "charge": (0.45, 0.90, 1.00), "glow_green": (0.46, 0.94, 0.52),
    "ember": (0.96, 0.42, 0.18), "chemfuel": (0.74, 0.30, 0.16), "rust": (0.50, 0.32, 0.22),
    "rust_dark": (0.30, 0.21, 0.16), "plasteel": (0.70, 0.80, 0.84), "brass": (0.80, 0.64, 0.30),
    "paint_red": (0.56, 0.24, 0.20), "spike": (0.80, 0.80, 0.80), "warning": (0.90, 0.75, 0.20),
    "glow_amber": (1.00, 0.72, 0.30), "food_green": (0.44, 0.62, 0.30), "metal_dark": (0.32, 0.33, 0.36),
    "agave": (0.29, 0.54, 0.52), "wood_end": (0.78, 0.62, 0.40), "glow_red": (0.98, 0.36, 0.30), "string": (0.86, 0.82, 0.72),
}
GLOWING = {"charge", "glow_green", "ember", "glow_amber", "glow_red"}


def material(name):
    m = bpy.data.materials.get(name)
    if m is None:
        m = bpy.data.materials.new(name)
        c = tuple(v ** 2.2 for v in COLOURS[name])   # isorim.mtl colours are sRGB; Blender wants linear
        m.diffuse_color = (c[0], c[1], c[2], 1.0)
        m.use_nodes = True
        bsdf = m.node_tree.nodes.get("Principled BSDF")
        bsdf.inputs["Base Color"].default_value = (c[0], c[1], c[2], 1.0)
        bsdf.inputs["Roughness"].default_value = 0.55
        if name in GLOWING:
            bsdf.inputs["Emission Color"].default_value = (c[0], c[1], c[2], 1.0)
            bsdf.inputs["Emission Strength"].default_value = 0.6
    return m


class Model:
    def __init__(self, name):
        self.name = name
        self.bm = bmesh.new()
        self.mats = []

    def _mat(self, name):
        if name not in self.mats:
            self.mats.append(name)
        return self.mats.index(name)

    def _faces(self, verts, faces, mat):
        vs = [self.bm.verts.new(v) for v in verts]
        i = self._mat(mat)
        for f in faces:
            face = self.bm.faces.new([vs[k] for k in f])
            face.material_index = i

    def box(self, mat, lo, hi):
        (x0, y0, z0), (x1, y1, z1) = lo, hi
        v = [(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
             (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)]
        f = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
        self._faces(v, f, mat)

    def prism(self, mat, profile, y0, y1):
        # A side-view outline (x, z), convex or not, given counter-clockwise, made solid between y0 and y1.
        n = len(profile)
        v = [(x, y0, z) for x, z in profile] + [(x, y1, z) for x, z in profile]
        f = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
        f += [(k, (k + 1) % n, n + (k + 1) % n, n + k) for k in range(n)]
        self._faces(v, f, mat)

    def tube(self, mat, axis, start, length, r0, r1=None, sides=8, cap=True, rot=0.0):
        # A cylinder or cone along axis 'x' / 'y' / 'z' from start, radius r0 at the start and r1 at the end.
        r1 = r0 if r1 is None else r1
        a = "xyz".index(axis)
        b, c = [k for k in range(3) if k != a]
        v = []
        for end, r in ((0.0, r0), (length, r1)):
            for k in range(sides):
                t = 2 * math.pi * k / sides + math.pi / sides + rot
                p = list(start)
                p[a] += end
                p[b] += r * math.cos(t)
                p[c] += r * math.sin(t)
                v.append(tuple(p))
        f = [(k, (k + 1) % sides, sides + (k + 1) % sides, sides + k) for k in range(sides)]
        if cap:
            f += [tuple(range(sides - 1, -1, -1)), tuple(range(sides, 2 * sides))]
        if axis == "y":
            f = [tuple(reversed(x)) for x in f]
        self._faces(v, f, mat)

    def ball(self, mat, centre, r, rings=4, sides=8):
        v = [(centre[0], centre[1], centre[2] - r)]
        for i in range(1, rings):
            phi = -math.pi / 2 + math.pi * i / rings
            for k in range(sides):
                t = 2 * math.pi * k / sides
                v.append((centre[0] + r * math.cos(phi) * math.cos(t), centre[1] + r * math.cos(phi) * math.sin(t),
                          centre[2] + r * math.sin(phi)))
        v.append((centre[0], centre[1], centre[2] + r))
        top = len(v) - 1
        f = [(0, 1 + (k + 1) % sides, 1 + k) for k in range(sides)]
        for i in range(rings - 2):
            o = 1 + i * sides
            f += [(o + k, o + (k + 1) % sides, o + sides + (k + 1) % sides, o + sides + k) for k in range(sides)]
        o = 1 + (rings - 2) * sides
        f += [(o + k, o + (k + 1) % sides, top) for k in range(sides)]
        self._faces(v, f, mat)

    def strip(self, mat, points, w0, w1, y0, y1):
        # A band along a side-view polyline (x, z), width w0 at the start to w1 at the end, made solid between y0 and y1.
        n = len(points)
        left, right = [], []
        for i, (x, z) in enumerate(points):
            ax, az = points[max(i - 1, 0)]
            bx, bz = points[min(i + 1, n - 1)]
            dx, dz = bx - ax, bz - az
            ln = math.hypot(dx, dz) or 1.0
            nx, nz = -dz / ln, dx / ln
            w = (w0 + (w1 - w0) * i / (n - 1)) / 2
            left.append((x + nx * w, z + nz * w))
            right.append((x - nx * w, z - nz * w))
        for i in range(n - 1):
            self.prism(mat, [right[i], right[i + 1], left[i + 1], left[i]], y0, y1)

    def ring(self, mat, centre, r_in, r_out, y0, y1, sides=12):
        # A flat ring standing in the side view (x, z) around centre, made solid between y0 and y1.
        cx, cz = centre
        for k in range(sides):
            a0 = 2 * math.pi * k / sides
            a1 = 2 * math.pi * (k + 1) / sides
            self.prism(mat, [(cx + r_in * math.cos(a0), cz + r_in * math.sin(a0)),
                             (cx + r_out * math.cos(a0), cz + r_out * math.sin(a0)),
                             (cx + r_out * math.cos(a1), cz + r_out * math.sin(a1)),
                             (cx + r_in * math.cos(a1), cz + r_in * math.sin(a1))], y0, y1)

    def slab(self, mat, outline, z0, z1):
        # A top-view outline (x, y) made solid between heights z0 and z1.
        n = len(outline)
        v = [(x, y, z0) for x, y in outline] + [(x, y, z1) for x, y in outline]
        f = [tuple(range(n - 1, -1, -1)), tuple(range(n, 2 * n))]
        f += [(k, (k + 1) % n, n + (k + 1) % n, n + k) for k in range(n)]
        self._faces(v, f, mat)

    def finish(self):
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        # Planar UVs, one unit = one cell, each face projected along its main axis (as IsoRim's own models).
        uv = self.bm.loops.layers.uv.new()
        for face in self.bm.faces:
            n = face.normal
            ax = max(range(3), key=lambda k: abs(n[k]))
            u, w = [k for k in range(3) if k != ax]
            for loop in face.loops:
                co = loop.vert.co
                loop[uv].uv = (co[u], co[w])
        me = bpy.data.meshes.new(self.name)
        self.bm.to_mesh(me)
        self.bm.free()
        for m in self.mats:
            me.materials.append(material(m))
        ob = bpy.data.objects.new(self.name, me)
        bpy.context.scene.collection.objects.link(ob)
        return ob


def swept(x0, x1, z_lo, z_hi, bend, steps):
    # A blade's side-view outline from x0 to x1: lower and upper edge heights as functions of t (0..1), bent upward
    # by bend * t^2 (a curved blade).
    lower, upper = [], []
    for i in range(steps + 1):
        t = i / steps
        x = x0 + (x1 - x0) * t
        dz = bend * t * t
        lower.append((x, z_lo(t) + dz))
        upper.append((x, z_hi(t) + dz))
    return lower + list(reversed(upper))


# ---------------------------------------------------------------------------------------------------------------------
# Royalty: monosword, plasmasword, zeushammer (their persona/bladelink versions use the same models), Eltex staff.

def monosword():
    m = Model("monosword")
    m.box("gun_dark", (-0.195, -0.018, -0.018), (-0.175, 0.018, 0.018))      # pommel
    m.box("grip", (-0.175, -0.014, -0.016), (0.0, 0.014, 0.016))            # wrapped grip
    m.tube("gun_dark", "x", (0.0, 0.0, 0.0), 0.012, 0.042, sides=8)        # round guard (tsuba)
    m.box("gun_light", (0.012, -0.010, -0.017), (0.045, 0.010, 0.019))      # collar (habaki)
    # Slightly curved single-edged blade, edge down, back up, the tip swept up to the back.
    m.prism("plasteel", swept(0.045, 0.74, lambda t: -0.016 + 0.010 * t ** 6, lambda t: 0.018 - 0.002 * t,
                                0.035, 10) + [], -0.005, 0.005)
    m.prism("plasteel", [(0.74, -0.006 + 0.035), (0.785, 0.010 + 0.037), (0.74, 0.016 + 0.035)], -0.004, 0.004)
    return m


def plasmasword():
    m = Model("plasmasword")
    m.tube("gun_dark", "x", (-0.20, 0.0, 0.0), 0.025, 0.020, 0.024)         # pommel
    m.tube("gun_white", "x", (-0.175, 0.0, 0.0), 0.175, 0.020)             # hilt
    for x in (-0.15, -0.11, -0.07, -0.03):
        m.tube("gun_dark", "x", (x, 0.0, 0.0), 0.012, 0.022)               # grip rings
    m.box("gun_dark", (0.0, -0.026, -0.040), (0.030, 0.026, 0.040))         # guard / emitter block
    m.tube("charge", "x", (0.006, 0.0, 0.044), 0.018, 0.006, sides=6)      # power lamp
    m.tube("gun_white", "x", (0.030, 0.0, 0.0), 0.030, 0.026, 0.020)        # emitter
    # The plasma blade: a glowing, slightly flaring then tapering column.
    m.prism("charge", [(0.060, -0.020), (0.200, -0.026), (0.600, -0.020), (0.700, 0.0),
                       (0.600, 0.020), (0.200, 0.026), (0.060, 0.020)], -0.012, 0.012)
    return m


def zeushammer():
    m = Model("zeushammer")
    m.tube("gun_dark", "x", (-0.27, 0.0, 0.0), 0.03, 0.020, 0.024)          # butt cap
    m.tube("grip", "x", (-0.24, 0.0, 0.0), 0.25, 0.018)                    # grip
    m.tube("gun_dark", "x", (0.01, 0.0, 0.0), 0.39, 0.016)                 # shaft
    for x in (0.10, 0.22):
        m.tube("charge", "x", (x, 0.0, 0.0), 0.012, 0.019)                 # charge rings on the shaft
    # Head: a drum standing across the shaft (along up), white plates, glowing coils, dark striking faces.
    m.tube("gun_white", "z", (0.46, 0.0, -0.10), 0.20, 0.060, sides=10)
    for z in (-0.065, 0.045):
        m.tube("charge", "z", (0.46, 0.0, z), 0.020, 0.064, sides=10, cap=False)
    m.tube("gun_dark", "z", (0.46, 0.0, -0.125), 0.025, 0.050, 0.060, sides=10)
    m.tube("gun_dark", "z", (0.46, 0.0, 0.10), 0.025, 0.060, 0.050, sides=10)
    m.box("gun_dark", (0.39, -0.022, -0.035), (0.53, 0.022, 0.035))         # collar where the shaft meets the head
    return m


def eltex_staff():
    m = Model("eltex_staff")
    m.tube("gun_dark", "x", (-0.50, 0.0, 0.0), 0.04, 0.010, 0.017)          # ferrule
    m.tube("gun_white", "x", (-0.46, 0.0, 0.0), 0.88, 0.014)               # shaft
    m.tube("grip", "x", (-0.10, 0.0, 0.0), 0.20, 0.017)                    # hand wrap
    for x in (-0.30, 0.20):
        m.tube("charge", "x", (x, 0.0, 0.0), 0.014, 0.018)                 # eltex inlays
    m.tube("gun_white", "x", (0.42, 0.0, 0.0), 0.04, 0.016, 0.030)          # head socket
    # Two prongs holding the eltex crystal.
    m.prism("gun_white", [(0.45, 0.010), (0.56, 0.055), (0.60, 0.040), (0.50, 0.004)], -0.008, 0.008)
    m.prism("gun_white", [(0.45, -0.010), (0.50, -0.004), (0.60, -0.040), (0.56, -0.055)], -0.008, 0.008)
    m.ball("charge", (0.535, 0.0, 0.0), 0.038, rings=4, sides=8)
    return m


# ---------------------------------------------------------------------------------------------------------------------
# Biotech: toxbomb launcher, tox grenade.

def toxbomb_launcher():
    m = Model("toxbomb_launcher")
    m.tube("gun_olive", "x", (-0.13, 0.0, 0.03), 0.52, 0.048, sides=10)    # launch tube
    m.tube("gun_dark", "x", (0.35, 0.0, 0.03), 0.06, 0.056, sides=10, cap=False)
    m.tube("gun_dark", "x", (-0.16, 0.0, 0.03), 0.04, 0.054, 0.050, sides=10)
    m.tube("glow_green", "x", (0.10, 0.0, 0.03), 0.03, 0.051, sides=10, cap=False)   # tox warning band
    m.tube("glow_green", "x", (0.16, 0.0, 0.03), 0.015, 0.051, sides=10, cap=False)
    m.box("gun_dark", (-0.03, -0.018, -0.12), (0.03, 0.018, -0.015))        # pistol grip
    m.box("gun_dark", (0.03, -0.006, -0.06), (0.07, 0.006, -0.015))         # trigger guard
    m.box("gun_dark", (0.20, -0.016, -0.10), (0.24, 0.016, -0.015))         # fore grip
    m.box("gun_dark", (0.00, -0.012, 0.075), (0.10, 0.012, 0.11))           # sight
    m.box("gun_olive", (-0.10, -0.040, -0.04), (0.00, 0.040, 0.02))         # shell drum housing
    return m


def grenade_tox():
    m = Model("grenade_tox")
    m.tube("gun_olive", "z", (0.0, 0.0, -0.07), 0.13, 0.045, sides=10)     # canister
    for z in (-0.045, 0.02):
        m.tube("glow_green", "z", (0.0, 0.0, z), 0.018, 0.048, sides=10, cap=False)   # tox bands
    m.tube("gun_dark", "z", (0.0, 0.0, 0.06), 0.025, 0.022, sides=8)       # fuse head
    m.box("gun_light", (0.010, -0.008, 0.02), (0.060, 0.008, 0.08))         # spoon (lever)
    m.box("gun_light", (-0.050, -0.003, 0.068), (-0.020, 0.003, 0.098))     # pin ring
    return m


# ---------------------------------------------------------------------------------------------------------------------
# Anomaly: nerve spiker, incinerator, hellcat rifle (bioferrite: dark red-brown).

def nerve_spiker():
    m = Model("nerve_spiker")
    m.prism("gun_wood", [(-0.24, -0.06), (-0.04, -0.02), (-0.04, 0.03), (-0.24, 0.02)], -0.020, 0.020)  # stock
    m.box("rust_dark", (-0.04, -0.028, -0.025), (0.24, 0.028, 0.040))       # bioferrite body
    m.box("rust", (0.00, -0.032, 0.040), (0.20, 0.032, 0.055))              # spike magazine
    for x in (0.03, 0.08, 0.13):
        m.box("spike", (x, -0.004, 0.055), (x + 0.03, 0.004, 0.075))        # spikes waiting in it
    m.box("rust_dark", (-0.02, -0.016, -0.11), (0.03, 0.016, -0.025))       # grip
    m.tube("rust", "x", (0.24, 0.0, 0.008), 0.18, 0.018, sides=8)          # barrel
    m.tube("rust_dark", "x", (0.30, 0.0, 0.008), 0.03, 0.026, sides=8)     # barrel band
    m.tube("spike", "x", (0.42, 0.0, 0.008), 0.06, 0.007, 0.0, sides=6)    # spike in the muzzle
    m.prism("rust_dark", [(0.16, 0.04), (0.20, 0.04), (0.17, 0.09), (0.15, 0.09)], -0.006, 0.006)  # rear fin
    return m


def incinerator():
    m = Model("incinerator")
    m.box("gun_dark", (-0.22, -0.020, -0.03), (-0.05, 0.020, 0.03))        # stock
    m.box("rust_dark", (-0.05, -0.030, -0.03), (0.30, 0.030, 0.055))       # body
    m.box("rust_dark", (-0.02, -0.016, -0.12), (0.03, 0.016, -0.03))       # grip
    m.tube("chemfuel", "x", (0.00, 0.0, -0.075), 0.26, 0.042, sides=10)    # fuel tank under the body
    m.tube("gun_dark", "x", (0.26, 0.0, -0.075), 0.02, 0.044, 0.030, sides=10)
    m.box("gun_dark", (0.20, -0.012, -0.05), (0.24, 0.012, -0.03))         # feed
    m.tube("gun_metal", "x", (0.30, 0.0, 0.012), 0.20, 0.020, sides=8)    # nozzle
    m.tube("rust", "x", (0.50, 0.0, 0.012), 0.05, 0.026, 0.032, sides=8)  # flared tip
    m.tube("ember", "x", (0.50, 0.0, -0.020), 0.03, 0.007, sides=6)        # pilot light
    m.box("gun_dark", (0.30, -0.006, -0.020), (0.50, 0.006, -0.010))       # pilot line
    return m


def hellcat_rifle():
    m = Model("hellcat_rifle")
    m.prism("gun_dark", [(-0.26, -0.07), (-0.06, -0.02), (-0.06, 0.035), (-0.26, 0.025)], -0.022, 0.022)  # stock
    m.box("rust_dark", (-0.06, -0.030, -0.025), (0.30, 0.030, 0.055))      # bioferrite receiver
    m.box("gun_dark", (-0.03, -0.016, -0.12), (0.02, 0.016, -0.025))       # grip
    m.box("paint_red", (0.06, -0.022, -0.11), (0.12, 0.022, -0.025))       # fuel cell (magazine)
    m.box("gun_dark", (0.04, -0.012, 0.055), (0.18, 0.012, 0.08))          # sight rail
    m.box("gun_dark", (0.30, -0.026, -0.015), (0.58, 0.026, 0.045))        # barrel shroud
    for x in (0.34, 0.40, 0.46, 0.52):
        m.box("ember", (x, -0.027, 0.0), (x + 0.03, 0.027, 0.03))          # glowing vents
    m.tube("gun_metal", "x", (0.58, 0.0, 0.015), 0.14, 0.013, sides=8)    # barrel
    m.tube("rust", "x", (0.70, 0.0, 0.015), 0.04, 0.020, sides=8)         # muzzle
    return m


# ---------------------------------------------------------------------------------------------------------------------
# Batch 1, second pass: shaped after the game's own sprites (rimworldwiki.com). The sprite's side view is the model's
# side view (x along, z up); a 128 px sprite is about 0.0068 cells per pixel, as IsoRim's assault rifle to its sprite.

def monosword():
    # Straight white blade with a blue line, a short angled point, small dark guard, grey grip, dark pommel.
    m = Model("monosword")
    m.tube("gun_dark", "x", (-0.235, 0.0, 0.0), 0.030, 0.020, 0.024, sides=8)          # pommel
    m.tube("gun_metal", "x", (-0.205, 0.0, 0.0), 0.205, 0.021, sides=8)                # grip
    m.box("gun_dark", (0.0, -0.020, -0.046), (0.024, 0.020, 0.046))                    # guard
    m.prism("gun_white", [(0.024, -0.030), (0.520, -0.030), (0.585, -0.004), (0.560, 0.030),
                          (0.024, 0.030)], -0.006, 0.006)                              # blade
    m.box("charge", (0.040, -0.0068, -0.005), (0.400, 0.0068, 0.004))                  # the blue line, both faces
    return m


def plasmasword():
    # Grey hilt with a round collar, a curved, widening orange plasma blade with a yellow core line.
    m = Model("plasmasword")
    m.tube("gun_metal", "x", (-0.250, 0.0, 0.0), 0.070, 0.020, 0.026, sides=8)         # pommel
    m.tube("gun_dark", "x", (-0.180, 0.0, 0.0), 0.130, 0.022, sides=8)                 # grip
    m.ball("gun_metal", (-0.020, 0.0, 0.0), 0.042, rings=4, sides=8)                   # collar / emitter
    m.prism("ember", [(0.010, -0.030), (0.120, -0.046), (0.300, -0.044), (0.560, -0.030), (0.600, -0.024),
                      (0.555, 0.040), (0.300, 0.030), (0.120, 0.034), (0.010, 0.030)], -0.008, 0.008)
    m.prism("glow_amber", [(0.040, -0.006), (0.300, -0.010), (0.540, -0.006), (0.540, 0.004), (0.300, 0.000),
                           (0.040, 0.004)], -0.0088, 0.0088)                          # bright core line
    return m


def zeushammer():
    # Long grey shaft, dark grip and pommel, a heavy dark head of two chamfered lobes with teal panels.
    m = Model("zeushammer")
    m.box("gun_dark", (-0.300, -0.030, -0.034), (-0.240, 0.030, 0.034))                # pommel block
    m.box("charge", (-0.292, -0.031, -0.024), (-0.250, 0.031, 0.024))                  # pommel panel
    m.tube("gun_dark", "x", (-0.240, 0.0, 0.0), 0.100, 0.022, 0.015, sides=8)          # grip
    m.tube("gun_light", "x", (-0.140, 0.0, 0.0), 0.560, 0.013, sides=8)               # shaft
    m.ball("gun_dark", (0.475, 0.0, 0.0), 0.070, rings=4, sides=8)                     # head core
    lobe = [(0.405, 0.050), (0.545, 0.050), (0.575, 0.110), (0.575, 0.180), (0.545, 0.225),
            (0.405, 0.225), (0.375, 0.180), (0.375, 0.110)]
    for sign in (1, -1):
        pts = [(x, sign * z) for x, z in lobe]
        if sign < 0:
            pts.reverse()
        m.prism("gun_dark", pts, -0.060, 0.060)
        m.box("metal_dark", (0.372, -0.062, sign * 0.120 - 0.012), (0.578, 0.062, sign * 0.120 + 0.012))   # band
        lo, hi = sorted((sign * 0.160, sign * 0.210))
        m.box("charge", (0.395, -0.062, lo), (0.555, 0.062, hi))                       # teal panel
    return m


def toxbomb_launcher():
    # A rifle-shaped grenade launcher: dark stock, grey receiver, pistol grip, short dark barrel with a front sight,
    # and a pale green tox drum under the front.
    m = Model("toxbomb_launcher")
    m.prism("gun_dark", [(-0.225, -0.075), (-0.060, -0.045), (-0.060, 0.040), (-0.225, 0.048)], -0.022, 0.022)
    m.box("gun_metal", (-0.060, -0.026, -0.054), (0.265, 0.026, 0.041))                # receiver
    m.prism("gun_metal", [(0.000, 0.041), (0.110, 0.041), (0.090, 0.085), (0.030, 0.085)], -0.012, 0.012)
    m.box("gun_dark", (0.060, -0.027, -0.010), (0.250, 0.027, 0.000))                  # side groove
    m.prism("gun_dark", [(-0.030, -0.054), (0.030, -0.054), (0.005, -0.195), (-0.055, -0.190)], -0.016, 0.016)
    m.box("gun_dark", (0.030, -0.006, -0.090), (0.075, 0.006, -0.054))                 # trigger guard
    m.tube("gun_dark", "x", (0.265, 0.0, -0.005), 0.140, 0.032, sides=8)              # barrel
    m.tube("gun_dark", "x", (0.380, 0.0, -0.005), 0.030, 0.040, sides=8)              # muzzle
    m.prism("gun_dark", [(0.365, 0.030), (0.405, 0.030), (0.405, 0.095), (0.390, 0.095)], -0.008, 0.008)
    m.tube("food_green", "x", (0.100, 0.0, -0.125), 0.225, 0.070, sides=10)           # tox drum
    m.tube("gun_dark", "x", (0.090, 0.0, -0.125), 0.012, 0.064, sides=10)
    m.tube("gun_dark", "x", (0.323, 0.0, -0.125), 0.012, 0.064, sides=10)
    return m


def incinerator():
    # Dark stock, light grey body with an orange label, grey pistol grip, a dark muzzle block with a front sight,
    # and a maroon fuel canister hanging forward-down under the body.
    m = Model("incinerator")
    m.prism("gun_dark", [(-0.215, -0.020), (-0.070, -0.030), (-0.070, 0.060), (-0.215, 0.075)], -0.022, 0.022)
    m.box("gun_light", (-0.070, -0.030, -0.060), (0.310, 0.030, 0.065))                # body
    m.box("chemfuel", (0.020, -0.031, -0.010), (0.110, 0.031, 0.040))                  # label
    m.box("gun_dark", (0.310, -0.026, -0.020), (0.430, 0.026, 0.065))                  # muzzle block
    m.tube("gun_dark", "x", (0.430, 0.0, 0.022), 0.025, 0.020, sides=8)
    m.prism("gun_dark", [(0.350, 0.065), (0.410, 0.065), (0.410, 0.120), (0.395, 0.120)], -0.008, 0.008)
    m.prism("gun_metal", [(-0.050, -0.060), (0.030, -0.060), (0.005, -0.175), (-0.070, -0.165)], -0.017, 0.017)
    m.box("gun_dark", (0.140, -0.014, -0.085), (0.200, 0.014, -0.060))                 # canister neck
    m.prism("paint_red", [(0.100, -0.070), (0.270, -0.095), (0.180, -0.300), (0.030, -0.245)], -0.034, 0.034)
    m.prism("gun_dark", [(0.075, -0.140), (0.235, -0.165), (0.228, -0.180), (0.068, -0.155)], -0.035, 0.035)  # strap
    return m


def zeushammer():
    # Remade after the sprite: round teal pommel with a dark core, dark tapering grip, long light grey shaft, and a tall
    # dark head of two chamfered (octagonal) lobes joined by a teal neck behind a round hub, each lobe with a grey band
    # and a muted teal panel near its end.
    m = Model("zeushammer")
    m.tube("agave", "x", (-0.310, 0.0, 0.0), 0.060, 0.052, sides=8)                   # pommel
    m.tube("gun_dark", "x", (-0.316, 0.0, 0.0), 0.072, 0.030, sides=8)                # its dark core, both ends
    m.tube("gun_dark", "x", (-0.250, 0.0, 0.0), 0.085, 0.036, 0.026, sides=8)          # tapering grip
    m.tube("gun_light", "x", (-0.165, 0.0, 0.0), 0.590, 0.026, sides=8)               # shaft
    m.box("agave", (0.400, -0.048, -0.070), (0.550, 0.048, 0.070))                     # neck between the lobes
    m.tube("gun_dark", "y", (0.470, -0.075, 0.0), 0.150, 0.066, sides=10)             # round hub across the head
    lobe = [(0.395, 0.055), (0.555, 0.055), (0.585, 0.105), (0.585, 0.215), (0.555, 0.265),
            (0.395, 0.265), (0.365, 0.215), (0.365, 0.105)]
    panel = [(0.390, 0.180), (0.560, 0.180), (0.545, 0.240), (0.405, 0.240)]
    for sign in (1, -1):
        def flip(points):
            pts = [(x, sign * z) for x, z in points]
            return pts if sign > 0 else list(reversed(pts))
        m.prism("gun_dark", flip(lobe), -0.065, 0.065)                                 # lobe
        lo, hi = sorted((sign * 0.135, sign * 0.150))
        m.box("gun_metal", (0.364, -0.067, lo), (0.586, 0.067, hi))                    # band
        m.prism("agave", flip(panel), -0.068, 0.068)                                   # teal panel, both faces
        m.box("agave", (0.405, -0.045, min(sign * 0.268, sign * 0.262)), (0.545, 0.045, max(sign * 0.268, sign * 0.262)))  # end face panel
    return m

# ---------------------------------------------------------------------------------------------------------------------
# Batch 2, shaped after the game's sprites.

def px(k, ox, oz):
    # Sprite pixels (column, row) to model x, z: k cells per pixel, (ox, oz) the pixel at the model's origin.
    return lambda pts: [((c - ox) * k, (oz - r) * k) for c, r in pts]


def eltex_staff():
    # Grey staff, a dark round head ring with two small curled horns, holding a pale cyan eltex crystal pointing forward.
    m = Model("eltex_staff")
    m.tube("gun_dark", "x", (-0.330, 0.0, 0.0), 0.030, 0.018, 0.023, sides=8)          # butt cap
    m.tube("gun_metal", "x", (-0.300, 0.0, 0.0), 0.640, 0.022, sides=8)               # staff
    m.tube("gun_dark", "x", (0.300, 0.0, 0.0), 0.050, 0.028, 0.034, sides=8)           # collar
    m.ring("metal_dark", (0.455, 0.0), 0.080, 0.122, -0.032, 0.032)                    # head ring
    m.ring("gun_metal", (0.455, 0.0), 0.110, 0.126, -0.034, 0.034)                     # its lighter rim
    for sign in (1, -1):
        horn = [(0.385, sign * 0.090), (0.425, sign * 0.115), (0.405, sign * 0.150), (0.365, sign * 0.155),
                (0.385, sign * 0.135)]
        m.prism("metal_dark", horn if sign > 0 else list(reversed(horn)), -0.020, 0.020)
    m.ball("charge", (0.440, 0.0, 0.0), 0.062, rings=4, sides=8)                       # eltex crystal
    m.tube("charge", "x", (0.470, 0.0, 0.0), 0.090, 0.045, 0.0, sides=8)               # its point
    return m


def flamebow():
    # A deep D-shaped golden bow with thick limbs, string at the back, both tips curling back past the string (as
    # IsoRim's bows: upright, the string at x = -0.03, the grip forward).
    m = Model("flamebow")
    limb = [(0.200, 0.0), (0.195, 0.100), (0.175, 0.190), (0.140, 0.270), (0.090, 0.335), (0.035, 0.380),
            (-0.015, 0.410), (-0.050, 0.430), (-0.062, 0.455), (-0.042, 0.476), (-0.002, 0.482), (0.030, 0.470)]
    for sign in (1, -1):
        pts = [(x, sign * z) for x, z in limb]
        m.strip("wood_end", pts, 0.076, 0.034, -0.018, 0.018)
        inner = [(x - 0.022, sign * z * 0.96) for x, z in limb[:7]]
        m.strip("gun_wood", inner, 0.022, 0.010, -0.0185, 0.0185)                       # darker inner line
    m.box("gun_wood", (0.168, -0.022, -0.060), (0.232, 0.022, 0.060))                  # grip
    m.box("string", (-0.034, -0.004, -0.415), (-0.026, 0.004, 0.415))                  # string
    return m


def grenade_tox():
    # One tox grenade (the sprite shows three on a strap): a pale green canister with a grey cap, fuse and lever, on the
    # frag grenade's footprint.
    m = Model("grenade_tox")
    m.tube("food_green", "z", (0.005, 0.0, -0.070), 0.120, 0.046, sides=10)            # canister
    m.tube("gun_olive", "z", (0.005, 0.0, -0.070), 0.030, 0.048, sides=10, cap=False)  # darker foot
    m.tube("gun_metal", "z", (0.005, 0.0, 0.050), 0.022, 0.048, 0.040, sides=10)       # cap
    m.tube("gun_dark", "z", (0.005, 0.0, 0.072), 0.026, 0.018, sides=8)                # fuse
    m.box("gun_light", (0.015, -0.006, 0.072), (0.060, 0.006, 0.086))                  # lever
    m.box("gun_light", (0.052, -0.006, 0.010), (0.060, 0.006, 0.086))
    return m


def nerve_spiker():
    # A crossbow; the sprite is its top view. Seen from above: a slim dark bioferrite body with a forked tail and a
    # pointed nose, a jagged pair of limbs spreading sideways with the string across them, a red glowing line along the
    # top. Seen from the side: a stock with a grip and trigger under it, the limbs a thin flat band near the front.
    m = Model("nerve_spiker")
    k = 0.0115

    def P(pts):
        return [((c - 24) * k, (21 - r) * k) for c, r in pts]
    body = [(63, 21), (55, 17), (46, 16), (38, 16), (26, 16), (16, 17), (3, 6), (10, 19), (7, 21), (10, 23), (3, 36),
            (16, 25), (26, 26), (38, 26), (46, 26), (55, 25)]
    m.slab("metal_dark", P(list(reversed(body))), -0.034, 0.030)                      # body (stock and rail)
    for sign in (1, -1):
        limb = [(29, 16), (27, 1), (33, 0), (36, 5), (41, 0), (44, 4), (41, 9), (46, 10), (42, 16)]
        pts = [(c, 21 - sign * (21 - r)) for c, r in limb]
        m.slab("gun_dark", P(pts if sign < 0 else list(reversed(pts))), 0.000, 0.026)  # limb
        spike = [(50, 17), (53, 11), (57, 17)]
        pts = [(c, 21 - sign * (21 - r)) for c, r in spike]
        m.slab("gun_dark", P(pts if sign < 0 else list(reversed(pts))), -0.020, 0.016)  # side spikes at the nose
    m.box("gun_light", (0.074, -0.232, 0.026), (0.082, 0.232, 0.032))                  # string across the limbs
    m.box("glow_red", (-0.070, -0.008, 0.030), (0.250, 0.008, 0.036))                  # red line on top
    m.prism("gun_dark", [(-0.040, -0.034), (0.020, -0.034), (0.000, -0.150), (-0.060, -0.145)], -0.016, 0.016)  # grip
    m.box("gun_dark", (0.020, -0.004, -0.070), (0.050, 0.004, -0.034))                 # trigger
    return m


def hellcat_rifle():
    # A dark rifle: stock, grey receiver, curved light grey magazine, maroon handguard over a grey cylinder with an
    # orange light, dark barrel and front sight.
    m = Model("hellcat_rifle")
    P = px(0.0068, 35, 14)
    m.prism("gun_dark", P([(0, 8), (0, 28), (10, 33), (30, 26), (30, 10)]), -0.022, 0.022)          # stock
    m.box("gun_metal", (-0.034, -0.026, -0.068), (0.238, 0.026, 0.041))                             # receiver
    m.prism("gun_dark", P([(27, 22), (25, 38), (35, 41), (40, 22)]), -0.017, 0.017)                 # grip
    m.prism("gun_light", P([(47, 24), (48, 30), (56, 40), (64, 52), (74, 46), (66, 34), (58, 24)]), -0.017, 0.017)  # magazine
    m.prism("paint_red", P([(66, 22), (90, 22), (90, 8), (72, 3), (66, 5)]), -0.030, 0.030)         # handguard
    m.tube("gun_metal", "x", ((70 - 35) * 0.0068, 0.0, (14 - 26) * 0.0068), 0.170, 0.032, sides=8)  # cylinder
    m.box("ember", ((88 - 35) * 0.0068, -0.025, (14 - 28) * 0.0068), ((93 - 35) * 0.0068, 0.025, (14 - 23) * 0.0068))
    m.box("gun_dark", ((90 - 35) * 0.0068, -0.020, (14 - 19) * 0.0068), ((121 - 35) * 0.0068, 0.020, (14 - 9) * 0.0068))
    m.prism("gun_dark", P([(105, 9), (113, 9), (113, 0), (110, 0)]), -0.008, 0.008)                # front sight
    m.prism("gun_dark", P([(40, 8), (52, 8), (50, 4), (44, 4)]), -0.010, 0.010)                    # rear sight
    return m


MODELS = [eltex_staff, flamebow, grenade_tox, nerve_spiker, hellcat_rifle]
ALL = [monosword, plasmasword, zeushammer, eltex_staff, toxbomb_launcher, grenade_tox, nerve_spiker,
          incinerator, hellcat_rifle]


def clear():
    for ob in list(bpy.data.objects):
        if ob.name != "sun":
            bpy.data.objects.remove(ob)


def export(ob):
    for o in bpy.context.scene.objects:
        o.select_set(o == ob)
    bpy.context.view_layer.objects.active = ob
    path = os.path.join(OUT, ob.name + ".obj")
    bpy.ops.wm.obj_export(filepath=path, export_selected_objects=True, export_materials=True,
                          export_normals=False, export_uv=True, export_triangulated_mesh=True, forward_axis="NEGATIVE_Z", up_axis="Y",
                          apply_modifiers=True)
    # Point the file at IsoRim's shared material library instead of the one Blender writes.
    lines = open(path).read().splitlines()
    head = ["# IsoRim model: " + ob.name,
            "# Built in Blender (make_weapons.py). One unit = one map cell, +Y up, -Z = north;",
            "# the grip at the origin, the muzzle / tip along +X. Materials: Models/isorim.mtl.",
            "mtllib isorim.mtl"]
    body = [l for l in lines if not l.startswith("#") and not l.startswith("mtllib")]
    open(path, "w").write("\n".join(head + body) + "\n")
    mtl = os.path.join(OUT, ob.name + ".mtl")
    if os.path.exists(mtl):
        os.remove(mtl)
    return path


def render(ob, path, direction=(0.35, -1.0, 0.55)):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 48
    scene.cycles.use_denoising = False
    scene.world = scene.world or bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.30, 0.32, 0.35, 1)
    scene.world.node_tree.nodes["Background"].inputs[1].default_value = 0.8
    scene.view_settings.view_transform = "Standard"
    if not bpy.data.objects.get("sun"):
        sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
        sun.data.energy = 3.0
        sun.rotation_euler = (math.radians(40), math.radians(-20), math.radians(30))
        scene.collection.objects.link(sun)
    scene.render.resolution_x = 900
    scene.render.resolution_y = 500
    lo = Vector([min(v.co[k] for v in ob.data.vertices) for k in range(3)])
    hi = Vector([max(v.co[k] for v in ob.data.vertices) for k in range(3)])
    centre = (lo + hi) / 2
    ext = hi - lo
    aspect = scene.render.resolution_x / scene.render.resolution_y
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = max(ext.x + ext.y * 0.4, (ext.z + ext.y * 0.5) * aspect, 0.2) * 1.2
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    direction = Vector(direction).normalized()
    cam.location = centre + direction * 3.0
    cam.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
    if direction.z > 0.99:
        cam.rotation_euler = (0.0, 0.0, 0.0)   # straight down, +X to the right (as the game's sprite)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)


for build in MODELS:
    clear()
    ob = build().finish()
    p = export(ob)
    render(ob, os.path.join(OUT, "previews", ob.name + ".png"))
    render(ob, os.path.join(OUT, "previews", ob.name + "_side.png"), (0.0, -1.0, 0.0))
    render(ob, os.path.join(OUT, "previews", ob.name + "_top.png"), (0.0, -0.0001, 1.0))
    print("built", p, len(ob.data.vertices), "verts", len(ob.data.polygons), "faces")
