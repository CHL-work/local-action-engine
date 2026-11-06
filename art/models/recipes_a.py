"""Monster recipes (set A): Ember Imp, Ogre and Gargoyle.

Same conventions as creatures.py: metres, standing on z = 0, facing -Y, the
creature's own left is +X. Heights follow SpeciesCatalog.cs.
"""
import math

import numpy as np

from bwkit import Model, V, box, cylinder, ellipsoid, flat_limb, limb, sphere, torus
from creatures import humanoid_bones, eyes


# ---------------------------------------------------------------------------
# helpers
def aim(d):
    """Euler XYZ (degrees) that turns local +Z toward direction d (for Mesher cones)."""
    d = np.asarray(d, float)
    d = d / np.linalg.norm(d)
    a = -math.degrees(math.asin(max(-1.0, min(1.0, d[1]))))
    b = math.degrees(math.atan2(d[0], d[2]))
    return (a, b, 0)


def spike(mesh, base, direction, length, radius, color, sides=4):
    """A cone from `base` pointing along `direction` (claws, teeth, spikes)."""
    d = np.asarray(direction, float)
    d = d / np.linalg.norm(d)
    c = np.asarray(base, float) + d * length * .5
    mesh.cone(tuple(c), radius, length, color, sides=sides, rotation=aim(d))


def catmull(points, samples):
    """Points along a Catmull-Rom curve through `points` (inclusive of the ends)."""
    P = [np.asarray(p, float) for p in points]
    P = [2 * P[0] - P[1]] + P + [2 * P[-1] - P[-2]]
    out = []
    segs = len(P) - 3
    for i in range(segs):
        p0, p1, p2, p3 = P[i:i + 4]
        for j in range(samples):
            t = j / samples
            t2, t3 = t * t, t * t * t
            out.append(.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3))
    out.append(P[-2])
    return out


def _triangulate(poly):
    """Ear clipping for a simple 2D polygon; returns index triples."""
    pts = [np.asarray(p, float) for p in poly]
    area = sum(pts[i][0] * pts[(i + 1) % len(pts)][1] - pts[(i + 1) % len(pts)][0] * pts[i][1] for i in range(len(pts)))
    idx = list(range(len(pts))) if area > 0 else list(range(len(pts)))[::-1]
    tris = []

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    guard = 0
    while len(idx) > 3 and guard < 1000:
        guard += 1
        for i in range(len(idx)):
            a, b, c = idx[i - 1], idx[i], idx[(i + 1) % len(idx)]
            if cross(pts[a], pts[b], pts[c]) <= 1e-12:
                continue
            if any(cross(pts[a], pts[b], pts[p]) >= 0 and cross(pts[b], pts[c], pts[p]) >= 0 and cross(pts[c], pts[a], pts[p]) >= 0
                   for p in idx if p not in (a, b, c)):
                continue
            tris.append((a, b, c))
            idx.pop(i)
            break
    tris.append(tuple(idx))
    return tris


def slab(mesh, outline, origin, u, v, thickness, color):
    """A flat plate: the 2D `outline` (u, v coordinates, may be concave) in the plane
    origin + u*s + v*t, extruded `thickness` along the plane normal."""
    o, u, v = (np.asarray(x, float) for x in (origin, u, v))
    n = np.cross(u, v)
    n /= np.linalg.norm(n)
    h = thickness * .5
    bm = mesh.bm
    top = [bm.verts.new(tuple(o + u * s + v * t + n * h)) for s, t in outline]
    bot = [bm.verts.new(tuple(o + u * s + v * t - n * h)) for s, t in outline]
    faces = []
    for a, b, c in _triangulate(outline):
        faces.append(bm.faces.new((top[a], top[b], top[c])))
        faces.append(bm.faces.new((bot[c], bot[b], bot[a])))
    for i in range(len(outline)):
        j = (i + 1) % len(outline)
        faces.append(bm.faces.new((top[i], bot[i], bot[j], top[j])))
    mesh._tag(faces, color)


def rod(mesh, a, b, radius, color, sides=4, radius_b=None):
    """A thin (tapered) cylinder from a to b."""
    a, b = np.asarray(a, float), np.asarray(b, float)
    d = b - a
    mesh.cylinder(tuple((a + b) / 2), radius, float(np.linalg.norm(d)), color, sides=sides, rotation=aim(d),
                  radius_top=radius if radius_b is None else radius_b)


def tube(points, r0, r1, bones, k=.015, color=None, tau=None, mirror=False):
    """Tapered chain of capsules along `points`; segment i goes to bones[f(i)] by arc length."""
    pts = [np.asarray(p, float) for p in points]
    seg = [np.linalg.norm(pts[i + 1] - pts[i]) for i in range(len(pts) - 1)]
    total = sum(seg)
    prims, acc = [], 0.0
    for i, L in enumerate(seg):
        u0, u1 = acc / total, (acc + L) / total
        mid = (u0 + u1) / 2
        bone = bones[min(len(bones) - 1, int(mid * len(bones)))]
        ra = r0 + (r1 - r0) * u0
        rb = r0 + (r1 - r0) * u1
        prims.append(limb(tuple(pts[i]), tuple(pts[i + 1]), ra, rb, bone=bone, k=k, color=color, tau=tau, mirror=mirror))
        acc += L
    return prims


# ---------------------------------------------------------------------------
def bat_wing(side, root, yaw=35, lean=.25, size=1.0, thickness=.014, membrane="tint_deep", spar="tint",
             tip=(.42, .24), fingers=((.38, .02), (.24, -.08), (.07, -.12)), wrist=(.2, .17), depth=.28,
             claw="claw", spar_radius=.02):
    """Mesher builder for a scalloped bat wing in a plane leaning back from the shoulder blade.
    `root` is the left-side root; side = 1 (left, +X) or -1 (right)."""
    def frame():
        a = math.radians(yaw)
        u = np.array([side * math.cos(a), math.sin(a), 0.0])
        v = np.array([0.0, lean, 1.0])
        v -= u * (u @ v)
        v /= np.linalg.norm(v)
        return np.array([side * root[0], root[1], root[2]]), u, v

    def build(mesh):
        o, u, v = frame()
        W = np.array(wrist) * size
        T = np.array(tip) * size
        F = [np.array(f) * size for f in fingers]

        def scallop(P, Q, n=2):
            mid = (P + Q) / 2
            C = mid + (W - mid) * depth * 2
            return [(1 - t) ** 2 * P + 2 * (1 - t) * t * C + t * t * Q for t in [(i + 1) / (n + 1) for i in range(n)]]

        outline = [np.zeros(2), W, T]
        prev = T
        for f in F:
            outline += scallop(prev, f)
            outline.append(f)
            prev = f
        slab(mesh, [tuple(p) for p in outline], o, u, v, thickness, membrane)
        P = lambda q: tuple(o + u * q[0] + v * q[1])
        r = spar_radius * size
        rod(mesh, P((-.02, -.01)), P(W), r, spar, sides=5, radius_b=r * .8)
        rod(mesh, P(W), P(T), r * .8, spar, sides=4, radius_b=r * .25)
        for f in F[:-1]:
            rod(mesh, P(W), P(f), r * .6, spar, sides=4, radius_b=r * .2)
        spike(mesh, P(W), u * .2 + v, .05 * size, .014 * size, claw)   # thumb claw at the wrist
    build.frame = frame
    return build


def imp() -> Model:
    """Ember Imp: a wiry red devil with a glowing furnace belly and a stove-mouth grin."""
    m = Model("imp")
    m.height = 1.8
    m.gait = "Biped"
    m.preview_tint = (217, 64, 46)
    humanoid_bones(m, hip=(0, .03, .84), chest=(0, .0, 1.1), neck=(0, -.03, 1.24), head_top=(0, -.07, 1.66),
                   shoulder=(.17, .02, 1.17), elbow=(.29, .05, .93), wrist=(.35, .0, .7), hand_end=(.37, -.04, .54),
                   hip_joint=(.1, .03, .8), knee=(.13, -.055, .46), ankle=(.13, .03, .11), toe=(.13, -.19, .03))
    wing_root = (.05, .08, 1.13)
    wing = bat_wing(1, wing_root)
    o, u, v = wing.frame()
    m.bone("Wing.L", tuple(o), tuple(o + u * .2 + v * .17), "Chest", mirror=True)
    tail_pts = [(0, .08, .92), (0, .3, .82), (0, .52, .76), (0, .72, .8), (0, .86, .93), (0, .92, 1.04)]
    m.bone("Tail1", tail_pts[0], tail_pts[2], "Hips")
    m.bone("Tail2", tail_pts[2], tail_pts[4], "Tail1")
    m.bone("Tail3", tail_pts[4], (0, .96, 1.16), "Tail2")

    k = .04
    flat_tau = .012   # flat_limb distances are underestimated away from the blade; keep their weights local
    body = [
        # torso: narrow chest, round furnace belly
        ellipsoid((0, .03, .84), (.13, .1, .09), bone="Hips"),
        ellipsoid((0, -.03, .94), (.145, .14, .13), bone="Spine", k=k),
        ellipsoid((0, .01, 1.1), (.16, .1, .1), bone="Chest", k=k),
        ellipsoid((0, .03, 1.16), (.19, .085, .055), bone="Chest", k=.03),
        limb((0, .0, 1.17), (0, -.04, 1.32), .045, .04, bone="Neck", k=.03),
        # head: big cranium, wide grinning jaw, pointed chin
        ellipsoid((0, -.04, 1.5), (.17, .16, .15), bone="Head", k=.04),
        ellipsoid((0, .02, 1.47), (.15, .13, .13), bone="Head", k=.04),
        ellipsoid((0, -.09, 1.36), (.16, .12, .09), bone="Head", k=.04),
        limb((0, -.12, 1.32), (0, -.19, 1.23), .05, .015, bone="Head", k=.03),
        limb((0, -.2, 1.44), (0, -.25, 1.42), .03, .014, bone="Head", k=.02, color="tint_shade"),   # snub nose
        ellipsoid((.075, -.19, 1.535), (.075, .035, .022), rotation=(0, 18, 0), bone="Head", k=.02,
                  color="tint_shade", mirror=True),                                                  # angry brows
        # small swept-back ears
        flat_limb((.14, -.02, 1.46), (.29, .09, 1.515), .055, .02, flatten=.4, normal=(0, .3, 1), bone="Head", k=.02,
                  tau=flat_tau, mirror=True),
        # horns
        limb((.09, -.07, 1.6), (.15, -.05, 1.69), .045, .034, bone="Head", k=.02, color="bone", mirror=True),
        limb((.15, -.05, 1.69), (.17, .0, 1.76), .034, .02, bone="Head", k=.01, color="bone", mirror=True),
        limb((.17, .0, 1.76), (.14, .06, 1.8), .02, .006, bone="Head", k=.01, color="bone", mirror=True),
        # arms
        limb((.17, .02, 1.16), (.29, .05, .93), .05, .038, bone="UpperArm.L", k=.03, mirror=True),
        limb((.29, .05, .93), (.35, .0, .71), .038, .046, bone="Forearm.L", k=.02, mirror=True),
        ellipsoid((.36, -.02, .645), (.045, .05, .06), bone="Hand.L", k=.02, color="tint_shade", mirror=True),
        limb((.34, -.055, .615), (.335, -.085, .525), .02, .016, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.37, -.05, .61), (.38, -.075, .515), .02, .016, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.398, -.03, .615), (.415, -.04, .53), .019, .015, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.325, -.04, .655), (.305, -.09, .615), .019, .015, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        # legs
        limb((.1, .03, .8), (.13, -.055, .46), .076, .05, bone="Thigh.L", k=.04, mirror=True),
        sphere((.13, -.07, .465), .05, bone="Shin.L", k=.02, mirror=True),
        limb((.13, -.055, .46), (.13, .03, .13), .048, .034, bone="Shin.L", k=.02, mirror=True),
        ellipsoid((.13, .0, .35), (.048, .052, .085), rotation=(-14, 0, 0), bone="Shin.L", k=.03, mirror=True),
        ellipsoid((.13, -.05, .06), (.07, .13, .055), bone="Foot.L", k=.03, color="tint_shade", mirror=True),
        limb((.105, -.14, .04), (.095, -.22, .032), .027, .021, bone="Foot.L", k=.012, color="tint_shade", mirror=True),
        limb((.155, -.14, .04), (.165, -.22, .032), .027, .021, bone="Foot.L", k=.012, color="tint_shade", mirror=True),
        # furnace belly: a shallow scorched recess; the glowing core sits inside it
        ellipsoid((0, -.18, .935), (.08, .045, .085), op="sub", k=.01, color="ember"),
        # stove mouth: a wide carved grin curling up at the corners
        ellipsoid((0, -.2, 1.35), (.075, .06, .032), rotation=(8, 0, 0), op="sub", k=.008, color="mouth"),
        ellipsoid((.075, -.185, 1.362), (.06, .06, .026), rotation=(8, -22, 0), op="sub", k=.008, color="mouth", mirror=True),
        # tail: rises behind the hips so it never hangs between the legs; spade tip
        *tube(catmull(tail_pts, 3), .045, .022, ["Tail1", "Tail1", "Tail2", "Tail2", "Tail3"], k=.015),
        flat_limb((0, .91, 1.02), (0, .96, 1.17), .062, .01, flatten=.28, normal=(1, 0, 0), bone="Tail3", k=.012,
                  tau=flat_tau, color="tint_shade"),
    ]
    m.piece("Body", body, faces=2700, voxel=.011, tau=.04)
    eyes(m, (.075, -.205, 1.47), (.052, .03, .045), "eye_yellow", slit=True)

    def mouth_glow(mesh):
        mesh.sphere((0, -.155, 1.35), 1.0, "fire_orange", segments=8, rings=4, scale=(.13, .04, .035))
        mesh.sphere((0, -.18, 1.345), 1.0, "fire_yellow", segments=6, rings=4, scale=(.06, .02, .02))

    def belly_glow(mesh):
        mesh.sphere((0, -.15, .935), 1.0, "fire_orange", segments=8, rings=5, scale=(.078, .035, .082))
        mesh.sphere((0, -.17, .935), 1.0, "fire_yellow", segments=6, rings=4, scale=(.04, .02, .045))

    def teeth(mesh):
        for side in (1, -1):
            for x, z, dz in ((.03, 1.38, -1), (.075, 1.383, -1), (.115, 1.392, -1), (.05, 1.322, 1), (.1, 1.338, 1)):
                spike(mesh, (side * x, -.208 + abs(x) * .25, z), (0, -.15, dz), .035, .014, "bone", sides=3)

    def hand_claws(side):
        def build(mesh):
            for x, y, z in ((.335, -.085, .525), (.38, -.075, .515), (.415, -.04, .53), (.305, -.09, .615)):
                spike(mesh, (side * x, y, z + .01), (side * (x - .36) * 1.5, -.7, -1), .055, .013, "claw")
        return build

    def foot_claws(side):
        def build(mesh):
            for x in (.095, .165):
                spike(mesh, (side * x, -.215, .032), (side * (x - .13) * 2, -1, -.2), .055, .016, "claw")
        return build

    m.piece("Mouth", build=mouth_glow, bone="Head", glow=True)
    m.piece("Belly", build=belly_glow, bone="Spine", glow=True)
    m.piece("Teeth", build=teeth, bone="Head")
    m.piece("Wing.L", build=bat_wing(1, wing_root), bone="Wing.L")
    m.piece("Wing.R", build=bat_wing(-1, wing_root), bone="Wing.R")
    m.piece("Claws.L", build=hand_claws(1), bone="Hand.L")
    m.piece("Claws.R", build=hand_claws(-1), bone="Hand.R")
    m.piece("ToeClaws.L", build=foot_claws(1), bone="Foot.L")
    m.piece("ToeClaws.R", build=foot_claws(-1), bone="Foot.R")
    m.socket("Socket.Hand", "Hand.R", (-.37, -.06, .6))
    return m


# ---------------------------------------------------------------------------
def ogre() -> Model:
    """Ogre: a hairless gray great ape, all shoulders and forearms, knuckles near its knees."""
    m = Model("ogre")
    m.height = 3.0
    m.gait = "Heavy"
    m.preview_tint = (133, 128, 128)
    humanoid_bones(m, hip=(0, .05, 1.12), chest=(0, -.02, 1.9), neck=(0, -.14, 2.4), head_top=(0, -.4, 3.0),
                   shoulder=(.47, -.02, 2.38), elbow=(.65, .1, 1.62), wrist=(.71, -.08, .97), hand_end=(.72, -.2, .58),
                   hip_joint=(.25, .05, 1.06), knee=(.37, -.07, .6), ankle=(.31, .04, .19), toe=(.31, -.34, .05))
    k = .07
    arm_tau = .045
    body = [
        # torso: pelvis, pot belly, barrel chest, a huge trapezius hump the head sinks into
        ellipsoid((0, .06, 1.13), (.35, .27, .22), bone="Hips"),
        ellipsoid((0, -.1, 1.42), (.4, .36, .36), bone="Spine", k=k),
        ellipsoid((0, -.02, 1.93), (.48, .4, .42), bone="Chest", k=k),
        ellipsoid((.21, -.3, 2.06), (.2, .13, .17), rotation=(0, -15, 0), bone="Chest", k=.06, mirror=True),     # pecs
        ellipsoid((0, .17, 2.34), (.45, .33, .34), rotation=(-20, 0, 0), bone="Chest", k=.08),                   # upper back
        ellipsoid((0, .0, 2.6), (.37, .27, .21), bone="Chest", k=.08),                                           # trapezius
        limb((0, -.12, 2.4), (0, -.25, 2.58), .17, .15, bone="Neck", k=.05),
        # head: low and forward; cone crest, angry brow over deep sockets, underbite jaw
        ellipsoid((0, -.31, 2.69), (.21, .21, .2), bone="Head", k=.05),
        limb((0, -.29, 2.76), (0, -.27, 2.97), .14, .035, bone="Head", k=.08),                                    # crest
        ellipsoid((.1, -.5, 2.725), (.14, .085, .06), rotation=(-6, -16, 0), bone="Head", k=.04, color="tint_shade",
                  mirror=True),                                                                                       # brow
        ellipsoid((0, -.49, 2.53), (.2, .15, .12), bone="Head", k=.05, color="tint_light"),                        # muzzle
        ellipsoid((0, -.565, 2.43), (.205, .15, .085), rotation=(-10, 0, 0), bone="Head", k=.04, color="tint_light"),  # jaw
        ellipsoid((0, -.625, 2.585), (.09, .05, .05), bone="Head", k=.03, color="tint_shade"),                     # nose
        ellipsoid((.22, -.28, 2.64), (.035, .06, .08), bone="Head", k=.03, mirror=True),                           # ears
        ellipsoid((.11, -.545, 2.64), (.08, .05, .055), op="sub", k=.015, color="tint_deep", mirror=True),       # eye sockets
        ellipsoid((.035, -.665, 2.57), (.022, .03, .018), op="sub", k=.005, color="black", mirror=True),          # nostrils
        ellipsoid((0, -.645, 2.485), (.15, .07, .026), rotation=(-6, 0, 0), op="sub", k=.01, color="mouth"),       # mouth
        # arms: huge deltoids and forearms, knuckle-walking fists beside the knees
        sphere((.5, -.02, 2.36), .21, bone="UpperArm.L", k=.06, tau=arm_tau, mirror=True),
        limb((.5, -.02, 2.32), (.65, .1, 1.62), .19, .14, bone="UpperArm.L", k=.05, tau=arm_tau, mirror=True),
        ellipsoid((.58, -.08, 2.0), (.14, .14, .24), rotation=(10, -10, 0), bone="UpperArm.L", k=.05, tau=arm_tau, mirror=True),
        limb((.65, .1, 1.62), (.71, -.08, 1.0), .15, .12, bone="Forearm.L", k=.04, tau=arm_tau, mirror=True),
        ellipsoid((.68, .03, 1.4), (.165, .17, .25), rotation=(14, -4, 0), bone="Forearm.L", k=.05, tau=arm_tau, mirror=True),
        ellipsoid((.72, -.13, .8), (.14, .17, .2), rotation=(15, 0, 0), bone="Hand.L", k=.04, tau=arm_tau,
                  color="tint_shade", mirror=True),
        *[sphere((.72 + dx, -.22 + abs(dx) * .3, .63), .06, bone="Hand.L", k=.03, tau=arm_tau, color="tint_shade", mirror=True)
          for dx in (-.09, -.03, .03, .09)],                                                                          # knuckles
        limb((.62, -.17, .86), (.61, -.27, .74), .055, .045, bone="Hand.L", k=.03, tau=arm_tau, color="tint_shade",
             mirror=True),                                                                                             # thumb
        # legs: short, thick and bowed
        limb((.25, .05, 1.06), (.37, -.07, .6), .21, .16, bone="Thigh.L", k=.05, tau=arm_tau, mirror=True),
        limb((.37, -.07, .6), (.31, .04, .22), .15, .125, bone="Shin.L", k=.04, tau=arm_tau, mirror=True),
        ellipsoid((.35, .05, .45), (.135, .14, .19), bone="Shin.L", k=.05, tau=arm_tau, mirror=True),                 # calf
        ellipsoid((.31, -.13, .1), (.18, .3, .11), bone="Foot.L", k=.05, tau=arm_tau, color="tint_shade", mirror=True),
        *[sphere((.31 + dx, -.4 + abs(dx) * .4, .07), .055, bone="Foot.L", k=.03, tau=arm_tau, color="tint_shade",
                 mirror=True) for dx in (-.1, -.03, .04, .11)],                                                       # toes
        # loincloth with a notched hem, on a rope belt
        box((0, -.37, .9), (.44, .06, .5), rotation=(12, 0, 0), round=.02, bone="Hips", k=.02, color="cloth_brown"),
        box((0, .31, .93), (.48, .06, .46), rotation=(-10, 0, 0), round=.02, bone="Hips", k=.02, color="cloth_brown"),
        box((0, -.31, .64), (.16, .3, .16), rotation=(12, 45, 0), op="sub", k=.01),
        box((0, .37, .69), (.16, .3, .16), rotation=(-10, 45, 0), op="sub", k=.01),
        box((0, -.05, 1.2), (.88, 1.0, .075), op="paint", color="straw"),
        sphere((.13, -.43, 1.2), .07, bone="Hips", k=.02, color="straw"),                                             # knot
        limb((.13, -.47, 1.17), (.17, -.48, 1.0), .03, .022, bone="Hips", k=.02, color="straw"),
    ]
    m.piece("Body", body, faces=3500, voxel=.025, tau=.06)
    eyes(m, (.11, -.53, 2.64), (.06, .032, .046), "eye_yellow", pupil=(.4, .55))

    def tusks(mesh):
        for side in (1, -1):
            spike(mesh, (side * .14, -.66, 2.46), (side * .3, -.2, 1), .18, .045, "bone", sides=5)
            for x in (.04, .09):
                spike(mesh, (side * x, -.69, 2.47), (side * .1, -.1, 1), .06, .02, "bone", sides=4)

    m.piece("Tusks", build=tusks, bone="Head")
    m.socket("Socket.Hand", "Hand.R", (-.7, -.24, .78))
    return m


# ---------------------------------------------------------------------------
def gargoyle() -> Model:
    """Gargoyle: a crouching winged statue with a snarling muzzle and red eyes; carved stone."""
    m = Model("gargoyle")
    m.height = 1.9
    m.gait = "Biped"
    m.preview_tint = (117, 120, 117)
    humanoid_bones(m, hip=(0, .1, .86), chest=(0, -.08, 1.2), neck=(0, -.22, 1.34), head_top=(0, -.4, 1.66),
                   shoulder=(.22, -.1, 1.28), elbow=(.37, .02, .93), wrist=(.42, -.17, .52), hand_end=(.43, -.26, .33),
                   hip_joint=(.13, .1, .82), knee=(.21, -.22, .62), ankle=(.2, .14, .3), toe=(.2, -.13, .03))
    wing_kw = dict(yaw=32, lean=.2, size=1.08, thickness=.045, membrane="tint_shade", spar="tint", tip=(.22, .5),
                   fingers=((.46, .1), (.4, -.22), (.18, -.33)), wrist=(.1, .48), depth=.22, claw="tint_deep",
                   spar_radius=.04)
    wing_root = (.1, .07, 1.3)
    o, u, v = bat_wing(1, wing_root, **wing_kw).frame()
    m.bone("Wing.L", tuple(o), tuple(o + u * .1 + v * .5), "Chest", mirror=True)
    # the tail sweeps back at hip height (hidden behind the pelvis from the front) and droops far behind
    tail_pts = [(0, .18, .88), (0, .42, .85), (0, .68, .79), (0, .92, .72), (0, 1.12, .67), (0, 1.28, .69)]
    m.bone("Tail1", tail_pts[0], tail_pts[2], "Hips")
    m.bone("Tail2", tail_pts[2], tail_pts[4], "Tail1")
    m.bone("Tail3", tail_pts[4], tail_pts[5], "Tail2")

    # the head is drafted small and scaled up about its centre
    hc, hs = V(0, -.36, 1.47), 1.15

    def H(x, y, z):
        return tuple(hc + (V(x, y, z) - hc) * hs)

    def S(*r):
        return tuple(V(*r) * hs)

    k = .04
    flat_tau = .012
    body = [
        # hunched torso: blocky ribcage leaning forward over a carved, narrow waist
        ellipsoid((0, .1, .86), (.17, .14, .11), rotation=(20, 0, 0), bone="Hips"),
        box((0, .02, 1.0), (.27, .22, .24), rotation=(34, 0, 0), round=.07, bone="Spine", k=k),
        box((0, -.07, 1.19), (.4, .27, .27), rotation=(34, 0, 0), round=.06, bone="Chest", k=k),
        ellipsoid((0, .06, 1.27), (.22, .13, .13), rotation=(34, 0, 0), bone="Chest", k=k),
        limb((0, -.14, 1.3), (0, -.29, 1.42), .075, .065, bone="Neck", k=.03),
        box((0, -.085, .96), (.3, .1, .022), rotation=(34, 0, 0), op="sub", k=.006, color="tint_deep"),       # carved abs
        box((0, -.03, 1.04), (.3, .1, .022), rotation=(34, 0, 0), op="sub", k=.006, color="tint_deep"),
        # head: snarling canine muzzle under a heavy carved brow
        ellipsoid(H(0, -.32, 1.5), S(.12, .13, .11), bone="Head", k=.03),
        box(H(.055, -.425, 1.53), S(.12, .07, .045), rotation=(-15, 0, -14), round=.012, bone="Head", k=.02,
            mirror=True),                                                                                     # angry brow
        box(H(0, -.5, 1.445), S(.15, .18, .1), rotation=(8, 0, 0), round=.03, bone="Head", k=.03),
        ellipsoid(H(0, -.6, 1.475), S(.045, .03, .03), bone="Head", k=.015, color="tint_deep"),
        box(H(0, -.45, 1.37), S(.13, .21, .05), rotation=(-12, 0, 0), round=.02, bone="Head", k=.02),
        limb(H(0, -.4, 1.375), (0, -.27, 1.38), .05, .065, bone="Head", k=.03),                                  # throat
        ellipsoid(H(.08, -.4, 1.45), S(.06, .06, .045), bone="Head", k=.03, mirror=True),
        box(H(0, -.535, 1.405), S(.12, .16, .035), op="sub", k=.008, color="mouth"),
        flat_limb(H(.1, -.28, 1.53), H(.235, -.185, 1.615), .057, .022, flatten=.42, normal=(.2, .4, 1), bone="Head",
                  k=.02, tau=flat_tau, mirror=True),
        *tube(catmull([H(.06, -.38, 1.57), H(.11, -.33, 1.67), H(.16, -.23, 1.72), H(.17, -.12, 1.72),
                       H(.15, -.05, 1.77)], 2), .041, .012, ["Head"], k=.012, color="tint_deep", mirror=True),
        # long arms with three-fingered clawed hands
        sphere((.22, -.1, 1.28), .095, bone="UpperArm.L", k=.03, mirror=True),
        limb((.22, -.1, 1.27), (.37, .02, .93), .072, .054, bone="UpperArm.L", k=.03, mirror=True),
        limb((.37, .02, .93), (.42, -.17, .53), .056, .047, bone="Forearm.L", k=.02, mirror=True),
        ellipsoid((.395, -.04, .79), (.068, .064, .13), rotation=(-25, 0, 0), bone="Forearm.L", k=.03, mirror=True),
        ellipsoid((.425, -.2, .465), (.05, .06, .065), bone="Hand.L", k=.02, color="tint_shade", mirror=True),
        limb((.4, -.235, .425), (.385, -.295, .3), .023, .018, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.43, -.245, .415), (.43, -.315, .29), .024, .018, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.457, -.225, .42), (.472, -.285, .3), .023, .017, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        limb((.39, -.21, .47), (.365, -.275, .42), .021, .016, bone="Hand.L", k=.012, color="tint_shade", mirror=True),
        # crouched digitigrade legs
        limb((.13, .1, .82), (.21, -.22, .62), .112, .08, bone="Thigh.L", k=.04, mirror=True),
        sphere((.21, -.25, .625), .07, bone="Shin.L", k=.02, mirror=True),
        limb((.21, -.22, .62), (.2, .14, .3), .072, .054, bone="Shin.L", k=.02, mirror=True),
        ellipsoid((.205, -.02, .5), (.07, .08, .1), rotation=(-50, 0, 0), bone="Shin.L", k=.03, mirror=True),     # calf
        limb((.2, .14, .3), (.2, -.06, .07), .054, .046, bone="Foot.L", k=.02, mirror=True),
        limb((.2, -.06, .06), (.155, -.175, .03), .027, .021, bone="Foot.L", k=.012, color="tint_shade", mirror=True),
        limb((.2, -.06, .06), (.2, -.195, .03), .028, .021, bone="Foot.L", k=.012, color="tint_shade", mirror=True),
        limb((.2, -.06, .06), (.245, -.175, .03), .027, .021, bone="Foot.L", k=.012, color="tint_shade", mirror=True),
        # long tail
        *tube(catmull(tail_pts, 3), .065, .02, ["Tail1", "Tail1", "Tail2", "Tail2", "Tail3"], k=.015),
        # weathering: cracks and moss
        *[limb(p, q, .028, op="paint", color="tint_deep") for crack in (
            [(.12, -.238, 1.197), (.06, -.204, 1.147), (.09, -.171, 1.097), (.03, -.137, 1.048)],      # chest
            [(-.254, -.014, .749), (-.275, -.06, .725), (-.269, -.108, .69), (-.281, -.187, .641)],    # thigh
            [(-.327, -.073, 1.192), (-.353, -.048, 1.123), (-.379, -.024, 1.053)],                     # upper arm
            [(-.066, -.354, 1.611), (-.05, -.39, 1.6), (-.043, -.414, 1.59)],                          # crown
        ) for p, q in zip(crack[:-1], crack[1:])],
        ellipsoid((0, .03, 1.36), (.2, .14, .06), rotation=(34, 0, 0), op="paint", color="moss"),
        ellipsoid((.22, -.1, 1.38), (.08, .08, .035), op="paint", color="moss", mirror=True),
        ellipsoid(H(0, -.3, 1.62), S(.1, .1, .04), op="paint", color="moss"),
        ellipsoid((-.21, -.27, .7), (.06, .06, .03), op="paint", color="moss"),
    ]
    m.piece("Body", body, faces=2600, voxel=.013, tau=.04)
    eyes(m, H(.058, -.462, 1.495), S(.046, .027, .034), "eye_red", slit=True)

    def fangs(mesh):
        for side in (1, -1):
            spike(mesh, H(side * .05, -.585, 1.43), (0, -.1, -1), .05, .016, "tint_light", sides=3)
            spike(mesh, H(side * .022, -.59, 1.43), (0, -.1, -1), .028, .01, "tint_light", sides=3)
            spike(mesh, H(side * .04, -.55, 1.385), (0, -.1, 1), .04, .013, "tint_light", sides=3)

    def hand_claws(side):
        def build(mesh):
            for x, y, z in ((.385, -.295, .3), (.43, -.315, .29), (.472, -.285, .3), (.365, -.275, .42)):
                spike(mesh, (side * x, y, z + .01), (side * (x - .42) * 1.2, -.8, -1), .075, .018, "tint_deep")
        return build

    def foot_claws(side):
        def build(mesh):
            for x, y in ((.155, -.175), (.2, -.195), (.245, -.175)):
                spike(mesh, (side * x, y, .03), (side * (x - .2) * 2, -1, -.25), .065, .019, "tint_deep")
            spike(mesh, (side * .2, .17, .3), (0, 1, .3), .08, .024, "tint_deep")   # hock spur
        return build

    def spines(ridge):
        def build(mesh):
            for y, z, s in ridge:
                spike(mesh, (0, y, z), (0, 1, .55), .09 * s, .028 * s, "tint_shade", sides=4)
        return build

    m.piece("Fangs", build=fangs, bone="Head")
    m.piece("Wing.L", build=bat_wing(1, wing_root, **wing_kw), bone="Wing.L")
    m.piece("Wing.R", build=bat_wing(-1, wing_root, **wing_kw), bone="Wing.R")
    m.piece("Claws.L", build=hand_claws(1), bone="Hand.L")
    m.piece("Claws.R", build=hand_claws(-1), bone="Hand.R")
    m.piece("ToeClaws.L", build=foot_claws(1), bone="Foot.L")
    m.piece("ToeClaws.R", build=foot_claws(-1), bone="Foot.R")
    m.piece("Spines", build=spines(((.16, 1.17, 1.0), (.18, 1.06, .85))), bone="Chest")
    m.piece("Spines.Low", build=spines(((.19, .95, .7),)), bone="Spine")
    m.socket("Socket.Hand", "Hand.R", (-.43, -.24, .42))
    return m


RECIPES = {"imp": imp, "ogre": ogre, "gargoyle": gargoyle}
