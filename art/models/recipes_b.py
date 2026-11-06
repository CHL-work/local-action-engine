"""Monster recipes, batch B: ice golem, mimic, bomb toad and cheese mouse.

Heights follow SpeciesCatalog.cs. Characters face -Y (Unity +Z); their left is +X.
"""
import math

import numpy as np

from bwkit import CELL, Model, V, box, cylinder, ellipsoid, flat_limb, limb, sphere, torus
from creatures import humanoid_bones, eyes


# ---------------------------------------------------------------------------
# Small geometry helpers for hard-surface (Mesher) parts
# ---------------------------------------------------------------------------
def _euler(R):
    """Blender XYZ Euler angles (degrees) of a rotation matrix (inverse of bwkit.rot)."""
    R = np.asarray(R, float)
    ry = math.asin(max(-1.0, min(1.0, -R[2, 0])))
    rx = math.atan2(R[2, 1], R[2, 2])
    rz = math.atan2(R[1, 0], R[0, 0])
    return (math.degrees(rx), math.degrees(ry), math.degrees(rz))


def _frame(z_axis, x_hint=(1, 0, 0)):
    """Right-handed frame (columns x, y, z) whose local Z runs along z_axis."""
    z = V(*z_axis)
    z /= np.linalg.norm(z)
    x = V(*x_hint)
    x = x - z * (x @ z)
    if np.linalg.norm(x) < 1e-6:
        x = np.cross(V(0, 1, 0) if abs(z[1]) < .9 else V(0, 0, 1), z)
    x /= np.linalg.norm(x)
    return np.stack([x, np.cross(z, x), z], axis=1)


def _axes(x_axis, y_axis):
    """Frame from a local X and an approximate local Y."""
    x = V(*x_axis)
    x /= np.linalg.norm(x)
    y = V(*y_axis)
    y = y - x * (y @ x)
    y /= np.linalg.norm(y)
    return np.stack([x, y, np.cross(x, y)], axis=1)


def seg_box(mesh, a, b, w, d, color, bevel=0.0, taper=None, pad=0.0, x_hint=(1, 0, 0)):
    """A box whose local Z runs from a to b (w across local X, d across local Y)."""
    a, b = V(*a), V(*b)
    L = float(np.linalg.norm(b - a)) + 2 * pad
    R = _frame(b - a, x_hint)
    return mesh.box(tuple((a + b) / 2), (w, d, L), color, rotation=_euler(R), bevel=bevel, taper=taper)


def frame_box(mesh, c, size, R, color, bevel=0.0, taper=None):
    return mesh.box(tuple(V(*c)), size, color, rotation=_euler(R), bevel=bevel, taper=taper)


def crystal(mesh, base, tip, r, color, sides=5, shoulder=.62, r_top=None, x_hint=(1, 0, 0), spin=0.0, tip_color=None):
    """A faceted ice crystal: a prism from base, then a pointed cap up to tip."""
    bm = mesh.bm
    base, tip = V(*base), V(*tip)
    ax = tip - base
    R = _frame(ax, x_hint)
    r_top = r if r_top is None else r_top
    ring0, ring1 = [], []
    for i in range(sides):
        a = 2 * math.pi * i / sides + math.radians(spin)
        u = V(math.cos(a), math.sin(a), 0)
        ring0.append(bm.verts.new(tuple(base + R @ (u * r))))
        ring1.append(bm.verts.new(tuple(base + ax * shoulder + R @ (u * r_top))))
    apex = bm.verts.new(tuple(tip))
    faces = [bm.faces.new(ring0[::-1])]
    sides_f, caps = [], []
    for i in range(sides):
        j = (i + 1) % sides
        sides_f.append(bm.faces.new((ring0[i], ring0[j], ring1[j], ring1[i])))
        caps.append(bm.faces.new((ring1[i], ring1[j], apex)))
    for f in faces + sides_f:
        f[mesh.layer] = CELL[color]
    for f in caps:
        f[mesh.layer] = CELL[tip_color or color]
    return faces + sides_f + caps


def recolor(mesh, faces, color, test):
    """Recolours the faces whose (outward) normal passes test(normal)."""
    for f in faces:
        f.normal_update()
        if test(np.array(f.normal[:])):
            f[mesh.layer] = CELL[color]


def chunk(mesh, c, size, color, rotation=(0, 0, 0), seed=0, jitter=.14, taper=(1, 1), extra=2):
    """An irregular faceted block (ice, rock): the convex hull of jittered box corners.

    taper scales the top (+Z) corners; extra adds a few bulges on random faces."""
    import bmesh  # Blender only
    rng = np.random.RandomState(seed)
    h = V(*size) / 2
    pts = []
    for sz in (-1, 1):
        for sx in (-1, 1):
            for sy in (-1, 1):
                p = V(sx, sy, sz) * h * (1 - rng.uniform(0, jitter, 3))
                if sz > 0:
                    p[0] *= taper[0]
                    p[1] *= taper[1]
                pts.append(p)
    for _ in range(extra):
        axis = rng.randint(3)
        p = rng.uniform(-.55, .55, 3) * h * 2 * .5
        p[axis] = h[axis] * rng.choice((-1, 1)) * (1 + rng.uniform(.04, .12))
        pts.append(p)
    R = np.asarray(rotation) if np.ndim(rotation) == 2 else None
    if R is None:
        from bwkit import rot
        R = rot(rotation)
    c = V(*c)
    verts = [mesh.bm.verts.new(tuple(c + R @ p)) for p in pts]
    res = bmesh.ops.convex_hull(mesh.bm, input=verts)
    junk = [g for g in res["geom_interior"] + res["geom_unused"] if isinstance(g, bmesh.types.BMVert)]
    if junk:
        bmesh.ops.delete(mesh.bm, geom=junk, context="VERTS")
    faces = [g for g in res["geom"] if isinstance(g, bmesh.types.BMFace) and g.is_valid]
    for f in faces:
        f[mesh.layer] = CELL[color]
    return faces


def mirror_x(p):
    return V(-p[0], p[1], p[2])


def look_eyes(m, centers, radius, look, color, pupil="slit", bone="Head", scale=(1, 1, 1), pupil_size=(.28, .5), pupil_drop=0.0):
    """Glowing eyes (+ dark pupils) whose fronts point along `look` (any direction).

    centers: list of eye centres; look: one direction (or one per eye)."""
    looks = [V(*look)] * len(centers) if np.ndim(look) == 1 else [V(*d) for d in look]
    rx, ry, rz = V(*scale) * radius

    def frame_for(d):
        d = d / np.linalg.norm(d)
        # local -Y looks along d; local Z stays as close to world up as possible
        y = -d
        up = V(0, 0, 1) if abs(d[2]) < .95 else V(0, 1, 0)
        x = np.cross(y, up)
        x /= np.linalg.norm(x)
        z = np.cross(x, y)
        return np.stack([x, y, z], axis=1)

    def glow(mesh):
        for c, d in zip(centers, looks):
            mesh.sphere(tuple(V(*c)), 1.0, color, segments=8, rings=6, scale=(rx, ry, rz), rotation=_euler(frame_for(d)))

    def pupils(mesh):
        for c, d in zip(centers, looks):
            R = frame_for(d)
            if pupil == "slit":
                p = V(*c) + R @ V(0, -ry * .84, 0)
                mesh.box(tuple(p), (rx * pupil_size[0], ry * .3, rz * pupil_size[1] * 2.6), "black", rotation=_euler(R))
            elif pupil == "bar":
                p = V(*c) + R @ V(0, -ry * .84 * math.cos(pupil_drop), -rz * math.sin(pupil_drop))
                mesh.box(tuple(p), (rx * pupil_size[1] * 2.4, ry * .3, rz * pupil_size[0]), "black", rotation=_euler(R))
            else:
                p = V(*c) + R @ V(0, -ry * .74, 0)
                mesh.sphere(tuple(p), 1.0, "black", segments=6, rings=4, scale=(rx * pupil_size[0], ry * .35, rz * pupil_size[1]),
                            rotation=_euler(R))

    m.piece("Eyes", build=glow, bone=bone, glow=True)
    if pupil:
        m.piece("Pupils", build=pupils, bone=bone)


# ---------------------------------------------------------------------------
def mimic() -> Model:
    """A wooden treasure chest that bites. Rigid base (Hips) and lid (Lid, hinged at the
    back top edge); the eyes sit inside the lid, so they show only while it is open."""
    m = Model("mimic")
    m.height = .9
    m.gait = "Shuffle"
    m.preview_tint = (122, 77, 36)
    W, D = .47, .31            # half width / half depth of the base
    z0, z1 = .09, .55          # base bottom / rim
    TH = .04                   # board thickness
    m.bone("Hips", (0, 0, .3), (0, 0, z1))
    m.bone("Lid", (0, D, z1), (0, D, z1 + .3), "Hips")
    m.bone("Tongue", (0, .1, .35), (0, -.2, .47), "Hips")

    # lid profile: vertical skirt, then a polygonal barrel vault
    skirt = .06
    vz = z1 + skirt
    rise = .884 - vz
    N = 6

    def arch(t, inset=0.0):
        return V(0, -(D - inset) * math.cos(t), vz + (rise - inset) * math.sin(t))

    ts = [math.pi * i / N for i in range(N + 1)]

    def base(mesh):
        # boards: three per wall, alternating shades
        shades = ("tint", "tint_shade", "tint_light")
        hz = (z1 - z0) / 3
        for i in range(3):
            zc = z0 + hz * (i + .5)
            for side in (-1, 1):
                mesh.box((0, side * (D - TH / 2), zc), (2 * W - .02, TH, hz - .008), shades[(i + (side > 0)) % 3])
                mesh.box((side * (W - TH / 2), 0, zc), (TH, 2 * D - .02, hz - .008), shades[(i + 1 + (side > 0)) % 3])
        # iron: corner posts, straps, rim and foot bands, rivets
        for sx in (-1, 1):
            for sy in (-1, 1):
                mesh.box((sx * (W - .005), sy * (D - .005), (z0 + z1) / 2 - .005), (.075, .075, z1 - z0 + .01), "iron", bevel=.01)
        for sx in (-1, 1):
            for sy in (-1, 1):
                mesh.box((sx * .27, sy * (D + .006), (z0 + z1) / 2), (.07, .014, z1 - z0), "iron")
        for zc, h in ((z1 - .025, .05), (z0 + .03, .06)):
            for sy in (-1, 1):
                mesh.box((0, sy * (D + .007), zc), (2 * W + .01, .016, h), "iron_dark" if zc < .3 else "iron")
            for sx in (-1, 1):
                mesh.box((sx * (W + .007), 0, zc), (.016, 2 * D + .01, h), "iron_dark" if zc < .3 else "iron")
        for sx in (-1, 1):
            for sy in (-1, 1):
                for zc in (z1 - .025, z0 + .03):
                    mesh.cone((sx * .27, sy * (D + .02), zc), .016, .02, "iron_dark", sides=4, rotation=(-90 * sy, 0, 0))
        # hinge knuckles at the back
        for sx in (-1, 1):
            mesh.cylinder((sx * .27, D + .02, z1), .026, .1, "iron_dark", sides=6, rotation=(0, 90, 0))
        # brass lock plate with a keyhole
        mesh.box((0, -D - .012, z1 - .1), (.16, .02, .15), "gold", bevel=.012)
        mesh.box((0, -D - .024, z1 - .115), (.022, .01, .05), "black")
        mesh.cylinder((0, -D - .024, z1 - .085), .018, .012, "black", sides=6, rotation=(90, 0, 0))
        # teeth on the rim: big uneven fangs at the front, smaller ones at the sides and back
        front = [(-.37, .09, 0), (-.25, .14, 6), (-.12, .1, -4), (0, .12, 3), (.12, .1, 5), (.25, .15, -6), (.37, .09, 0)]
        for x, h, lean in front:
            mesh.cone((x, -D + TH + .024, z1 - .03 + h / 2), .036, h, "bone", sides=4, rotation=(-10, lean, 45))
        for y, h in ((-.16, .11), (-.04, .085), (.08, .07), (.19, .06)):
            for sx in (-1, 1):
                mesh.cone((sx * (W - TH - .024), y, z1 - .03 + h / 2), .032, h, "bone", sides=4, rotation=(0, -sx * 10, 45))
        for x in (-.29, -.14, .01, .16, .3):
            mesh.cone((x, D - TH - .022, z1 - .03 + .035), .028, .07, "bone", sides=4, rotation=(8, 0, 45))

    m.piece("Crate", build=base, bone="Hips")

    def lid(mesh):
        # skirt boards
        for side in (-1, 1):
            board = mesh.box((0, side * (D - TH / 2), z1 + skirt / 2), (2 * W, TH, skirt), "tint_shade")
            recolor(mesh, board, "tint_deep", lambda nn, side=side: nn[1] * side < -.9)
        # vault planks
        shades = ("tint", "tint_light", "tint", "tint_shade", "tint", "tint_light")
        for i in range(N):
            p0, p1 = arch(ts[i]), arch(ts[i + 1])
            ydir = p1 - p0
            mid = (p0 + p1) / 2
            R = _axes(V(1, 0, 0), ydir)          # local Z = outward normal of this plank
            n = R[:, 2]
            plank = frame_box(mesh, mid - n * .02, (2 * W, np.linalg.norm(ydir) + .01, .04), R, shades[i])
            recolor(mesh, plank, "tint_deep", lambda nn, n=n: nn @ n < -.9)     # dark underside
            # iron bands over the vault and along its ends
            for x, w, col in ((-.27, .07, "iron"), (.27, .07, "iron"), (-W + .02, .05, "iron_dark"), (W - .02, .05, "iron_dark")):
                frame_box(mesh, mid + n * .006 + V(x, 0, 0), (w, np.linalg.norm(ydir) + .018, .016), R, col)
        # gable ends: convex slabs following the vault
        pts = [arch(t) for t in ts]
        for sx in (-1, 1):
            poly = [V(sx * W, -D, z1)] + [V(sx * W, p[1], p[2]) for p in pts] + [V(sx * W, D, z1)]
            if sx > 0:
                poly = poly[::-1]
            gable = mesh.poly([tuple(p) for p in poly], "tint_shade", thickness=TH)
            recolor(mesh, gable, "tint_deep", lambda nn, sx=sx: nn[0] * sx < -.9)
        # iron lip around the skirt, and straps down the skirt
        for side in (-1, 1):
            mesh.box((0, side * (D + .007), z1 + .015), (2 * W + .01, .016, .03), "iron")
            for sx in (-1, 1):
                mesh.box((sx * .27, side * (D + .007), z1 + skirt / 2), (.07, .016, skirt + .01), "iron")
        # brass hasp hanging over the lock
        mesh.box((0, -D - .018, z1 - .01), (.07, .016, .1), "gold", bevel=.008)
        mesh.box((0, -D - .028, z1 - .045), (.034, .012, .02), "brass_dark")
        # upper fangs along the inner front edge and the ends
        for x, h, lean in ((-.31, .11, -5), (-.19, .08, 4), (-.065, .13, -3), (.065, .13, 3), (.19, .08, -4), (.31, .11, 5)):
            mesh.cone((x, -D + TH + .022, z1 + .03 - h / 2), .034, h, "bone", sides=4, rotation=(180 + 10, lean, 45))
        for y, h in ((-.1, .09), (.02, .075), (.14, .06)):
            for sx in (-1, 1):
                mesh.cone((sx * (W - TH - .022), y, z1 + .03 - h / 2), .03, h, "bone", sides=4, rotation=(180, sx * 10, 45))

    m.piece("Lid_Wood", build=lid, bone="Lid")

    # Two fleshy mounds under the vault carry the eyes. The eyes look forward-down while the
    # lid is shut, so they meet the player's gaze once the lid opens about 65 degrees.
    look = V(0, -math.cos(math.radians(50)), -math.sin(math.radians(50)))
    t_eye = math.radians(108)
    tangent = arch(t_eye + .01) - arch(t_eye - .01)
    Rm = _axes(V(1, 0, 0), tangent)                  # local Z = outward normal of the vault
    inward = -Rm[:, 2]
    eye_r = .08
    mounds, eye_cs = [], []
    for x in (.15, -.15):
        c = arch(t_eye, .11) + V(x, 0, 0)
        mounds.append(ellipsoid(tuple(c), (.115, .1, .055), rotation=_euler(Rm), color="mouth"))
        e = c + inward * .045
        eye_cs.append(e)
        mounds.append(torus(tuple(e - look * .025), eye_r * .95, .024, rotation=(140, 0, 0), k=.02, color="pink_dark"))
    mounds.append(limb(tuple(eye_cs[0] - inward * .03), tuple(eye_cs[1] - inward * .03), .04, color="mouth", k=.03))
    m.piece("Lid_Flesh", mounds, bone="Lid", faces=300, voxel=.008)
    look_eyes(m, eye_cs, eye_r, look, "eye_yellow", pupil="slit", bone="Lid", scale=(1, .8, 1))

    # the mouth: a cavity lined with flesh, gums along the rim and a black gullet at the back
    floor = .38
    mouth = [
        box((0, 0, (z0 + .53) / 2), (2 * W - .02, 2 * D - .02, .53 - z0), bone="Hips", color="mouth"),
        box((0, -.01, floor + .3), (2 * W - 2 * TH - .1, 2 * D - 2 * TH - .1, .6), round=.035, op="sub", k=.015, color="mouth"),
        ellipsoid((0, .085, floor), (.18, .11, .17), op="sub", k=.02, color="black"),
        box((0, 0, .56), (2, 2, .08), op="paint", color="pink_dark"),
    ]
    m.piece("Body", mouth, faces=520, voxel=.01, tau=.05)

    # tongue rising out of the gullet, lolling forward over the floor, tip curled up
    tongue = [
        flat_limb((0, .13, floor - .03), (0, .03, floor + .028), .06, .072, flatten=.28, normal=(0, .5, 1), bone="Tongue", color="tongue"),
        flat_limb((0, .03, floor + .028), (0, -.15, floor + .03), .072, .062, flatten=.28, normal=(0, 0, 1), bone="Tongue", k=.02, color="tongue"),
        flat_limb((0, -.15, floor + .03), (0, -.205, floor + .07), .062, .035, flatten=.28, normal=(0, .6, 1), bone="Tongue", k=.02, color="tongue"),
        limb((0, .04, floor + .05), (0, -.15, floor + .052), .011, .008, op="paint", color="pink_dark"),
    ]
    m.piece("Tongue_Flesh", tongue, bone="Tongue", faces=200, voxel=.007)

    # stubby fleshy legs with pale claws under the corners
    legs = []
    for sy in (-1, 1):
        a = V(W - .1, sy * (D - .09), z0 + .03)
        b = V(W + .005, sy * (D + .01), .04)
        legs.append(limb(tuple(a), tuple(b), .06, .042, color="mouth", mirror=True, k=.01))
        for dx, dy in ((.055, -.01 * sy), (.025, .045 * sy), (.06, .045 * sy)):
            legs.append(limb(tuple(b), tuple(b + V(dx, dy, -.03)), .02, .006, color="bone", mirror=True, k=.008))
    m.piece("Feet", legs, bone="Hips", faces=200, voxel=.008)

    m.socket("Socket.Mouth", "Hips", (0, -.04, .48))
    m.socket("Socket.Hand", "Hips", (0, -.04, .48))
    return m

# ---------------------------------------------------------------------------
def ice_golem() -> Model:
    """Frost Golem: a hunched giant armoured in faceted blocks of ice. The ice is rigid on
    the bones; a dark skinned body underneath shows at the joints. The face sits behind
    the ice visor that gameplay adds at height * .855, width * .42 forward."""
    m = Model("ice_golem")
    m.height = 2.4
    m.gait = "Heavy"
    m.preview_tint = (64, 166, 242)
    J = dict(hip=V(0, .08, .9), chest=V(0, .06, 1.42), neck=V(0, -.12, 1.82), head_top=V(0, -.26, 2.3),
             shoulder=V(.56, .12, 1.8), elbow=V(.76, .18, 1.33), wrist=V(.8, -.04, .9), hand_end=V(.8, -.1, .58),
             hip_joint=V(.28, .08, .84), knee=V(.33, -.06, .48), ankle=V(.34, .06, .17), toe=V(.34, -.3, .05))
    humanoid_bones(m, **{k: tuple(v) for k, v in J.items()})
    sh, el, wr, he = J["shoulder"], J["elbow"], J["wrist"], J["hand_end"]
    hj, kn, an = J["hip_joint"], J["knee"], J["ankle"]
    fist_c = (wr + he) / 2 + V(0, -.02, -.03)

    def mirror(p):
        return V(-p[0], p[1], p[2])

    # dark body under the armour (skinned): it fills the gaps between the blocks
    core = [
        ellipsoid((0, .08, .92), (.32, .22, .17), bone="Hips", color="tint_deep"),
        ellipsoid((0, .06, 1.16), (.3, .21, .16), bone="Spine", k=.06, color="tint_deep"),
        ellipsoid((0, .07, 1.6), (.46, .3, .32), bone="Chest", k=.06, color="tint_deep"),
        limb((0, -.03, 1.74), (0, -.16, 1.96), .15, .13, bone="Neck", k=.05, color="tint_deep"),
        ellipsoid((0, -.23, 2.05), (.21, .17, .16), bone="Head", k=.04, color="tint_deep"),
        limb(tuple(sh), tuple(el), .15, .13, bone="UpperArm.L", k=.05, mirror=True, color="tint_deep"),
        limb(tuple(el), tuple(wr), .13, .14, bone="Forearm.L", k=.04, mirror=True, color="tint_deep"),
        ellipsoid(tuple(fist_c), (.15, .15, .15), bone="Hand.L", k=.04, mirror=True, color="tint_deep"),
        limb(tuple(hj), tuple(kn), .17, .15, bone="Thigh.L", k=.05, mirror=True, color="tint_deep"),
        limb(tuple(kn), tuple(an), .15, .14, bone="Shin.L", k=.04, mirror=True, color="tint_deep"),
        ellipsoid((.34, -.08, .1), (.15, .22, .09), bone="Foot.L", k=.04, mirror=True, color="tint_deep"),
    ]
    m.piece("Body", core, faces=700, voxel=.022, tau=.06)

    frost = lambda n: n[2] > .7             # noqa: E731  (upward faces catch frost)

    def ice(mesh, c, size, color="tint", rotation=(0, 0, 0), seed=0, taper=(1, 1), jitter=.14, extra=2, frosted=True):
        faces = chunk(mesh, c, size, color, rotation=rotation, seed=seed, jitter=jitter, taper=taper, extra=extra)
        if frosted:
            recolor(mesh, faces, "ice_light", frost)
        return faces

    def seg(mesh, a, b, w, d, color="tint", seed=0, taper=(1, 1), pad=0.0, x_hint=(1, 0, 0), frosted=True):
        a, b = V(*a), V(*b)
        L = float(np.linalg.norm(b - a)) + 2 * pad
        return ice(mesh, (a + b) / 2, (w, d, L), color, rotation=_frame(b - a, x_hint), seed=seed, taper=taper, frosted=frosted)

    def shard(mesh, base, direction, length, r, color="ice_light", tip="white", spin=0.0):
        d = V(*direction)
        d /= np.linalg.norm(d)
        crystal(mesh, tuple(V(*base)), tuple(V(*base) + d * length), r, color, sides=5, tip_color=tip, spin=spin)

    # --- torso ------------------------------------------------------------
    def pelvis(mesh):
        ice(mesh, (0, .08, .88), (.78, .52, .32), seed=1, taper=(1.05, 1.02))
        ice(mesh, (0, -.2, .8), (.38, .12, .24), "tint_shade", rotation=(10, 0, 0), seed=2)     # front plate

    def belly(mesh):
        ice(mesh, (0, .06, 1.14), (.68, .5, .28), "tint_shade", rotation=(4, 0, 0), seed=3)
        ice(mesh, (-.16, -.19, 1.15), (.28, .12, .22), rotation=(4, 0, 4), seed=4)
        ice(mesh, (.17, -.19, 1.13), (.27, .12, .21), rotation=(4, 0, -3), seed=5)

    def chest(mesh):
        ice(mesh, (0, .07, 1.62), (.86, .72, .66), rotation=(10, 0, 0), taper=(1.28, 1.04), seed=6, jitter=.1)
        ice(mesh, (-.25, -.33, 1.7), (.42, .18, .38), "tint_light", rotation=(4, 0, 9), seed=7)        # pectoral slabs
        ice(mesh, (.24, -.33, 1.67), (.4, .18, .36), "tint_light", rotation=(2, 0, -6), seed=8)
        # hunched shoulders: a hump of ice behind the head
        ice(mesh, (0, .2, 1.98), (.76, .48, .32), "tint_shade", rotation=(24, 0, 0), seed=40, taper=(.8, .8))
        # the freezing sac on the back grows a ridge of crystals
        ice(mesh, (0, .43, 1.62), (.56, .18, .42), "tint_shade", rotation=(14, 0, 0), seed=9)
        for x, y, z, h, lean in ((0, .3, 2.07, .4, 0), (-.2, .28, 2.0, .3, -24), (.2, .28, 2.0, .32, 20),
                                 (-.12, .5, 1.7, .22, -14), (.13, .5, 1.68, .2, 16), (0, .52, 1.52, .18, 0)):
            shard(mesh, (x, y, z), (math.sin(math.radians(lean)), .75, .65), h, .07, spin=x * 200)

    def collar(mesh):
        ice(mesh, (0, -.06, 1.86), (.52, .44, .14), "tint_shade", seed=10)

    # --- head (face behind the visor volume) ------------------------------
    def head(mesh):
        ice(mesh, (0, -.22, 2.07), (.5, .42, .42), taper=(.88, .9), seed=11, jitter=.1)
        for x, sgn in ((-.15, 1), (.15, -1)):        # heavy brow in a scowling V
            ice(mesh, (x, -.4, 2.14), (.26, .14, .1), "tint_shade", rotation=(-12, sgn * 4, sgn * 16), seed=12 + (x > 0), frosted=True)
        ice(mesh, (0, -.32, 1.9), (.48, .28, .14), "tint_shade", rotation=(6, 0, 0), seed=14)        # jaw
        mesh.box((0, -.43, 1.97), (.3, .03, .03), "tint_deep")                                         # mouth slit
        for x in (-.14, .14):
            mesh.box((x, -.428, 2.05), (.17, .04, .08), "tint_deep", rotation=(0, 0, -14 if x > 0 else 14))   # eye sockets
            shard(mesh, (x * 1.15, -.43, 1.94), (x * .4, -.2, 1), .09, .024, "white", "white")             # tusks
        # a crest of crystals swept back over the head
        for x, y, h, lx, ly in ((0, -.22, .2, 0, .25), (-.12, -.22, .15, -.45, .3), (.12, -.22, .16, .45, .3), (-.2, -.12, .11, -.9, .4),
                                (.2, -.12, .12, .9, .4), (0, -.08, .15, 0, .9)):
            shard(mesh, (x, y, 2.22), (lx * .6, ly * .6, 1), h * .88, .045 + h * .14, spin=x * 100)

    # --- arms ----------------------------------------------------------------
    def upper_arm(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            ice(mesh, S(V(.62, .12, 1.9)), (.52, .5, .36), rotation=(0, s * 16, 0), taper=(.8, .86), seed=20)   # pauldron
            seg(mesh, S(sh + V(.0, 0, -.14)), S(el + V(0, 0, .07)), .32, .34, "tint_shade", seed=21)
            for dx, dy, h, lean in ((.04, .02, .34, 20), (-.08, .16, .25, 8), (-.06, -.16, .22, 4), (.18, .06, .18, 42)):
                base = S(V(.62 + dx, .12 + dy * .85, 2.04))
                shard(mesh, base, S(V(math.sin(math.radians(lean)), dy * .5, math.cos(math.radians(lean)))), h, .065, spin=dx * 300)
        return build

    def forearm(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            seg(mesh, S(el + V(0, 0, -.07)), S(wr + V(0, 0, .02)), .36, .38, seed=22, taper=(1.18, 1.12))
            ice(mesh, S((el + wr) / 2 + V(.17, .03, .03)), (.1, .26, .3), "tint_light", rotation=(0, s * 6, 0), seed=23)
            for dz, h in ((.1, .18), (-.08, .14)):        # shards along the outer edge
                shard(mesh, S((el + wr) / 2 + V(.18, .1, dz)), S(V(.45, .7, .4)), h, .045, spin=dz * 500)
        return build

    def fist(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            ice(mesh, S(fist_c), (.44, .46, .38), seed=24, jitter=.1)
            for i, dy in enumerate((-.16, -.055, .055, .16)):    # knuckles along the bottom front
                ice(mesh, S(fist_c + V(.0, dy - .05, -.2)), (.38, .1, .1), "tint_light", seed=25 + i, jitter=.2, extra=0, frosted=False)
            ice(mesh, S(fist_c + V(-.22, -.14, .05)), (.14, .17, .22), "tint_shade", rotation=(0, 0, s * 10), seed=29)  # thumb
        return build

    # --- legs ----------------------------------------------------------------
    def thigh(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            seg(mesh, S(hj + V(.02, 0, -.08)), S(kn + V(0, 0, .07)), .42, .44, "tint_shade", seed=30)
        return build

    def shin(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            seg(mesh, S(kn + V(0, 0, -.06)), S(an + V(0, 0, .05)), .38, .4, seed=31, taper=(1.15, 1.1))
            ice(mesh, S(kn + V(.0, -.21, -.02)), (.3, .12, .24), "tint_light", rotation=(-10, 0, 0), seed=32)   # knee cap
        return build

    def foot(side):
        def build(mesh):
            s = 1 if side == "L" else -1
            S = lambda p: p if s > 0 else mirror(p)          # noqa: E731
            ice(mesh, S(V(.34, -.07, .11)), (.46, .6, .22), taper=(.9, .82), seed=33)
            for i, dx in enumerate((-.14, 0, .14)):
                ice(mesh, S(V(.34 + dx, -.38, .065)), (.13, .15, .13), "tint_shade", seed=34 + i, extra=0)
        return build

    m.piece("Ice_Pelvis", build=pelvis, bone="Hips")
    m.piece("Ice_Belly", build=belly, bone="Spine")
    m.piece("Ice_Chest", build=chest, bone="Chest")
    m.piece("Ice_Collar", build=collar, bone="Neck")
    m.piece("Ice_Head", build=head, bone="Head")
    for side in ("L", "R"):
        m.piece("Ice_UpperArm." + side, build=upper_arm(side), bone="UpperArm." + side)
        m.piece("Ice_Forearm." + side, build=forearm(side), bone="Forearm." + side)
        m.piece("Ice_Fist." + side, build=fist(side), bone="Hand." + side)
        m.piece("Ice_Thigh." + side, build=thigh(side), bone="Thigh." + side)
        m.piece("Ice_Shin." + side, build=shin(side), bone="Shin." + side)
        m.piece("Ice_Foot." + side, build=foot(side), bone="Foot." + side)
    eyes(m, (.14, -.448, 2.05), (.066, .024, .032), "eye_cyan", slit=True)
    m.socket("Socket.Hand", "Hand.R", tuple(mirror(fist_c + V(-.05, -.24, 0))))
    return m


# ---------------------------------------------------------------------------
def bomb_toad() -> Model:
    """Bomb Toad: a squat warty toad the size of a water barrel, dozing with half-closed
    eyes. Its throat sac is a glowing balloon of marsh gas on bone Sac (scaled about its
    head, at the sac's centre)."""
    m = Model("bomb_toad")
    m.height = .85
    m.gait = "Hopper"
    m.preview_tint = (92, 140, 56)
    sac_c = V(0, -.34, .2)
    m.bone("Hips", (0, .2, .3), (0, -.06, .42))
    m.bone("Head", (0, -.06, .45), (0, -.3, .7), "Hips")
    m.bone("Jaw", (0, -.04, .37), (0, -.42, .33), "Head")
    m.bone("Sac", tuple(sac_c), tuple(sac_c + V(0, 0, .12)), "Jaw")
    # folded hind legs: thigh forward to the knee, shin back to the ankle, foot forward again
    hip, knee, ankle, toe = V(.25, .22, .31), V(.38, .0, .19), V(.35, .3, .075), V(.42, .06, .03)
    m.bone("Thigh.L", tuple(hip), tuple(knee), "Hips", mirror=True)
    m.bone("Shin.L", tuple(knee), tuple(ankle), "Thigh.L", mirror=True)
    m.bone("Foot.L", tuple(ankle), tuple(toe), "Shin.L", mirror=True)
    sh, el, wr, fi = V(.24, -.19, .3), V(.33, -.24, .17), V(.32, -.34, .055), V(.34, -.45, .02)
    m.bone("UpperArm.L", tuple(sh), tuple(el), "Hips", mirror=True)
    m.bone("Forearm.L", tuple(el), tuple(wr), "UpperArm.L", mirror=True)
    m.bone("Hand.L", tuple(wr), tuple(fi), "Forearm.L", mirror=True)

    eye = V(.19, -.27, .735)
    eye_r = .095
    body = [
        # trunk and head
        ellipsoid((0, .08, .36), (.42, .4, .3), bone="Hips"),
        ellipsoid((0, .23, .3), (.36, .25, .25), bone="Hips", k=.1),
        ellipsoid((0, -.2, .47), (.37, .26, .19), bone="Head", k=.14),
        ellipsoid((0, -.36, .455), (.27, .12, .11), bone="Head", k=.07),
        ellipsoid((0, -.25, .345), (.34, .24, .1), bone="Jaw", k=.05),
        # eye turrets and heavy, sleepy upper lids
        ellipsoid(tuple(eye + V(-.01, .05, -.07)), (.1, .1, .08), bone="Head", k=.06, mirror=True),
        ellipsoid(tuple(eye + V(0, .014, .02)), (.104, .104, .09), rotation=(-16, 0, 0), bone="Head", k=.02, mirror=True),
        # glands behind the eyes
        ellipsoid((.24, -.05, .59), (.075, .12, .055), rotation=(15, 0, -20), bone="Head", k=.05, mirror=True, color="tint_shade"),
        # hind legs
        limb(tuple(hip), tuple(knee), .16, .1, bone="Thigh.L", k=.07, mirror=True, tau=.03),
        limb(tuple(knee + V(0, 0, -.02)), tuple(ankle), .08, .055, bone="Shin.L", k=.03, mirror=True, tau=.025),
        ellipsoid(tuple(ankle + V(.03, -.09, -.04)), (.065, .11, .032), rotation=(0, 0, -15), bone="Foot.L", k=.03, mirror=True, tau=.025),
        # front legs
        limb(tuple(sh), tuple(el), .075, .06, bone="UpperArm.L", k=.05, mirror=True, tau=.03),
        limb(tuple(el), tuple(wr), .06, .05, bone="Forearm.L", k=.03, mirror=True, tau=.025),
        ellipsoid(tuple(wr + V(.01, -.04, -.025)), (.058, .058, .03), bone="Hand.L", k=.025, mirror=True, tau=.025),
    ]
    # webbed toes and fingers with round tips
    foot_c = ankle + V(.06, -.2, -.045)
    for d in (V(-.07, -.11, -.01), V(-.005, -.13, -.01), V(.055, -.105, -.01), V(.09, -.045, -.01)):
        body.append(limb(tuple(foot_c), tuple(foot_c + d), .026, .02, bone="Foot.L", k=.015, mirror=True, tau=.02))
        body.append(sphere(tuple(foot_c + d), .025, bone="Foot.L", k=.01, mirror=True, color="toad_belly", tau=.02))
    hand = wr + V(.01, -.04, -.03)
    for d in (V(-.065, -.055, -.008), V(-.02, -.085, -.008), V(.03, -.08, -.008), V(.068, -.04, -.008)):
        body.append(limb(tuple(hand), tuple(hand + d), .021, .016, bone="Hand.L", k=.012, mirror=True, tau=.02))
        body.append(sphere(tuple(hand + d), .02, bone="Hand.L", k=.008, mirror=True, color="toad_belly", tau=.02))
    # warts: a few soft bumps over the back and head, some of them yellow
    warts = ((.12, .3, .64, .04, "toad_spot"), (.3, .16, .56, .035, "tint_shade"), (.22, .42, .5, .038, "toad_spot"),
             (.36, .32, .38, .032, "tint_shade"), (.08, .05, .67, .03, "tint_shade"), (.3, -.18, .55, .03, "toad_spot"),
             (.4, .02, .44, .035, "tint_shade"), (.14, .5, .36, .03, "tint_shade"), (0, .2, .67, .036, "toad_spot"))
    for x, y, z, r, col in warts:
        body.append(sphere((x, y, z), r, bone="Head" if y < -.12 else "Hips", k=.025, mirror=x > 0, color=col, bias=.004))
    body += [
        # mouth: a wide grumpy slit with down-turned corners; the jaw hinges at them
        ellipsoid((.1, -.19, .375), (.27, .29, .016), rotation=(-4, 9, 0), op="sub", k=.008, color="mouth", mirror=True),
        sphere((.06, -.475, .47), .013, op="sub", color="black", mirror=True),          # nostrils
        # colour: pale belly and throat, dark mottling on the back
        ellipsoid((0, -.12, .12), (.34, .34, .16), op="paint", color="toad_belly"),
        ellipsoid((0, -.27, .27), (.27, .2, .07), op="paint", color="toad_belly"),
        ellipsoid((.18, .2, .62), (.1, .14, .06), op="paint", color="tint_shade", mirror=True),
        ellipsoid((0, .36, .5), (.12, .12, .1), op="paint", color="tint_shade"),
        ellipsoid((.3, .3, .42), (.08, .1, .1), op="paint", color="tint_shade", mirror=True),
    ]
    m.piece("Body", body, faces=3000, voxel=.0085, tau=.04)

    # big bulging eyes looking out from under the lids
    look = [V(.2, -.97, .1), V(-.2, -.97, .1)]
    look_eyes(m, [eye, mirror_x(eye)], eye_r, look, "eye_yellow", pupil="bar", bone="Head", pupil_size=(.3, .36), pupil_drop=.3)

    # the glowing gas sac: brighter "liquid" light sloshing in the lower half
    def sac(mesh):
        faces = mesh.sphere(tuple(sac_c), .135, "glass_green", segments=10, rings=7, scale=(1.05, .9, .92))
        recolor(mesh, faces, "acid", lambda n: n[2] < -.25)
        mesh.ico(tuple(sac_c + V(.05, -.07, .03)), .022, "white", subdiv=1)
    m.piece("Sac", build=sac, bone="Sac", glow=True)
    m.socket("Socket.Mouth", "Head", (0, -.5, .38))
    return m


# ---------------------------------------------------------------------------
def mouse() -> Model:
    """Cheese Mouse: a gray rat bigger than a cat, with big round ears, beady glowing eyes,
    oversized yellow incisors, long whiskers and a long pink segmented tail."""
    m = Model("mouse")
    m.height = .6
    m.gait = "Quadruped"
    m.preview_tint = (140, 140, 148)
    m.bone("Hips", (0, .3, .27), (0, .05, .28))
    m.bone("Spine", (0, .05, .28), (0, -.14, .3), "Hips")
    m.bone("Neck", (0, -.14, .3), (0, -.22, .35), "Spine")
    m.bone("Head", (0, -.22, .35), (0, -.52, .32), "Neck")
    ear_a, ear_b = V(.075, -.25, .46), V(.125, -.235, .525)
    m.bone("Ear.L", tuple(ear_a), tuple(ear_b), "Head", mirror=True)
    sh, el, wr, he = V(.085, -.1, .22), V(.1, -.07, .12), V(.1, -.12, .035), V(.1, -.18, .01)
    m.bone("UpperArm.L", tuple(sh), tuple(el), "Spine", mirror=True)
    m.bone("Forearm.L", tuple(el), tuple(wr), "UpperArm.L", mirror=True)
    m.bone("Hand.L", tuple(wr), tuple(he), "Forearm.L", mirror=True)
    hj, kn, an, to = V(.12, .24, .24), V(.15, .13, .13), V(.14, .3, .04), V(.14, .12, .01)
    m.bone("Thigh.L", tuple(hj), tuple(kn), "Hips", mirror=True)
    m.bone("Shin.L", tuple(kn), tuple(an), "Thigh.L", mirror=True)
    m.bone("Foot.L", tuple(an), tuple(to), "Shin.L", mirror=True)
    tail = [V(0, .4, .22), V(.015, .58, .12), V(.06, .77, .05), V(.05, .97, .03), V(-.04, 1.15, .035)]
    for i in range(4):
        m.bone("Tail%d" % (i + 1), tuple(tail[i]), tuple(tail[i + 1]), "Hips" if i == 0 else "Tail%d" % i)

    body = [
        # rump, belly and shoulders: a hunched pear shape
        ellipsoid((0, .2, .27), (.17, .21, .17), bone="Hips"),
        ellipsoid((0, -.02, .26), (.14, .19, .14), bone="Spine", k=.08),
        limb((0, -.1, .28), (0, -.22, .35), .105, .088, bone="Neck", k=.05),
        # head: skull, cheeks and a long tapering snout
        ellipsoid((0, -.28, .385), (.1, .12, .092), bone="Head", k=.05),
        ellipsoid((.055, -.34, .345), (.06, .07, .055), bone="Head", k=.04, mirror=True),
        limb((0, -.33, .36), (0, -.5, .315), .072, .03, bone="Head", k=.04),
        sphere((0, -.525, .318), .024, bone="Head", k=.01, color="pink", bias=.006),
        # ears
        flat_limb(tuple(ear_a + V(0, 0, -.02)), tuple(ear_b), .07, .084, flatten=.2, normal=(.35, -1, .15), bone="Ear.L", k=.02,
                  mirror=True, tau=.02),
        flat_limb(tuple(ear_a + V(.004, -.012, 0)), tuple(ear_b + V(0, -.012, -.004)), .052, .064, flatten=.2, normal=(.35, -1, .15),
                  op="paint", color="pink", mirror=True),
        # front legs with pink paws
        limb(tuple(sh), tuple(el), .056, .04, bone="UpperArm.L", k=.04, mirror=True, tau=.025),
        limb(tuple(el), tuple(wr), .038, .03, bone="Forearm.L", k=.02, mirror=True, tau=.02),
        ellipsoid(tuple(wr + V(0, -.03, -.015)), (.03, .04, .016), bone="Hand.L", k=.015, mirror=True, color="pink", tau=.02),
        # hind legs: big haunches, long pink feet
        limb(tuple(hj), tuple(kn), .09, .05, bone="Thigh.L", k=.06, mirror=True, tau=.03),
        limb(tuple(kn), tuple(an), .042, .028, bone="Shin.L", k=.025, mirror=True, tau=.02),
        limb(tuple(an + V(0, 0, -.015)), tuple(to + V(0, 0, .005)), .028, .022, bone="Foot.L", k=.015, mirror=True, color="pink", tau=.02),
        # mouth and belly colours
        ellipsoid((0, -.47, .285), (.035, .03, .008), op="sub", k=.004, color="mouth"),
        ellipsoid((0, -.05, .14), (.12, .26, .08), op="paint", color="tint_light"),
        ellipsoid((0, -.33, .3), (.07, .12, .04), op="paint", color="tint_light"),
    ]
    for y, h in ((-.12, .035), (-.05, .045), (.03, .05), (.11, .05), (.19, .045), (.27, .035)):   # mangy bristles
        z = .26 + .14 * math.sqrt(max(0, 1 - ((y + .02) / .19) ** 2)) if y < .08 else .27 + .17 * math.sqrt(max(0, 1 - ((y - .2) / .21) ** 2))
        body.append(flat_limb((0, y - .02, z - .015), (0, y + .03, z + h * .8), .024, .004, flatten=.45, normal=(1, 0, 0),
                              bone="Spine" if y < .05 else "Hips", k=.012, color="tint_shade"))
    for side in (1,):   # toes on paws and feet
        for d in (V(-.02, -.03, 0), V(0, -.035, 0), V(.02, -.03, 0)):
            c = wr + V(0, -.055, -.022) + d * .7
            body.append(sphere(tuple(c), .011, bone="Hand.L", k=.006, mirror=True, color="pink", tau=.02))
            c = to + V(0, -.01, .006) + d * .8
            body.append(sphere(tuple(c), .012, bone="Foot.L", k=.006, mirror=True, color="pink", tau=.02))
    m.piece("Body", body, faces=2000, voxel=.006, tau=.035)

    # long scaly tail, ringed, curving along the ground
    def tail_at(u):              # Catmull-Rom through the tail joints, u in [0, 4]
        j = min(int(u), 3)
        f = u - j
        p0, p1, p2 = tail[max(j - 1, 0)], tail[j], tail[j + 1]
        p3 = tail[min(j + 2, 4)]
        return .5 * (2 * p1 + (-p0 + p2) * f + (2 * p0 - 5 * p1 + 4 * p2 - p3) * f * f + (-p0 + 3 * p1 - 3 * p2 + p3) * f ** 3)

    tail_prims = []
    n = 12
    for i in range(n):
        u0, u1 = 4 * i / n, 4 * (i + 1) / n
        r0, r1 = .03 * (1 - u0 / 4.6), .03 * (1 - u1 / 4.6)
        tail_prims.append(limb(tuple(tail_at(u0)), tuple(tail_at(u1)), r0, r1, bone="Tail%d" % (min(int(u0), 3) + 1), k=.006,
                               color="pink", tau=.03))
    for i in range(1, 22):
        u = i / 22 * 4
        c, d = tail_at(u), tail_at(min(u + .05, 4)) - tail_at(max(u - .05, 0))
        tail_prims.append(cylinder(tuple(c), .045, .006, rotation=_euler(_frame(d)), op="paint", color="pink_dark"))
    m.piece("Tail", tail_prims, faces=300, voxel=.005, tau=.03, symmetric=False)

    # beady glowing eyes
    eye = V(.073, -.355, .405)
    look_eyes(m, [eye, mirror_x(eye)], .036, [V(.5, -.85, .15), V(-.5, -.85, .15)], "eye_red", pupil="round", bone="Head",
              pupil_size=(.42, .5))

    # oversized yellow incisors and long whiskers
    def teeth(mesh):
        for x in (-.012, .012):
            mesh.box((x * 1.1, -.507, .266), (.022, .013, .075), "beak", rotation=(-8, 0, 0), taper=(1, .7))
            mesh.box((x * .8, -.49, .258), (.016, .011, .034), "beak", rotation=(12, 0, 0))
    m.piece("Teeth", build=teeth, bone="Head")

    def whiskers(mesh):
        for side in (-1, 1):
            for dz, dy, length in ((.012, -.02, .21), (-.004, .0, .23), (-.02, .02, .2)):
                a = V(side * .035, -.465, .315 + dz)
                b = a + V(side * length, dy - .05, dz * 3.5)
                seg_box(mesh, a, b, .0035, .0035, "white")
    m.piece("Whiskers", build=whiskers, bone="Head")

    def ear_tag(mesh):           # "a thin punched tag in one ear"
        mesh.box((.152, -.262, .418), (.026, .006, .032), "steel", rotation=(0, -20, 0))
        mesh.cylinder((.14, -.262, .438), .006, .008, "iron_dark", sides=6, rotation=(90, 0, 0))
    m.piece("Ear_Tag", build=ear_tag, bone="Ear.L")

    m.socket("Socket.Mouth", "Head", (0, -.55, .26))
    m.socket("Socket.Hand", "Head", (0, -.55, .26))
    return m


RECIPES = {
    "ice_golem": ice_golem,
    "mimic": mimic,
    "bomb_toad": bomb_toad,
    "mouse": mouse,
}
