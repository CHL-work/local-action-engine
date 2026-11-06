"""Procedural low-poly model kit for the Project Brainwash demos.

Runs inside Blender's Python (Blender 5.x, or the `bpy` wheel from PyPI) with
numpy and scikit-image available. Organic shapes are sculpted as signed
distance fields (SDF), meshed with marching cubes and decimated into faceted,
flat-shaded low-poly meshes. Hard-surface parts are built directly with bmesh.

Conventions (they matter for the Unity side, see Brainwash/Runtime/ModelLibrary.cs):
- Metres. Characters stand on z = 0 and face -Y in Blender, so they face +Z in
  Unity. Their own left side ("L" names) is +X in Blender.
- Colours come from one shared 8 x 8 palette (16 px cells). Every face's UVs sit
  in the centre of one palette cell; the game builds the palette at run time
  from palette.json and replaces the TINT cells with each monster's own colour.
- Objects whose names end in "_Glow" are drawn unlit (eyes, coals, sacs).
- A creature is one skinned mesh ("Body") on an armature ("Rig"), plus rigid
  parts parented to bones. Empties named "Socket.*" mark item and effect points.
"""
from __future__ import annotations

import json
import math
import os
from dataclasses import dataclass, field

import numpy as np

try:
    import bpy
    import bmesh
    from mathutils import Matrix, Vector
except ImportError:  # allows importing the palette tables without Blender
    bpy = None

# --------------------------------------------------------------------------
# Palette
# --------------------------------------------------------------------------
CELL_PX = 16
GRID = 8
TEX_PX = CELL_PX * GRID

# Cells 0-3 are tinted at run time with the entity's colour (base, shade, light, deep).
PALETTE = [
    # row 0: tint cells and flesh
    ("tint", (150, 150, 150)), ("tint_shade", (104, 104, 108)), ("tint_light", (196, 196, 196)), ("tint_deep", (70, 70, 76)),
    ("skin_light", (236, 196, 160)), ("skin", (206, 154, 118)), ("skin_dark", (150, 100, 76)), ("bone", (232, 222, 190)),
    # row 1: eyes, mouths, neutrals
    ("black", (18, 16, 20)), ("charcoal", (44, 42, 48)), ("white", (244, 244, 240)), ("eye_white", (250, 238, 200)),
    ("eye_yellow", (255, 214, 70)), ("eye_red", (255, 70, 46)), ("eye_green", (150, 255, 110)), ("eye_cyan", (130, 240, 255)),
    # row 2: metals and cloth
    ("gold", (240, 186, 64)), ("brass_dark", (150, 104, 40)), ("iron", (92, 96, 104)), ("iron_dark", (52, 54, 60)),
    ("steel", (170, 176, 186)), ("copper", (184, 104, 64)), ("cloth_red", (168, 38, 40)), ("cloth_red_dark", (104, 22, 28)),
    # row 3: wood, leather, paper
    ("wood_light", (176, 128, 80)), ("wood", (128, 86, 50)), ("wood_dark", (82, 54, 32)), ("leather", (110, 70, 42)),
    ("leather_dark", (66, 42, 28)), ("straw", (200, 170, 100)), ("parchment", (226, 208, 166)), ("paper", (246, 238, 220)),
    # row 4: stone, earth, ice
    ("stone_light", (168, 166, 158)), ("stone", (120, 120, 116)), ("stone_dark", (78, 78, 78)), ("moss", (86, 104, 58)),
    ("dirt", (108, 84, 60)), ("sand", (200, 178, 136)), ("ice_light", (200, 238, 255)), ("ice", (110, 180, 230)),
    # row 5: fire, acid, flesh accents
    ("fire_yellow", (255, 220, 90)), ("fire_orange", (255, 128, 30)), ("ember", (210, 56, 24)), ("acid", (140, 255, 70)),
    ("acid_dark", (60, 150, 40)), ("slime_purple", (130, 70, 170)), ("pink", (240, 150, 160)), ("pink_dark", (190, 90, 110)),
    # row 6: cloth and creature accents
    ("cloth_blue", (54, 82, 150)), ("cloth_green", (60, 110, 60)), ("cloth_purple", (100, 60, 130)), ("cloth_brown", (110, 80, 56)),
    ("cloth_gray", (110, 110, 116)), ("teal", (40, 140, 140)), ("beak", (240, 170, 50)), ("claw", (40, 34, 30)),
    # row 7: extras
    ("mouth", (90, 16, 26)), ("tongue", (220, 80, 100)), ("feather_dark", (34, 30, 40)), ("feather", (64, 58, 76)),
    ("toad_belly", (222, 214, 130)), ("toad_spot", (250, 200, 40)), ("glass_green", (150, 230, 140)), ("candle", (250, 240, 210)),
]
assert len(PALETTE) == GRID * GRID
CELL = {name: i for i, (name, _) in enumerate(PALETTE)}


def cell_uv(index: int) -> tuple[float, float]:
    """UV of a cell's centre. Row 0 is the top row of the image (v near 1)."""
    col, row = index % GRID, index // GRID
    return ((col + .5) / GRID, 1 - (row + .5) / GRID)


def tint_colors(rgb):
    """The four tint cells for an entity colour (mirrors ModelLibrary.TintCells in Unity)."""
    c = np.array(rgb, float)
    white = np.array([255.0, 255, 255])
    return [c, c * .62, c + (white - c) * .35, c * .38]


def palette_pixels(tint=None):
    img = np.zeros((TEX_PX, TEX_PX, 4), np.uint8)
    cells = [rgb for _, rgb in PALETTE]
    if tint is not None:
        for i, t in enumerate(tint_colors(tint)):
            cells[i] = tuple(int(round(min(255, max(0, v)))) for v in t)
    for i, rgb in enumerate(cells):
        col, row = i % GRID, i // GRID
        img[row * CELL_PX:(row + 1) * CELL_PX, col * CELL_PX:(col + 1) * CELL_PX] = (*rgb, 255)
    return img


def write_palette(png_path: str | None = None, json_path: str | None = None):
    if json_path:
        os.makedirs(os.path.dirname(json_path), exist_ok=True)
        with open(json_path, "w") as f:
            json.dump({"cellPixels": CELL_PX, "grid": GRID, "tintCells": [0, 1, 2, 3],
                       "cells": [{"name": n, "rgb": list(rgb)} for n, rgb in PALETTE]}, f, indent=1)
    if png_path:
        from PIL import Image
        os.makedirs(os.path.dirname(png_path), exist_ok=True)
        Image.fromarray(palette_pixels(), "RGBA").save(png_path)


# --------------------------------------------------------------------------
# SDF primitives (numpy). P is an (N, 3) array of points in model space.
# --------------------------------------------------------------------------
def rot(deg) -> np.ndarray:
    """Blender XYZ Euler (degrees) as a 3 x 3 matrix whose columns are the local axes."""
    x, y, z = np.radians(deg if deg is not None else (0, 0, 0))
    cx, sx, cy, sy, cz, sz = math.cos(x), math.sin(x), math.cos(y), math.sin(y), math.cos(z), math.sin(z)
    rx = np.array([[1, 0, 0], [0, cx, -sx], [0, sx, cx]])
    ry = np.array([[cy, 0, sy], [0, 1, 0], [-sy, 0, cy]])
    rz = np.array([[cz, -sz, 0], [sz, cz, 0], [0, 0, 1]])
    return rz @ ry @ rx


@dataclass
class Prim:
    kind: str
    params: dict
    color: str | None = None      # palette cell name
    bone: str | None = None       # bone that moves this part
    op: str = "add"               # add | sub | paint
    k: float = 0.0                # smooth blend radius (m)
    tau: float | None = None      # weight falloff (m); None = model default
    bias: float = 0.0             # ownership priority for colouring (m)
    mirror: bool = False          # also add the X-mirrored copy

    def d(self, P: np.ndarray) -> np.ndarray:
        p = self.params
        if self.kind == "ellipsoid":
            q = (P - p["c"]) @ p["R"]
            r = p["r"]
            k0 = np.linalg.norm(q / r, axis=1)
            k1 = np.linalg.norm(q / (r * r), axis=1)
            return k0 * (k0 - 1.0) / np.maximum(k1, 1e-9)
        if self.kind == "roundcone":
            return _round_cone(P, p["a"], p["b"], p["ra"], p["rb"])
        if self.kind == "box":
            q = np.abs((P - p["c"]) @ p["R"]) - p["h"] + p["round"]
            outside = np.linalg.norm(np.maximum(q, 0), axis=1)
            inside = np.minimum(np.max(q, axis=1), 0)
            return outside + inside - p["round"]
        if self.kind == "torus":
            q = (P - p["c"]) @ p["R"]
            xy = np.sqrt(q[:, 0] ** 2 + q[:, 1] ** 2) - p["R1"]
            return np.sqrt(xy ** 2 + q[:, 2] ** 2) - p["r2"]
        if self.kind == "cylinder":   # capped cylinder along local Z
            q = (P - p["c"]) @ p["R"]
            dxy = np.sqrt(q[:, 0] ** 2 + q[:, 1] ** 2) - p["r"]
            dz = np.abs(q[:, 2]) - p["h"]
            return np.minimum(np.maximum(dxy, dz), 0) + np.sqrt(np.maximum(dxy, 0) ** 2 + np.maximum(dz, 0) ** 2) - p.get("round", 0)
        if self.kind == "flatcone":   # round cone squashed along one side axis (ears, wings, blades)
            q = (P - p["a"]) @ p["R"]
            q[:, 2] /= p["f"]
            L = p["L"]
            return _round_cone(q, np.zeros(3), np.array([L, 0, 0]), p["ra"], p["rb"]) * p["f"]
        raise ValueError(self.kind)

    def mirrored(self) -> "Prim":
        p = dict(self.params)
        M = np.diag([-1.0, 1, 1])
        if self.kind == "flatcone":
            # Mirror the frame; keep local X along the limb, flip the side axis.
            p["a"] = p["a"] * np.array([-1.0, 1, 1])
            R = M @ p["R"]
            R[:, 1] *= -1
            p["R"] = R
            bone = self.bone
            if bone and bone.endswith(".L"):
                bone = bone[:-2] + ".R"
            elif bone and bone.endswith(".R"):
                bone = bone[:-2] + ".L"
            return Prim(self.kind, p, self.color, bone, self.op, self.k, self.tau, self.bias, False)
        for key in ("c", "a", "b"):
            if key in p:
                p[key] = p[key] * np.array([-1.0, 1, 1])
        if "R" in p:
            # Mirror the frame and keep it right-handed by flipping its local X.
            p["R"] = M @ p["R"] @ np.diag([-1.0, 1, 1])
        bone = self.bone
        if bone and bone.endswith(".L"):
            bone = bone[:-2] + ".R"
        elif bone and bone.endswith(".R"):
            bone = bone[:-2] + ".L"
        return Prim(self.kind, p, self.color, bone, self.op, self.k, self.tau, self.bias, False)

    def bounds(self):
        p = self.params
        if self.kind == "ellipsoid":
            e = np.abs(p["R"]) @ p["r"]
            return p["c"] - e, p["c"] + e
        if self.kind == "roundcone":
            lo = np.minimum(p["a"] - p["ra"], p["b"] - p["rb"])
            hi = np.maximum(p["a"] + p["ra"], p["b"] + p["rb"])
            return lo, hi
        if self.kind == "box":
            e = np.abs(p["R"]) @ p["h"]
            return p["c"] - e, p["c"] + e
        if self.kind == "torus":
            e = np.full(3, p["R1"] + p["r2"])
            return p["c"] - e, p["c"] + e
        if self.kind == "cylinder":
            e = np.abs(p["R"]) @ np.array([p["r"], p["r"], p["h"]])
            return p["c"] - e, p["c"] + e
        if self.kind == "flatcone":
            b = p["a"] + p["R"][:, 0] * p["L"]
            r = max(p["ra"], p["rb"])
            return np.minimum(p["a"], b) - r, np.maximum(p["a"], b) + r
        raise ValueError(self.kind)


def _round_cone(P, a, b, r1, r2):
    a = np.asarray(a, float)
    b = np.asarray(b, float)
    ba = b - a
    l2 = float(ba @ ba)
    rr = r1 - r2
    a2 = l2 - rr * rr
    il2 = 1.0 / l2
    pa = P - a
    y = pa @ ba
    z = y - l2
    x = pa * l2 - np.outer(y, ba)
    x2 = np.einsum("ij,ij->i", x, x)
    y2 = y * y * l2
    z2 = z * z * l2
    k = np.sign(rr) * rr * rr * x2
    out = (np.sqrt(np.maximum(x2 * a2 * il2, 0)) + y * rr) * il2 - r1
    c1 = np.sign(z) * a2 * z2 > k
    c2 = np.sign(y) * a2 * y2 < k
    out = np.where(c2, np.sqrt(x2 + y2) * il2 - r1, out)
    out = np.where(c1, np.sqrt(x2 + z2) * il2 - r2, out)
    return out


def V(*xyz):
    return np.array(xyz, float)


def ellipsoid(c, r, rotation=None, **kw):
    return Prim("ellipsoid", {"c": V(*c), "r": V(*r), "R": rot(rotation)}, **kw)


def sphere(c, radius, **kw):
    return ellipsoid(c, (radius, radius, radius), **kw)


def limb(a, b, ra, rb=None, **kw):
    """A capsule whose radius changes from ra at a to rb at b."""
    rb = ra if rb is None else rb
    if abs(ra - rb) >= np.linalg.norm(V(*b) - V(*a)) * .98:
        rb = ra - np.sign(ra - rb) * np.linalg.norm(V(*b) - V(*a)) * .98
    return Prim("roundcone", {"a": V(*a), "b": V(*b), "ra": float(ra), "rb": float(rb)}, **kw)


def flat_limb(a, b, ra, rb, flatten=.35, normal=(0, 0, 1), **kw):
    """A round cone from a to b squashed to `flatten` of its width along `normal`
    (made perpendicular to the limb). Good for ears, fins, wing membranes, blades."""
    a, b = V(*a), V(*b)
    x = b - a
    L = float(np.linalg.norm(x))
    x /= L
    n = V(*normal)
    n = n - x * (n @ x)
    if np.linalg.norm(n) < 1e-6:
        n = np.cross(x, V(1, 0, 0) if abs(x[0]) < .9 else V(0, 1, 0))
    n /= np.linalg.norm(n)
    y = np.cross(n, x)
    R = np.stack([x, y, n], axis=1)
    return Prim("flatcone", {"a": a, "R": R, "L": L, "ra": float(ra), "rb": float(rb), "f": float(flatten)}, **kw)


def box(c, size, rotation=None, round=0.0, **kw):
    return Prim("box", {"c": V(*c), "h": V(*size) * .5, "R": rot(rotation), "round": float(round)}, **kw)


def torus(c, ring_radius, tube_radius, rotation=None, **kw):
    return Prim("torus", {"c": V(*c), "R1": float(ring_radius), "r2": float(tube_radius), "R": rot(rotation)}, **kw)


def cylinder(c, radius, height, rotation=None, round=0.0, **kw):
    return Prim("cylinder", {"c": V(*c), "r": float(radius), "h": float(height) * .5, "R": rot(rotation), "round": float(round)}, **kw)


def smin(a, b, k):
    if k <= 0:
        return np.minimum(a, b)
    h = np.clip(.5 + .5 * (b - a) / k, 0, 1)
    return b * (1 - h) + a * h - k * h * (1 - h)


def smax(a, b, k):
    return -smin(-a, -b, k)


# --------------------------------------------------------------------------
# Model description
# --------------------------------------------------------------------------
@dataclass
class Bone:
    name: str
    head: np.ndarray
    tail: np.ndarray
    parent: str | None
    roll: float = 0.0


@dataclass
class Piece:
    """One mesh object. `prims` (SDF) or `build` (bmesh callback) makes its geometry."""
    name: str
    prims: list = field(default_factory=list)
    build: object = None          # callable(bm) -> None for direct bmesh geometry
    bone: str | None = None       # rigid parent bone; None = skinned (weights from prims)
    faces: int = 1500             # decimation target (SDF pieces)
    voxel: float = .02            # marching-cubes cell size (m)
    tau: float = .05              # default weight falloff (m)
    symmetric: bool = True
    glow: bool = False
    smooth: float = 0.0           # 0 = flat shading; >0 = auto-smooth angle in degrees


class Model:
    def __init__(self, name: str):
        self.name = name
        self.bones: dict[str, Bone] = {}
        self.pieces: list[Piece] = []
        self.sockets: dict[str, tuple] = {}   # name -> (bone, position, rotation deg)
        self.preview_tint = None              # RGB 0-255 used for preview renders only
        self.gait = "Biped"                   # CreatureAnimator.GaitKind in Unity
        self.height = None                    # design height (SpeciesCatalog); None = measured
        self.kind = "creature"
        self.preview_height = None

    # bones ------------------------------------------------------------
    def bone(self, name, head, tail, parent=None, roll=0.0, mirror=False):
        self.bones[name] = Bone(name, V(*head), V(*tail), parent, roll)
        if mirror:
            mname = name.replace(".L", ".R")
            mparent = parent.replace(".L", ".R") if parent else None
            self.bones[mname] = Bone(mname, V(*head) * V(-1, 1, 1), V(*tail) * V(-1, 1, 1), mparent, -roll)
        return self

    def socket(self, name, bone, position, rotation=(0, 0, 0)):
        self.sockets[name] = (bone, V(*position), rotation)
        return self

    def piece(self, name, prims=None, **kw) -> Piece:
        expanded = []
        for p in prims or []:
            expanded.append(p)
            if p.mirror:
                expanded.append(p.mirrored())
        piece = Piece(name, expanded, **kw)
        self.pieces.append(piece)
        return piece


# --------------------------------------------------------------------------
# SDF evaluation helpers
# --------------------------------------------------------------------------
def sdf_eval(prims, P):
    """Returns (final distance, per-prim distances list)."""
    ds = [p.d(P) for p in prims]
    d = None
    for p, di in zip(prims, ds):
        if p.op == "add":
            d = di.copy() if d is None else smin(d, di, p.k)
        elif p.op == "sub":
            d = smax(d, -di, p.k)
    return d, ds


def sdf_union_only(prims, ds):
    d = None
    for p, di in zip(prims, ds):
        if p.op == "add":
            d = di.copy() if d is None else smin(d, di, p.k)
    return d


def colors_at(prims, P, eps):
    d, ds = sdf_eval(prims, P)
    add_idx = [i for i, p in enumerate(prims) if p.op == "add"]
    D = np.stack([ds[i] - prims[i].bias for i in add_idx])
    owner = np.array(add_idx)[np.argmin(D, axis=0)]
    names = np.array([prims[i].color or "tint" for i in owner], dtype=object)
    du = sdf_union_only(prims, ds)
    for i, p in enumerate(prims):
        if p.op == "sub" and p.color:
            carved = (np.abs(ds[i]) < eps) & (du < -eps * .5)
            names[carved] = p.color
    for i, p in enumerate(prims):
        if p.op == "paint":
            names[ds[i] < 0] = p.color
    return names


def weights_at(prims, P, default_tau, bones):
    ds = []
    bone_of = []
    taus = []
    for p in prims:
        if p.op == "add" and p.bone:
            ds.append(p.d(P))
            bone_of.append(p.bone)
            taus.append(p.tau or default_tau)
    D = np.stack(ds)
    dmin = D.min(axis=0)
    W = np.exp(-np.maximum(D - dmin, 0) / np.array(taus)[:, None])
    names = sorted(set(bone_of))
    out = np.zeros((len(names), P.shape[0]))
    for row, b in enumerate(bone_of):
        out[names.index(b)] += W[row]
    out /= out.sum(axis=0, keepdims=True)
    # keep the four strongest influences
    if len(names) > 4:
        order = np.argsort(-out, axis=0)
        mask = np.zeros_like(out, bool)
        for j in range(4):
            mask[order[j], np.arange(out.shape[1])] = True
        out = np.where(mask, out, 0)
    out[out < .03] = 0
    out /= np.maximum(out.sum(axis=0, keepdims=True), 1e-9)
    return names, out


def mesh_sdf(prims, voxel, symmetric=True):
    from skimage import measure
    lo = np.full(3, np.inf)
    hi = np.full(3, -np.inf)
    for p in prims:
        if p.op != "add":
            continue
        a, b = p.bounds()
        lo = np.minimum(lo, a)
        hi = np.maximum(hi, b)
    lo -= voxel * 3
    hi += voxel * 3
    if symmetric:
        m = max(abs(lo[0]), abs(hi[0]))
        lo[0], hi[0] = -m, m
    n = np.ceil((hi - lo) / voxel).astype(int) + 1
    if symmetric and n[0] % 2 == 0:
        n[0] += 1
    xs = lo[0] + np.arange(n[0]) * voxel
    if symmetric:
        xs = np.linspace(-(n[0] - 1) * voxel / 2, (n[0] - 1) * voxel / 2, n[0])
    ys = lo[1] + np.arange(n[1]) * voxel
    zs = lo[2] + np.arange(n[2]) * voxel
    X, Y, Z = np.meshgrid(xs, ys, zs, indexing="ij")
    P = np.stack([X.ravel(), Y.ravel(), Z.ravel()], axis=1)
    d, _ = sdf_eval(prims, P)
    vol = d.reshape(n)
    verts, faces, _, _ = measure.marching_cubes(vol, level=0.0, spacing=(voxel, voxel, voxel))
    verts += np.array([xs[0], ys[0], zs[0]])
    return verts, faces


# --------------------------------------------------------------------------
# Blender construction
# --------------------------------------------------------------------------
def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def set_preview_tint(rgb):
    """Recolours the palette image's tint cells (preview only; FBX files keep gray)."""
    img = bpy.data.images.get("palette.png")
    if img is None:
        return
    px = palette_pixels(rgb)[::-1].astype(np.float32) / 255.0
    if img.colorspace_settings.name != "sRGB":
        c = px[..., :3]
        px[..., :3] = np.where(c <= .04045, c / 12.92, ((c + .055) / 1.055) ** 2.4)
    img.pixels.foreach_set(px.ravel())
    img.update()


def palette_material(name="Palette", glow=False, image_path=None):
    mat = bpy.data.materials.get(name)
    if mat:
        return mat
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nt = mat.node_tree
    bsdf = nt.nodes.get("Principled BSDF")
    tex = nt.nodes.new("ShaderNodeTexImage")
    tex.interpolation = "Closest"
    img = bpy.data.images.get("palette.png")
    if img is None:
        if image_path and os.path.exists(image_path):
            img = bpy.data.images.load(image_path)
        else:
            img = bpy.data.images.new("palette.png", TEX_PX, TEX_PX, alpha=True)
            px = palette_pixels()[::-1].astype(np.float32) / 255.0
            # sRGB to linear for the generated buffer
            rgb = px[..., :3]
            px[..., :3] = np.where(rgb <= .04045, rgb / 12.92, ((rgb + .055) / 1.055) ** 2.4)
            img.pixels.foreach_set(px.ravel())
            img.colorspace_settings.name = "Linear Rec.709"
    tex.image = img
    nt.links.new(tex.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = .85
    if glow:
        nt.links.new(tex.outputs["Color"], bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 2.0
    return mat


def _assign_uvs_by_names(me, names):
    uv = me.uv_layers.new(name="UVMap")
    for poly, cname in zip(me.polygons, names):
        u, v = cell_uv(CELL[cname])
        for li in poly.loop_indices:
            uv.data[li].uv = (u, v)


def _new_object(name, me):
    obj = bpy.data.objects.new(name, me)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def _decimate(obj, target_faces, symmetric):
    faces = len(obj.data.polygons)
    if faces <= target_faces:
        return
    mod = obj.modifiers.new("Decimate", "DECIMATE")
    mod.decimate_type = "COLLAPSE"
    mod.ratio = target_faces / faces
    mod.use_symmetry = symmetric
    mod.symmetry_axis = "X"
    dg = bpy.context.evaluated_depsgraph_get()
    ev = obj.evaluated_get(dg)
    me = bpy.data.meshes.new_from_object(ev)
    obj.modifiers.clear()
    old = obj.data
    obj.data = me
    bpy.data.meshes.remove(old)


def _shade(me, smooth_angle):
    if smooth_angle and smooth_angle > 0:
        for p in me.polygons:
            p.use_smooth = True
        # Blender 4.1+: sharp edges by angle
        bm = bmesh.new()
        bm.from_mesh(me)
        for e in bm.edges:
            if e.is_manifold and e.calc_face_angle(0) > math.radians(smooth_angle):
                e.smooth = False
        bm.to_mesh(me)
        bm.free()
    else:
        for p in me.polygons:
            p.use_smooth = False


def build_sdf_piece(model: Model, piece: Piece):
    verts, faces = mesh_sdf(piece.prims, piece.voxel, piece.symmetric)
    me = bpy.data.meshes.new(piece.name)
    me.from_pydata(verts.tolist(), [], faces.tolist())
    me.update()
    bm = bmesh.new()
    bm.from_mesh(me)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=piece.voxel * .05)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    bm.to_mesh(me)
    bm.free()
    obj = _new_object(piece.name, me)
    _decimate(obj, piece.faces, piece.symmetric)
    me = obj.data
    centers = np.array([p.center[:] for p in me.polygons])
    names = colors_at(piece.prims, centers, piece.voxel * 1.6)
    _assign_uvs_by_names(me, names)
    _shade(me, piece.smooth)
    return obj


class Mesher:
    """Direct low-poly geometry with per-face palette colours."""

    def __init__(self):
        self.bm = bmesh.new()
        # Create the colour layer first: adding a layer later invalidates face references.
        self.layer = self.bm.faces.layers.int.new("cell")

    def _tag(self, faces, color):
        for f in faces:
            f[self.layer] = CELL[color]

    def _place(self, geom_verts, c, rotation, scale=None):
        R = Matrix(rot(rotation).tolist()).to_4x4() if rotation is not None else Matrix.Identity(4)
        S = Matrix.Diagonal((*scale, 1)) if scale is not None else Matrix.Identity(4)
        M = Matrix.Translation(Vector(c)) @ R @ S
        bmesh.ops.transform(self.bm, matrix=M, verts=geom_verts)

    def _new_faces(self, before):
        return [f for f in self.bm.faces if f.index == -1 or f not in before]

    def box(self, c, size, color, rotation=None, bevel=0.0, taper=None):
        before = set(self.bm.faces)
        res = bmesh.ops.create_cube(self.bm, size=1.0)
        verts = res["verts"]
        if taper:  # scale the top face (taper = (sx, sy) at +Z)
            for v in verts:
                if v.co.z > 0:
                    v.co.x *= taper[0]
                    v.co.y *= taper[1]
        self._place(verts, c, rotation, size)
        new = [f for f in self.bm.faces if f not in before]
        if bevel > 0:
            edges = list({e for f in new for e in f.edges})
            bmesh.ops.bevel(self.bm, geom=edges, offset=bevel, segments=1, affect="EDGES", profile=.5)
            new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def cylinder(self, c, radius, height, color, sides=8, rotation=None, radius_top=None, cap=True):
        before = set(self.bm.faces)
        res = bmesh.ops.create_cone(self.bm, cap_ends=cap, cap_tris=False, segments=sides,
                                    radius1=radius, radius2=radius if radius_top is None else radius_top, depth=height)
        self._place(res["verts"], c, rotation)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def cone(self, c, radius, height, color, sides=6, rotation=None):
        return self.cylinder(c, radius, height, color, sides, rotation, radius_top=0.0)

    def ico(self, c, radius, color, subdiv=1, scale=(1, 1, 1), rotation=None):
        before = set(self.bm.faces)
        res = bmesh.ops.create_icosphere(self.bm, subdivisions=subdiv, radius=1.0)
        self._place(res["verts"], c, rotation, V(*scale) * radius)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def sphere(self, c, radius, color, segments=8, rings=5, scale=(1, 1, 1), rotation=None):
        before = set(self.bm.faces)
        res = bmesh.ops.create_uvsphere(self.bm, u_segments=segments, v_segments=rings, radius=1.0)
        self._place(res["verts"], c, rotation, V(*scale) * radius)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def prism(self, points, z0, z1, color, top=None, side=None, offset=(0, 0, 0), rotation=None):
        """A closed prism: a convex 2-D outline (x, y) extruded from z0 to z1."""
        before = set(self.bm.faces)
        pts = list(points)
        area = sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(pts, pts[1:] + pts[:1]))
        if area < 0:
            pts.reverse()
        lo = [self.bm.verts.new((x, y, z0)) for x, y in pts]
        hi = [self.bm.verts.new((x, y, z1)) for x, y in pts]
        self.bm.faces.new(list(reversed(lo)))
        top_face = self.bm.faces.new(hi)
        sides = []
        n = len(pts)
        for i in range(n):
            j = (i + 1) % n
            sides.append(self.bm.faces.new((lo[i], lo[j], hi[j], hi[i])))
        self._place(lo + hi, offset, rotation)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        if top:
            self._tag([top_face], top)
        if side:
            self._tag(sides, side)
        return new

    def rock(self, c, radius, color, subdiv=1, jitter=.22, seed=0, scale=(1, 1, 1), rotation=None, flat_bottom=None):
        """A lumpy low-poly rock: an icosphere with its vertices pushed in and out."""
        before = set(self.bm.faces)
        res = bmesh.ops.create_icosphere(self.bm, subdivisions=subdiv, radius=1.0)
        rng = np.random.default_rng(seed)
        for v in res["verts"]:
            v.co *= 1 + rng.uniform(-jitter, jitter)
            if flat_bottom is not None and v.co.z < -flat_bottom:
                v.co.z = -flat_bottom
        self._place(res["verts"], c, rotation, V(*scale) * radius)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def torus(self, c, ring, tube, color, sides=10, tube_sides=4, rotation=None):
        before = set(self.bm.faces)
        verts = []
        rows = []
        for i in range(sides):
            a = 2 * math.pi * i / sides
            row = []
            for j in range(tube_sides):
                b = 2 * math.pi * j / tube_sides + math.pi / tube_sides
                r = ring + tube * math.cos(b)
                row.append(self.bm.verts.new((r * math.cos(a), r * math.sin(a), tube * math.sin(b))))
            rows.append(row)
            verts.extend(row)
        for i in range(sides):
            for j in range(tube_sides):
                a, b = rows[i][j], rows[(i + 1) % sides][j]
                c2, d = rows[(i + 1) % sides][(j + 1) % tube_sides], rows[i][(j + 1) % tube_sides]
                self.bm.faces.new((a, b, c2, d))
        self._place(verts, c, rotation)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def poly(self, points, color, thickness=0.0):
        """A flat polygon (list of 3D points); with thickness it becomes a slab."""
        before = set(self.bm.faces)
        vs = [self.bm.verts.new(p) for p in points]
        f = self.bm.faces.new(vs)
        if thickness > 0:
            f.normal_update()
            res = bmesh.ops.extrude_face_region(self.bm, geom=[f])
            moved = [v for v in res["geom"] if isinstance(v, bmesh.types.BMVert)]
            n = f.normal.copy()
            bmesh.ops.translate(self.bm, verts=moved, vec=n * -thickness)
        new = [f for f in self.bm.faces if f not in before]
        self._tag(new, color)
        return new

    def to_object(self, name, smooth_angle=0.0):
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        lay = self.layer
        cells = [f[lay] for f in self.bm.faces]
        me = bpy.data.meshes.new(name)
        self.bm.to_mesh(me)
        self.bm.free()
        names = [PALETTE[c][0] for c in cells]
        _assign_uvs_by_names(me, names)
        _shade(me, smooth_angle)
        return _new_object(name, me)


def build_armature(model: Model):
    arm = bpy.data.armatures.new("Rig")
    obj = _new_object("Rig", arm)
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.mode_set(mode="EDIT")
    for b in model.bones.values():
        eb = arm.edit_bones.new(b.name)
        eb.head = Vector(b.head)
        eb.tail = Vector(b.tail)
        eb.roll = math.radians(b.roll)
    for b in model.bones.values():
        if b.parent:
            arm.edit_bones[b.name].parent = arm.edit_bones[b.parent]
            arm.edit_bones[b.name].use_connect = False
    bpy.ops.object.mode_set(mode="OBJECT")
    return obj


def parent_to_bone(obj, rig, bone_name):
    world = obj.matrix_world.copy()
    obj.parent = rig
    obj.parent_type = "BONE"
    obj.parent_bone = bone_name
    bpy.context.view_layer.update()
    obj.matrix_world = world


def skin(obj, rig, piece: Piece, model: Model):
    me = obj.data
    P = np.array([v.co[:] for v in me.vertices])
    names, W = weights_at(piece.prims, P, piece.tau, model.bones)
    groups = {n: obj.vertex_groups.new(name=n) for n in names}
    for row, n in enumerate(names):
        g = groups[n]
        for vi in np.nonzero(W[row] > 0)[0]:
            g.add([int(vi)], float(W[row, vi]), "REPLACE")
    obj.parent = rig
    mod = obj.modifiers.new("Armature", "ARMATURE")
    mod.object = rig


def build_model(model: Model, palette_png=None):
    """Creates every object of the model in the current (empty) scene."""
    mat = palette_material(image_path=palette_png)
    glow = palette_material("Glow", glow=True, image_path=palette_png)
    rig = build_armature(model) if model.bones else None
    objects = []
    for piece in model.pieces:
        if piece.build is not None:
            m = Mesher()
            piece.build(m)
            obj = m.to_object(piece.name + ("_Glow" if piece.glow and not piece.name.endswith("_Glow") else ""), piece.smooth)
        else:
            obj = build_sdf_piece(model, piece)
            if piece.glow and not obj.name.endswith("_Glow"):
                obj.name = piece.name + "_Glow"
        obj.data.materials.append(glow if piece.glow else mat)
        if rig is not None:
            if piece.bone:
                parent_to_bone(obj, rig, piece.bone)
            else:
                skin(obj, rig, piece, model)
        objects.append(obj)
    for name, (bone, pos, rotation) in model.sockets.items():
        empty = bpy.data.objects.new(name, None)
        empty.empty_display_size = .05
        bpy.context.scene.collection.objects.link(empty)
        empty.matrix_world = Matrix.Translation(Vector(pos)) @ Matrix(rot(rotation).tolist()).to_4x4()
        if rig is not None and bone:
            parent_to_bone(empty, rig, bone)
    return rig, objects


def export_fbx(path):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    bpy.ops.export_scene.fbx(
        filepath=path, use_selection=False, object_types={"ARMATURE", "MESH", "EMPTY"},
        apply_unit_scale=True, apply_scale_options="FBX_SCALE_ALL", bake_space_transform=False,
        axis_forward="-Z", axis_up="Y", mesh_smooth_type="FACE", use_mesh_modifiers=False,
        add_leaf_bones=False, primary_bone_axis="Y", secondary_bone_axis="X",
        armature_nodetype="NULL", bake_anim=False, path_mode="COPY", embed_textures=True,
        use_armature_deform_only=False, colors_type="NONE")


def model_bounds(objects):
    """Axis-aligned bounds of all mesh vertices in Blender space: (min, max)."""
    lo = np.full(3, np.inf)
    hi = np.full(3, -np.inf)
    for o in objects:
        if o.type == "MESH":
            M = o.matrix_world
            for v in o.data.vertices:
                p = np.array((M @ v.co)[:])
                lo = np.minimum(lo, p)
                hi = np.maximum(hi, p)
    return lo, hi


def model_height(objects):
    top = 0.0
    for o in objects:
        if o.type == "MESH":
            M = o.matrix_world
            for v in o.data.vertices:
                top = max(top, (M @ v.co).z)
    return top


def stats(objects):
    tris = 0
    for o in objects:
        if o.type == "MESH":
            tris += sum(len(p.vertices) - 2 for p in o.data.polygons)
    return tris


# --------------------------------------------------------------------------
# Preview renders (Cycles CPU, for checking work without Unity)
# --------------------------------------------------------------------------
TEST_POSE = {
    # bone: Euler XYZ degrees in the bone's own frame (Y runs along the bone)
    "UpperArm.L": (55, 0, 0), "UpperArm.R": (-35, 0, 25), "Forearm.L": (40, 0, 0), "Forearm.R": (30, 0, 0),
    "Thigh.L": (35, 0, 0), "Thigh.R": (-30, 0, 0), "Shin.L": (-50, 0, 0), "Shin.R": (-20, 0, 0),
    "Head": (0, 0, 25), "Neck": (10, 0, 0), "Spine": (12, 0, 0),
    "Wing.L": (0, 0, 35), "Wing.R": (0, 0, -35), "WingTip.L": (0, 0, 25), "WingTip.R": (0, 0, -25),
    "Tail1": (0, 0, 25), "Tail2": (0, 0, 25), "Tail3": (0, 0, 25), "Lid": (-60, 0, 0), "Jaw": (25, 0, 0),
    "Ear.L": (0, 0, 20), "Ear.R": (0, 0, -20),
}


def apply_test_pose(rig):
    """Bends the main joints so a preview shows how the skin weights deform."""
    if rig is None:
        return
    for pb in rig.pose.bones:
        if pb.name in TEST_POSE:
            pb.rotation_mode = "XYZ"
            pb.rotation_euler = [math.radians(a) for a in TEST_POSE[pb.name]]
    bpy.context.view_layer.update()


def render_preview(path, height=1.8, views=((35, 20), (90, 10), (200, 15)), size=360, pose=None, focus=None, floor_z=0.0):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.device = "CPU"
    scene.cycles.samples = 24
    scene.cycles.use_denoising = False
    scene.render.resolution_x = size
    scene.render.resolution_y = size
    scene.render.film_transparent = False
    world = bpy.data.worlds.get("Preview") or bpy.data.worlds.new("Preview")
    scene.world = world
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs[0].default_value = (.32, .34, .38, 1)
    world.node_tree.nodes["Background"].inputs[1].default_value = .7
    for name, rotation, energy, color in (("Key", (50, 0, 40), 3.2, (1, .92, .82)), ("Fill", (60, 0, -120), 1.0, (.7, .8, 1))):
        if bpy.data.objects.get(name) is None:
            light = bpy.data.objects.new(name, bpy.data.lights.new(name, "SUN"))
            scene.collection.objects.link(light)
            light.rotation_euler = [math.radians(a) for a in rotation]
            light.data.energy = energy
            light.data.color = color
    if bpy.data.objects.get("Floor") is None:
        bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, floor_z))
        floor = bpy.context.active_object
        floor.name = "Floor"
        fm = bpy.data.materials.new("FloorMat")
        fm.use_nodes = True
        fm.node_tree.nodes["Principled BSDF"].inputs["Base Color"].default_value = (.22, .22, .24, 1)
        floor.data.materials.append(fm)
    cam = bpy.data.objects.get("PreviewCam")
    if cam is None:
        cam = bpy.data.objects.new("PreviewCam", bpy.data.cameras.new("PreviewCam"))
        scene.collection.objects.link(cam)
    scene.camera = cam
    cam.data.lens = 50
    target = Vector(focus) if focus is not None else Vector((0, 0, height * .52))
    dist = height * 1.9 + .45
    frames = []
    from PIL import Image
    for i, (yaw, pitch) in enumerate(views):
        a, e = math.radians(yaw), math.radians(pitch)
        # yaw 0 = looking at the model's front (model faces -Y)
        pos = target + Vector((math.sin(a) * math.cos(e), -math.cos(a) * math.cos(e), math.sin(e))) * dist
        cam.location = pos
        cam.rotation_euler = (target - pos).to_track_quat("-Z", "Y").to_euler()
        tmp = f"{path}.{i}.png"
        scene.render.filepath = tmp
        bpy.ops.render.render(write_still=True)
        frames.append(Image.open(tmp).convert("RGB"))
        os.remove(tmp)
    sheet = Image.new("RGB", (size * len(frames), size))
    for i, f in enumerate(frames):
        sheet.paste(f, (i * size, 0))
    sheet.save(path)
    return path
