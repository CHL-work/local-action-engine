"""Monster recipes: the Harpy and the Plague Nurse. Each function returns a bwkit.Model.

Heights follow SpeciesCatalog.cs (Height = top of the model, metres).
Characters face -Y (Unity +Z); their left is +X.
"""
import math

import numpy as np

from bwkit import CELL, Model, V, box, ellipsoid, flat_limb, limb, sphere, torus
from creatures import humanoid_bones, eyes


# ---------------------------------------------------------------------------
# small geometry helpers (hard-surface parts built with bwkit.Mesher)
def _unit(v):
    v = np.asarray(v, float)
    return v / np.linalg.norm(v)


def _aim(d):
    """Blender XYZ Euler (degrees) that turns +Z onto direction d."""
    d = _unit(d)
    a = math.degrees(math.acos(max(-1.0, min(1.0, d[2]))))
    c = math.degrees(math.atan2(d[0], -d[1])) if abs(d[2]) < .9999 else 0.0
    return (a, 0.0, c)


def _mx(p, side):
    """Mirror a point to the creature's right (-X) when side == -1."""
    return V(p[0] * side, p[1], p[2])


# Feather outlines: (fraction along the feather, half-width as a fraction of the width).
_PRIMARY = [(0, .2), (.3, .5), (.78, .4), (1, 0), (.78, -.4), (.3, -.5), (0, -.2)]
_BROAD = [(0, .25), (.4, .5), (1, .26), (1, -.26), (.4, -.5), (0, -.25)]


def _feather(mesh, root, direction, normal, length, width, color, side=1, thick=.014, outline=_PRIMARY):
    """A flat feather card from `root` along `direction`, lying in the plane whose normal is `normal`.
    Points are given for the left side; side == -1 mirrors the card to the right."""
    root = V(*root)
    d = _unit(direction)
    n = _unit(normal)
    n = _unit(n - d * (n @ d))
    s = np.cross(n, d)
    pts = [root + d * length * a + s * width * b for a, b in outline]
    if side < 0:
        pts = [_mx(p, -1) for p in pts][::-1]
    mesh.poly([tuple(p) for p in pts], color, thick)


def _claw(mesh, base, direction, bend, length, radius, color="claw", side=1):
    """A curved talon: a tapering root segment, then a cone hooked further along `bend`."""
    base = V(*base)
    d1, d2 = _unit(direction), _unit(bend)
    l1, l2 = length * .45, length * .6
    c1 = base + d1 * l1 / 2
    mesh.cylinder(tuple(_mx(c1, side)), radius, l1, color, sides=5, rotation=_aim(_mx(d1, side)), radius_top=radius * .72)
    c2 = base + d1 * l1 + d2 * l2 / 2
    mesh.cone(tuple(_mx(c2, side)), radius * .72, l2, color, sides=5, rotation=_aim(_mx(d2, side)))


# ---------------------------------------------------------------------------
def harpy() -> Model:
    """A mutated giant crow. From afar its white face patch reads as a woman's face."""
    m = Model("harpy")
    m.height = 1.5
    m.gait = "Flyer"
    m.preview_tint = (31, 28, 36)
    m.preview_height = 1.75          # frame the 2.6 m wingspan in previews

    # Skeleton: a hovering crow, body pitched forward, talons hanging below.
    m.bone("Hips", (0, .08, .6), (0, .02, .76))
    m.bone("Spine", (0, .02, .76), (0, -.05, .9), "Hips")
    m.bone("Chest", (0, -.05, .9), (0, -.1, 1.04), "Spine")
    m.bone("Neck", (0, -.1, 1.04), (0, -.13, 1.14), "Chest")
    m.bone("Head", (0, -.13, 1.14), (0, -.15, 1.48), "Neck")
    m.bone("Wing.L", (.15, -.01, 1.0), (.64, -.05, 1.1), "Chest", mirror=True)
    m.bone("WingTip.L", (.64, -.05, 1.1), (1.25, .08, 1.2), "Wing.L", mirror=True)
    m.bone("Thigh.L", (.1, .07, .6), (.12, .04, .38), "Hips", mirror=True)
    m.bone("Shin.L", (.12, .04, .38), (.125, .03, .15), "Thigh.L", mirror=True)
    m.bone("Foot.L", (.125, .03, .15), (.125, -.13, .04), "Shin.L", mirror=True)
    m.bone("Tail1", (0, .18, .6), (0, .48, .42), "Hips")

    eye = (.075, -.274, 1.325)
    body = [
        # torso: a round, forward-pitched crow body with a puffed breast and a shaggy belly
        ellipsoid((0, .11, .62), (.14, .17, .13), rotation=(46, 0, 0), bone="Hips"),
        ellipsoid((0, 0, .78), (.19, .2, .24), rotation=(46, 0, 0), bone="Spine", k=.06),
        ellipsoid((0, -.08, .95), (.23, .18, .17), rotation=(46, 0, 0), bone="Chest", k=.06),
        ellipsoid((0, -.16, .86), (.17, .11, .15), rotation=(36, 0, 0), bone="Chest", k=.06),
        # overlapping breast feathers
        flat_limb((0, -.27, .98), (0, -.29, .87), .05, .012, flatten=.32, normal=(0, -1, -.1), bone="Chest", k=.02),
        flat_limb((.1, -.255, .96), (.105, -.275, .86), .048, .012, flatten=.32, normal=(.3, -1, -.1), bone="Chest",
                  k=.02, mirror=True),
        flat_limb((.055, -.255, .84), (.058, -.25, .73), .048, .012, flatten=.32, normal=(.15, -1, .25), bone="Spine",
                  k=.02, mirror=True),
        flat_limb((.15, -.205, .84), (.155, -.2, .74), .045, .012, flatten=.32, normal=(.6, -1, .25), bone="Spine",
                  k=.02, mirror=True),
        flat_limb((.07, -.12, .64), (.08, -.15, .5), .05, .012, flatten=.45, normal=(0, 1, .3), bone="Hips", k=.03,
                  mirror=True),
        flat_limb((.16, -.02, .62), (.19, -.04, .49), .05, .012, flatten=.45, normal=(-.3, 1, .3), bone="Hips",
                  k=.03, mirror=True),
        # neck and a ruff of iridescent hackles
        limb((0, -.09, 1.0), (0, -.13, 1.14), .1, .09, bone="Neck", k=.05),
        ellipsoid((0, -.12, 1.07), (.17, .15, .08), rotation=(18, 0, 0), bone="Neck", k=.04, color="feather"),
        # head: skull, long hair-like side curtains, a flat face mask
        ellipsoid((0, -.13, 1.3), (.16, .15, .16), bone="Head", k=.05),
        ellipsoid((0, -.06, 1.33), (.14, .13, .13), bone="Head", k=.05),
        ellipsoid((.15, -.12, 1.17), (.06, .11, .16), rotation=(0, -6, 0), bone="Head", k=.04, mirror=True),
        flat_limb((.145, -.16, 1.08), (.165, -.17, .95), .05, .012, flatten=.45, normal=(1, 0, 0), bone="Head",
                  k=.03, mirror=True),
        ellipsoid((0, -.205, 1.28), (.14, .085, .17), bone="Head", k=.035),
        # a messy crest of feathers at the back of the head
        flat_limb((0, .0, 1.4), (0, .17, 1.31), .055, .012, flatten=.45, normal=(1, 0, 0), bone="Head", k=.03),
        flat_limb((.07, -.01, 1.38), (.11, .14, 1.26), .05, .012, flatten=.45, normal=(1, 0, -.3), bone="Head",
                  k=.03, mirror=True),
        # angry brows
        limb((.035, -.295, 1.372), (.125, -.262, 1.395), .015, .011, bone="Head", k=.012, mirror=True),
        # wings: arm and broad inner membrane (Wing), hand (WingTip)
        limb((.13, -.03, 1.0), (.64, -.06, 1.1), .07, .045, bone="Wing.L", k=.05, tau=.03, mirror=True),
        flat_limb((.14, .08, .99), (.62, .04, 1.09), .19, .1, flatten=.26, normal=(-.09, 0, 1), bone="Wing.L",
                  k=.04, tau=.03, mirror=True),
        limb((.64, -.06, 1.1), (.96, 0, 1.15), .045, .03, bone="WingTip.L", k=.03, tau=.03, mirror=True),
        flat_limb((.64, .02, 1.1), (.94, .06, 1.15), .1, .05, flatten=.32, normal=(-.15, 0, 1), bone="WingTip.L",
                  k=.03, tau=.03, mirror=True),
        # tail root
        ellipsoid((0, .22, .57), (.12, .14, .08), rotation=(-30, 0, 0), bone="Tail1", k=.05),
        # legs: feathered thighs, scaly shanks, big open feet
        ellipsoid((.1, .06, .48), (.085, .09, .13), bone="Thigh.L", k=.05, mirror=True),
        limb((.12, .04, .4), (.125, .03, .16), .042, .034, bone="Shin.L", k=.03, color="beak", mirror=True),
        ellipsoid((.125, .02, .12), (.055, .06, .04), bone="Foot.L", k=.03, color="beak", mirror=True),
        limb((.125, -.01, .115), (.07, -.16, .088), .03, .022, bone="Foot.L", k=.02, color="beak", mirror=True),
        limb((.125, -.01, .115), (.13, -.18, .083), .03, .022, bone="Foot.L", k=.02, color="beak", mirror=True),
        limb((.125, -.01, .115), (.185, -.155, .088), .03, .022, bone="Foot.L", k=.02, color="beak", mirror=True),
        limb((.125, .05, .115), (.125, .17, .088), .028, .02, bone="Foot.L", k=.02, color="beak", mirror=True),
        # paint: the white woman's-face mask with a widow's peak and dark brows
        ellipsoid((0, -.31, 1.28), (.135, .13, .172), op="paint", color="white"),
        ellipsoid((0, -.31, 1.465), (.03, .1, .05), op="paint", color="tint"),
        limb((.035, -.295, 1.372), (.125, -.262, 1.395), .021, .017, op="paint", color="tint", mirror=True),
    ]
    m.piece("Body", body, faces=1950, voxel=.0135, tau=.04)
    eyes(m, eye, (.042, .026, .03), "eye_red", pupil=(.3, .5))

    beak = [
        flat_limb((0, -.27, 1.272), (0, -.48, 1.195), .056, .02, flatten=.6, normal=(1, 0, 0), color="charcoal"),
        limb((0, -.47, 1.2), (0, -.505, 1.15), .02, .008, k=.01, color="charcoal"),
        flat_limb((0, -.265, 1.23), (0, -.44, 1.175), .04, .014, flatten=.62, normal=(1, 0, 0), k=.01, color="charcoal"),
    ]
    m.piece("Beak", beak, bone="Head", faces=160, voxel=.007)

    # Rigid feathers: coverts and secondaries on the arm, fingered primaries fanned on the hand.
    # Each wing part has a frame: u runs out along the bone, v points back, n is the wing plane's normal.
    def frame(a, b, tilt):
        a, b = V(*a), V(*b)
        u = _unit(b - a)
        n = _unit(V(-math.sin(math.radians(tilt)), 0, math.cos(math.radians(tilt))))
        n = _unit(n - u * (n @ u))
        return a, u, np.cross(n, u), n, float(np.linalg.norm(b - a))

    arm = frame((.15, -.01, 1.0), (.64, -.05, 1.1), 11)
    hand = frame((.64, -.05, 1.1), (1.25, .08, 1.2), 9)

    def fan(fr, t, back, out_deg, lift=0.0):
        o, u, v, n, L = fr
        a = math.radians(out_deg)
        return o + u * L * t + v * back + n * lift, v * math.cos(a) + u * math.sin(a), n

    def arm_feathers(side):
        def build(mesh):
            for i in range(7):      # secondaries along the trailing edge
                root, d, n = fan(arm, .04 + .92 * i / 6, .15, -6 + 26 * i / 6)
                _feather(mesh, root, d, n, .31 + .03 * i / 6, .15, "tint" if i % 2 else "feather_dark", side,
                         outline=_BROAD)
            for i in range(4):      # a row of lighter coverts on top
                root, d, n = fan(arm, .1 + .8 * i / 3, -.01, -4 + 20 * i / 3, .035)
                _feather(mesh, root, d, n, .19, .13, "feather", side, thick=.012, outline=_BROAD)
        return build

    def primaries(side):
        def build(mesh):
            for i in range(7):      # fingered primaries fanned from the hand
                root, d, n = fan(hand, .04 + .5 * i / 6, .1 - .06 * i / 6, 14 + 70 * i / 6)
                _feather(mesh, root, d, n, .42 - .05 * i / 6, .11, "tint_deep" if i % 2 else "feather_dark", side)
        return build

    for side, sfx in ((1, "L"), (-1, "R")):
        m.piece("WingFeathers" + sfx, build=arm_feathers(side), bone="Wing." + sfx)
        m.piece("Primaries" + sfx, build=primaries(side), bone="WingTip." + sfx)

    def tail(mesh):
        d0 = _unit((0, .85, -.5))
        x = V(1, 0, 0)
        n = np.cross(d0, x)
        for i, phi in enumerate((-42, -28, -14, 0, 14, 28, 42)):
            a = math.radians(phi)
            d = d0 * math.cos(a) + x * math.sin(a)
            _feather(mesh, V(0, .22, .57) + d * .02, d, n, .47 - .1 * (abs(phi) / 42) ** 1.5, .14,
                     "tint" if i % 2 else "feather_dark", outline=_BROAD)
        for phi in (-20, 0, 20):
            a = math.radians(phi)
            d = d0 * math.cos(a) + x * math.sin(a)
            _feather(mesh, V(0, .2, .6) + n * .02, d, n, .24, .13, "feather", thick=.012, outline=_BROAD)

    m.piece("TailFan", build=tail, bone="Tail1")

    def talons(side):
        def build(mesh):
            for tip, d in (((.07, -.16, .088), (-.3, -.8, -.4)), ((.13, -.18, .083), (0, -.85, -.45)),
                           ((.185, -.155, .088), (.35, -.75, -.4)), ((.125, .17, .088), (0, .85, -.45))):
                d = _unit(d)
                bend = _unit(d + V(0, 0, -1.3))
                _claw(mesh, tip, d, bend, .11, .024, "claw", side)
        return build

    m.piece("TalonsL", build=talons(1), bone="Foot.L")
    m.piece("TalonsR", build=talons(-1), bone="Foot.R")
    m.socket("Socket.Hand", "Foot.R", (0, -.08, .03))
    return m


# ---------------------------------------------------------------------------
def _mirror_prims(prims):
    return [p.mirrored() for p in prims]


def nurse() -> Model:
    """The Plague Nurse: a tall field-medic bioweapon in a black robe, its plague mask grown from bone."""
    m = Model("nurse")
    m.height = 2.2
    m.gait = "Glide"
    m.preview_tint = (31, 33, 31)
    # A tall, slightly hunched figure; the legs stay hidden under the robe.
    humanoid_bones(m, hip=(0, .02, 1.02), chest=(0, 0, 1.42), neck=(0, -.04, 1.65), head_top=(0, -.1, 2.03),
                   shoulder=(.19, 0, 1.59), elbow=(.27, -.01, 1.27), wrist=(.3, -.06, .96), hand_end=(.31, -.09, .79),
                   hip_joint=(.1, .02, 1.0), knee=(.11, 0, .55), ankle=(.11, .03, .1), toe=(.11, -.1, .03))
    m.bone("Sac", (0, .15, 1.31), (0, .27, 1.31), "Spine")
    hx, hy, hz, hs = 0, -.05, 1.83, 1.1     # centre of the head inside the hood, and its scale

    def hp(dx, dy, dz):
        """A point on the head, given relative to its centre at scale 1."""
        return (hx + dx * hs, hy + dy * hs, hz + dz * hs)

    robe_tau = .09
    body = [
        # A bell-shaped robe skirt hangs from the hips; soft front panels follow the thighs so it sways.
        limb((0, .03, 1.08), (0, .06, -.06), .15, .35, bone="Hips", tau=robe_tau, color="tint_shade"),
        limb((.1, -.01, .98), (.16, -.05, .05), .1, .22, bone="Thigh.L", tau=robe_tau, color="tint_shade", mirror=True),
        # the apron
        flat_limb((0, -.128, 1.06), (0, -.315, .14), .095, .15, flatten=.14, normal=(0, -1, .19), bone="Hips",
                  tau=robe_tau, k=.015, color="cloth_gray"),
    ]
    for deg, bone in ((-55, "Thigh.L"), (-20, "Thigh.L"), (15, "Hips"), (50, "Hips"), (90, "Hips")):
        a = math.radians(deg)
        c, sn = math.cos(a), math.sin(a)
        for side in ((1, -1) if deg != 90 else (1,)):
            b = bone if side > 0 else bone.replace(".L", ".R")
            body.append(limb((side * .16 * c, .03 + .16 * sn, 1.0), (side * .32 * c, .06 + .32 * sn, -.06), .03, .06,
                             bone=b, tau=robe_tau, k=.03))
    body += [
        box((0, 0, -.25), (2, 2, .5), op="sub"),            # the hem rests on the floor
        # torso and a tattered capelet over the shoulders
        ellipsoid((0, .03, 1.2), (.13, .1, .2), bone="Spine", k=.06),
        ellipsoid((0, 0, 1.45), (.17, .12, .19), rotation=(8, 0, 0), bone="Chest", k=.06),
        ellipsoid((0, .01, 1.575), (.25, .175, .1), rotation=(6, 0, 0), bone="Chest", k=.04),
    ]
    for deg in (-60, 0, 55):
        a = math.radians(deg)
        c, sn = math.cos(a), math.sin(a)
        body.append(flat_limb((.23 * c, .01 + .16 * sn, 1.53), (.26 * c, .01 + .19 * sn, 1.4), .055, .012,
                              flatten=.35, normal=(c, sn, 0), bone="Chest", k=.03, mirror=True))
    body += [
        # neck, hood and the bone mask in the hood's opening
        limb((0, -.01, 1.58), (0, -.05, 1.75), .08, .07, bone="Neck", k=.04),
        ellipsoid(hp(0, 0, .01), (.145 * hs, .155 * hs, .18 * hs), bone="Head", k=.05),
        ellipsoid(hp(0, -.19, .01), (.1 * hs, .08 * hs, .12 * hs), op="sub", k=.02),
        ellipsoid(hp(0, -.095, 0), (.104 * hs, .07 * hs, .118 * hs), bone="Head", k=.015, color="bone"),
        # trim: the hem band, a belt over the apron, acid stains on the apron
        box((0, 0, .05), (2, 2, .1), op="paint", color="tint_light"),
        ellipsoid((0, .03, 1.1), (.17, .14, .028), op="paint", color="leather_dark"),
        sphere((.045, -.23, .66), .035, op="paint", color="acid_dark"),
        sphere((-.06, -.28, .36), .03, op="paint", color="acid_dark"),
    ]
    m.piece("Body", body, faces=1550, voxel=.014, tau=.05)

    # The arms are a skinned piece of their own, so the robe's soft weights cannot pull at them.
    arms = [
        limb((.19, 0, 1.59), (.27, -.01, 1.27), .058, .052, bone="UpperArm.L", k=.04, mirror=True),
        limb((.268, -.01, 1.29), (.28, -.02, 1.2), .056, .065, bone="Forearm.L", k=.02, mirror=True),
        limb((.27, -.01, 1.27), (.3, -.06, .99), .04, .033, bone="Forearm.L", k=.02, color="leather_dark", mirror=True),
        ellipsoid((.28, -.02, 1.2), (.08, .08, .024), op="paint", color="tint_light", mirror=True),
    ]
    m.piece("Arms", arms, faces=300, voxel=.01, tau=.03)

    eye = hp(.066, -.14, .012)
    eyes(m, eye, (.042 * hs, .024 * hs, .042 * hs), "glass_green", pupil=(.26, .26))
    beak = [
        flat_limb(hp(0, -.12, -.01), hp(0, -.32, -.085), .064 * hs, .04 * hs, flatten=.7, normal=(1, 0, 0),
                  color="bone"),
        flat_limb(hp(0, -.32, -.085), hp(0, -.49, -.23), .04 * hs, .01, flatten=.75, normal=(1, 0, 0), k=.03,
                  color="bone"),
        torus((eye[0], eye[1] - .002, eye[2]), .043 * hs, .012 * hs, rotation=(90, 0, 0), k=.01, color="bone",
              mirror=True),
        limb(hp(0, -.14, .02), hp(0, -.34, -.07), .012 * hs, .008, k=.01, color="white"),          # grown ridge
        limb(hp(.075, -.13, -.02), hp(.11, -.06, -.05), .014 * hs, .008, k=.01, color="bone", mirror=True),
        sphere(hp(.02, -.29, -.07), .011 * hs, op="sub", color="charcoal", mirror=True),            # nostrils
    ]
    m.piece("Beak", beak, bone="Head", faces=240, voxel=.006)

    def hat(mesh):
        tilt = (-7, 0, 0)
        mesh.cylinder(hp(0, -.01, .15), .3 * hs, .025, "tint_deep", sides=14, rotation=tilt)
        mesh.cylinder(hp(0, -.005, .25), .13 * hs, .18 * hs, "tint_deep", sides=10, rotation=tilt, radius_top=.118 * hs)
        mesh.cylinder(hp(0, -.006, .185), .135 * hs, .045 * hs, "tint_light", sides=10, rotation=tilt)

    m.piece("Hat", build=hat, bone="Head")

    # Gloves: an open left hand with long fingers; the right fist grips the syringe.
    palm = [
        ellipsoid((.3, -.072, .918), (.036, .048, .058), color="leather_dark"),
        limb((.293, -.05, .99), (.3, -.06, .95), .046, .04, k=.01, color="leather_dark"),     # gauntlet cuff
    ]
    glove = list(palm)
    for dx, dy, length in ((-.017, -.085, .2), (.002, -.09, .22), (.021, -.08, .2)):
        glove.append(limb((.3 + dx, -.08, .89), (.3 + dx * 1.5, dy - .02, .89 - length), .013, .008, k=.012,
                          color="leather_dark"))
    glove.append(limb((.28, -.1, .92), (.27, -.14, .85), .013, .009, k=.012, color="leather_dark"))  # thumb
    fist = _mirror_prims(palm) + [
        ellipsoid((-.3, -.128, .885), (.045, .05, .05), k=.015, color="leather_dark"),
        limb((-.27, -.095, .93), (-.288, -.158, .925), .014, .011, k=.01, color="leather_dark"),    # thumb
    ]
    m.piece("GloveL", glove, bone="Hand.L", faces=170, voxel=.006, symmetric=False)
    m.piece("GloveR", fist, bone="Hand.R", faces=150, voxel=.006, symmetric=False)

    def syringe(mesh):
        x, y = -.3, -.13
        mesh.cylinder((x, y, .82), .032, .24, "glass_green", sides=8)                 # barrel
        mesh.cylinder((x, y, .946), .044, .014, "steel", sides=8)                     # finger flange
        mesh.cylinder((x, y, 1.0), .008, .1, "steel", sides=6)                        # plunger rod
        mesh.cylinder((x, y, 1.056), .024, .012, "steel", sides=8)                    # thumb rest
        mesh.cone((x, y, .685), .03, .03, "steel", sides=8, rotation=(180, 0, 0))     # nozzle
        mesh.cylinder((x, y, .6), .004, .14, "steel", sides=4)                        # needle

    # Named Weapon*: Unity hides it while the nurse holds an item in that hand.
    m.piece("WeaponSyringe", build=syringe, bone="Hand.R")
    m.socket("Socket.Hand", "Hand.R", (-.3, -.13, .885))

    # A row of acid sacs down the back, each bulging out of an iron cup.
    # (height, back surface depth, radius)
    sacs = ((1.53, .155, .046), (1.42, .122, .06), (1.31, .122, .072), (1.2, .144, .062), (1.09, .181, .05))

    def sac_glow(mesh):
        for z, y, r in sacs:
            c = V(0, y + r * .5, z)
            for f in mesh.sphere(tuple(c), 1.0, "acid", segments=8, rings=5, scale=(r, r * .9, r)):
                if f.calc_center_median().z < z - r * .45:     # settled, darker fluid at the bottom
                    f[mesh.layer] = CELL["acid_dark"]

    def sac_ports(mesh):
        for z, y, r in sacs:     # iron cups the sacs grow out of
            mesh.cylinder((0, y + r * .2, z), r * 1.1, r * .62, "iron_dark", sides=8, rotation=_aim((0, 1, 0)))

    m.piece("Sacs", build=sac_glow, bone="Sac", glow=True)
    m.piece("SacPorts", build=sac_ports, bone="Sac")
    return m


RECIPES = {"harpy": harpy, "nurse": nurse}
