"""Props, items and the player's staff and boot.

Props are built around their own centre (Unity places an object's model at the
centre of its collider box and scales it to the box), except where noted.
Blender axes: +Z up, -Y is the front.
"""
import math

from bwkit import Model, V, box, cylinder, ellipsoid, limb, sphere


def prop(name, kind="prop"):
    m = Model(name)
    m.kind = kind
    m.gait = "None"
    return m


# ---------------------------------------------------------------------------
# Player equipment
def staff() -> Model:
    """First-person staff: the shaft runs along +Z from z = -0.47; the head is on top."""
    m = prop("staff", "equipment")

    def shaft(s):
        # A slightly crooked shaft from three tapered segments, with knots and a grip.
        pts = [(0, 0, -.47), (.012, -.006, -.05), (-.01, .004, .32), (.004, 0, .5)]
        radii = [.022, .027, .029, .034]
        for (a, b), (ra, rb) in zip(zip(pts, pts[1:]), zip(radii, radii[1:])):
            mid = [(x + y) / 2 for x, y in zip(a, b)]
            length = math.dist(a, b)
            tilt = math.degrees(math.atan2(math.hypot(b[0] - a[0], b[1] - a[1]), b[2] - a[2]))
            yaw = math.degrees(math.atan2(b[1] - a[1], b[0] - a[0]))
            s.cylinder(mid, ra, length, "wood", sides=7, radius_top=rb, rotation=(0, tilt, yaw))
        for z, r in ((-.2, .034), (.15, .036), (.36, .036)):
            s.ico((0, 0, z), r, "wood_dark", subdiv=1, scale=(1, 1, .7))
        for i in range(5):  # leather grip wrap
            s.cylinder((0, 0, -.12 + i * .045), .032, .03, "leather_dark" if i % 2 else "leather", sides=8, rotation=(0, 6 if i % 2 else -6, 0))
        s.cylinder((0, 0, -.49), .026, .05, "iron", sides=7)       # iron ferrule
        s.cone((0, 0, -.53), .024, .04, "iron_dark", sides=7, rotation=(180, 0, 0))
        # Carved claws cradling the orb.
        for i in range(4):
            a = i * math.pi / 2 + math.pi / 4
            x, y = math.cos(a), math.sin(a)
            s.cylinder((x * .045, y * .045, .56), .014, .12, "wood_dark", sides=5, radius_top=.008,
                       rotation=(-y * 24, x * 24, 0))
            s.cone((x * .07, y * .07, .645), .012, .05, "wood_dark", sides=4, rotation=(-y * 60, x * 60, 0))
        s.torus((0, 0, .53), .045, .012, "brass_dark", sides=10, tube_sides=4)

    def orb(s):
        s.ico((0, 0, .6), .062, "eye_cyan", subdiv=2)

    def iris(s):
        # A dark slit pupil facing forward makes the orb read as an eye.
        s.box((0, -.058, .6), (.016, .012, .07), "black")

    def hand(s):
        # A gloved fist around the grip and a sleeve reaching back toward the camera
        # (Blender +Y is toward the viewer once the staff sits in Unity's view).
        s.ico((.004, .006, -.04), .05, "leather_dark", subdiv=1, scale=(1.05, 1.1, 1.25))
        for i in range(4):
            s.box((-.012 + i * .0, -.04, -.085 + i * .028), (.075, .028, .026), "leather", bevel=.006)
        s.box((.045, -.02, -.02), (.03, .05, .06), "leather_dark", rotation=(0, -20, 0))       # thumb
        # The forearm leaves the fist toward the viewer and down (axis (0, .79, -.62)).
        # Blender -X is Unity +X: the right forearm comes in from the lower right.
        s.cylinder((-.045, .175, -.165), .045, .3, "cloth_gray", sides=7, radius_top=.058, rotation=(-128, 12, 0))
        s.cylinder((-.018, .06, -.08), .05, .05, "leather", sides=7, rotation=(-128, 12, 0))   # cuff

    m.piece("Shaft", build=shaft)
    m.piece("Orb", build=orb, glow=True)
    m.piece("Iris", build=iris)
    m.piece("Hand", build=hand)
    return m


def boot() -> Model:
    """Kick boot, centred; toe toward -Y (front)."""
    m = prop("boot", "equipment")

    def b(s):
        s.box((0, .03, -.04), (.2, .3, .1), "leather", bevel=.02)             # foot
        s.box((0, -.14, -.05), (.18, .14, .08), "leather", bevel=.025, taper=(.85, .8))  # toe
        s.box((0, .07, .06), (.19, .19, .2), "leather_dark", bevel=.02)        # shaft
        s.box((0, .07, .17), (.21, .21, .05), "leather", bevel=.01)            # cuff
        s.box((0, -.02, -.095), (.21, .44, .025), "wood_dark")                  # sole
        s.box((0, .12, -.105), (.16, .12, .03), "wood_dark")                    # heel
        s.box((0, -.03, .01), (.2, .03, .05), "iron_dark")                      # strap
        s.box((0, -.046, .01), (.05, .01, .05), "brass_dark")                   # buckle
    m.piece("Boot", build=b)
    return m


# ---------------------------------------------------------------------------
# Physics props (centred on their collider box)
def barrel() -> Model:
    """Explosive barrel, 0.7 x 1.0 x 0.7. Body colour = entity colour."""
    m = prop("barrel")

    def b(s):
        s.cylinder((0, 0, -.25), .31, .5, "tint", sides=10, radius_top=.35)
        s.cylinder((0, 0, .25), .35, .5, "tint", sides=10, radius_top=.31)
        for z, r in ((-.41, .325), (.41, .325), (-.17, .345), (.17, .345)):
            s.cylinder((0, 0, z), r + .012, .05, "iron_dark", sides=10)
        s.cylinder((0, 0, 0), .356, .1, "fire_yellow", sides=10)                # warning band
        s.cylinder((0, 0, .5), .3, .02, "tint_shade", sides=10)                 # lid
        s.cylinder((.12, -.05, .515), .035, .03, "iron", sides=6)               # bung
        # A painted flame mark on the front (no letters).
        s.prism([(-.07, .1), (0, .26), (.07, .1), (0, .05)], 0, .006, "fire_orange", offset=(0, -.352, 0), rotation=(90, 0, 0))
    m.piece("Barrel", build=b)
    return m


def crate() -> Model:
    """Wooden crate, a 1 m cube (scaled to each crate). Wood colour = entity colour."""
    m = prop("crate")

    def c(s):
        s.box((0, 0, 0), (.94, .94, .94), "tint_shade")
        h = .5
        for sx in (-1, 1):                     # corner posts
            for sy in (-1, 1):
                s.box((sx * .45, sy * .45, 0), (.1, .1, 1.0), "tint", bevel=.01)
        for z in (-1, 1):                      # top and bottom rims
            for sy in (-1, 1):
                s.box((0, sy * .45, z * .45), (.92, .1, .1), "tint", bevel=.01)
                s.box((sy * .45, 0, z * .45), (.1, .92, .1), "tint", bevel=.01)
        for face in range(4):                   # planks and a diagonal brace per side
            a = face * math.pi / 2
            nx, ny = math.cos(a), math.sin(a)
            for i in range(3):
                t = (i - 1) * .27
                s.box((nx * .475 - ny * t, ny * .475 + nx * t, 0), (.25 if abs(ny) > .5 else .02, .02 if abs(ny) > .5 else .25, .78),
                      "tint_light" if i % 2 else "tint")
            s.box((nx * .5, ny * .5, 0), (.1 if abs(ny) > .5 else .03, .03 if abs(ny) > .5 else .1, 1.15), "tint",
                  rotation=(0, 45, 0) if abs(ny) > .5 else (45, 0, 0))
        for sx in (-1, 1):                     # top planks
            s.box((sx * .22, 0, .48), (.4, .82, .03), "tint_light" if sx > 0 else "tint")
        for sx in (-1, 1):
            for sy in (-1, 1):
                s.box((sx * .5, sy * .5, .5), (.06, .06, .06), "iron_dark")    # corner caps
    m.piece("Crate", build=c)
    return m


def rock() -> Model:
    """Throwable rock, about 0.5 x 0.4 x 0.45."""
    m = prop("rock")

    def r(s):
        s.rock((0, 0, 0), .24, "tint", subdiv=1, jitter=.2, seed=3, scale=(1.05, .95, .82))
        s.rock((.1, -.06, .1), .1, "tint_light", subdiv=1, jitter=.25, seed=5)
        s.rock((-.1, .05, -.08), .12, "tint_shade", subdiv=1, jitter=.25, seed=7)
    m.piece("Rock", build=r)
    return m


def pebble() -> Model:
    m = prop("pebble")

    def r(s):
        s.rock((0, 0, 0), .12, "tint", subdiv=1, jitter=.18, seed=11, scale=(1, .9, .6))
    m.piece("Pebble", build=r)
    return m


def brazier() -> Model:
    """Iron brazier with glowing coals, 1 m tall (centred)."""
    m = prop("brazier")

    def b(s):
        for i in range(3):
            a = i * math.tau / 3
            s.cylinder((math.cos(a) * .2, math.sin(a) * .2, -.2), .025, .62, "iron_dark", sides=5,
                       rotation=(-math.sin(a) * 18, math.cos(a) * 18, 0))
            s.box((math.cos(a) * .3, math.sin(a) * .3, -.49), (.08, .08, .03), "iron_dark")
        s.torus((0, 0, -.15), .16, .015, "iron", sides=8)
        s.cylinder((0, 0, .2), .26, .2, "iron", sides=10, radius_top=.36)       # bowl
        s.torus((0, 0, .3), .36, .025, "iron_dark", sides=10)
        for i in range(8):
            a = i * math.tau / 8
            s.cone((math.cos(a) * .36, math.sin(a) * .36, .34), .025, .07, "iron_dark", sides=4)

    def coals(s):
        for i in range(9):
            a = i * 2.4
            r = .08 + (i % 3) * .07
            s.rock((math.cos(a) * r, math.sin(a) * r, .3), .07, "ember" if i % 3 else "fire_orange", subdiv=0, jitter=.3, seed=i)
    m.piece("Brazier", build=b)
    m.piece("Coals", build=coals, glow=True)
    return m


def pedestal() -> Model:
    """Relic pedestal, 0.8 x 1.0 x 0.8 (centred). Stone colour = entity colour."""
    m = prop("pedestal")

    def p(s):
        s.box((0, 0, -.44), (.8, .8, .12), "tint_shade", bevel=.02)
        s.box((0, 0, -.33), (.66, .66, .1), "tint", bevel=.02)
        s.cylinder((0, 0, .02), .2, .62, "tint", sides=8, radius_top=.17)
        for i in range(8):
            a = i * math.tau / 8
            s.box((math.cos(a) * .19, math.sin(a) * .19, .02), (.03, .03, .6), "tint_shade", rotation=(0, 0, math.degrees(a)))
        s.box((0, 0, .38), (.6, .6, .08), "tint", bevel=.02)
        s.box((0, 0, .46), (.7, .7, .08), "tint_light", bevel=.02)
        # A carved eye on the front: the church's only image of God.
        s.sphere((0, -.2, .15), 1, "gold", segments=8, rings=4, scale=(.1, .02, .05))
        s.sphere((0, -.215, .15), 1, "black", segments=6, rings=4, scale=(.035, .01, .035))
    m.piece("Pedestal", build=p)
    return m


def relic() -> Model:
    """Golden relic idol (item), about 0.3 x 0.4 x 0.3."""
    m = prop("relic", "item")

    def r(s):
        s.cylinder((0, 0, -.17), .14, .06, "brass_dark", sides=8)
        s.cylinder((0, 0, -.06), .07, .18, "gold", sides=8, radius_top=.1)
        s.ico((0, 0, .08), .11, "gold", subdiv=1, scale=(1, .9, 1.1))
        for i in range(7):  # a sunburst crown of points around the head
            a = math.pi * (i / 6)
            s.cone((math.cos(a) * .1, .02, .1 + math.sin(a) * .1), .022, .09, "gold", sides=4,
                   rotation=(0, 90 - math.degrees(a), 0))

    def eye(s):
        s.sphere((0, -.1, .09), 1, "eye_cyan", segments=8, rings=5, scale=(.055, .02, .04))

    m.piece("Relic", build=r)
    m.piece("Relic eye", build=eye, glow=True)
    return m


def cheese() -> Model:
    """Cheese wedge with holes, about 0.34 x 0.18 x 0.26."""
    m = prop("cheese", "item")

    def c(s):
        # A wedge: a triangle extruded upward, rind on the round back edge.
        s.prism([(-.17, -.13), (.17, -.13), (-.17, .13)], -.09, .09, "fire_yellow", side="fire_yellow")
        s.prism([(-.175, -.135), (-.15, -.135), (-.15, .135), (-.175, .135)], -.092, .092, "toad_spot")
        for x, z, r in ((-.08, .0, .035), (.05, -.03, .025), (-.02, .04, .02)):
            s.sphere((x, -.132, z), 1, "brass_dark", segments=6, rings=3, scale=(r, .006, r))
        s.sphere((-.08, -.04, .092), 1, "brass_dark", segments=6, rings=3, scale=(.03, .03, .005))
    m.piece("Cheese", build=c)
    return m


def scripture() -> Model:
    """Closed holy book, about 0.3 x 0.08 x 0.38."""
    m = prop("scripture", "item")

    def b(s):
        s.box((0, 0, 0), (.3, .38, .08), "cloth_red_dark", bevel=.008)
        s.box((.015, 0, 0), (.28, .36, .06), "parchment")
        s.box((-.15, 0, 0), (.02, .38, .085), "cloth_red")
        for y in (-.17, .17):
            s.box((0, y, .04), (.3, .03, .006), "gold")
        s.sphere((0, 0, .041), 1, "gold", segments=8, rings=3, scale=(.06, .035, .004))
        s.sphere((0, 0, .044), 1, "black", segments=6, rings=3, scale=(.018, .018, .003))
    m.piece("Book", build=b)
    return m


def key() -> Model:
    """A big iron/brass key lying flat, ring toward -X (matches the old key)."""
    m = prop("key", "item")

    def k(s):
        # Unity mirrors X and Blender's -Y is Unity's +Z: ring toward Unity -X, teeth toward +Z.
        s.torus((.17, 0, 0), .12, .035, "tint", sides=10, tube_sides=4)
        s.box((-.08, 0, 0), (.42, .06, .06), "tint", bevel=.01)
        s.box((.03, 0, 0), (.06, .09, .09), "tint_shade", bevel=.01)
        s.box((-.2, -.055, 0), (.05, .12, .06), "tint")
        s.box((-.29, -.065, 0), (.05, .14, .06), "tint")
        s.box((-.29, -.1, 0), (.12, .04, .06), "tint_shade")
    m.piece("Key", build=k)
    return m


def fire_pot() -> Model:
    """A clay fire pot with a glowing mouth, about 0.3 tall."""
    m = prop("fire_pot", "item")

    def p(s):
        s.cylinder((0, 0, -.09), .08, .06, "copper", sides=8, radius_top=.13)
        s.ico((0, 0, -.01), .14, "copper", subdiv=1, scale=(1, 1, .8))
        s.cylinder((0, 0, .1), .07, .08, "copper", sides=8, radius_top=.06)
        s.torus((0, 0, .14), .065, .015, "wood_dark", sides=8)
        s.torus((0, 0, .0), .136, .01, "charcoal", sides=10)

    def glow(s):
        s.cylinder((0, 0, .145), .05, .02, "fire_yellow", sides=8)
    m.piece("Pot", build=p)
    m.piece("Embers", build=glow, glow=True)
    return m


# ---------------------------------------------------------------------------
# Doors and levers: the moving part is a separate model so Unity can hinge it
def door_frame() -> Model:
    """Stone frame around a 2.6 x 2.6 x 0.25 door opening (centred on the opening)."""
    m = prop("door_frame")

    def f(s):
        for sx in (-1, 1):
            for i in range(5):
                z = -1.15 + i * .58
                s.box((sx * 1.42, 0, z), (.24, .42, .56), "stone_light" if (i + (sx > 0)) % 2 else "stone", bevel=.02)
        s.box((0, 0, 1.43), (3.2, .44, .26), "stone", bevel=.03)
        s.box((0, 0, 1.43), (.5, .5, .34), "stone_light", bevel=.03)    # keystone
    m.piece("Frame", build=f)
    return m


def door_panel() -> Model:
    """Wooden door leaf 2.6 x 2.6 x 0.25 with its origin on the hinge (left edge, +X... see note).

    The panel spans x from 0 to +2.6 in Unity terms: in Blender that is -X, because
    Unity mirrors X. Unity parents it to a hinge node on the door's left edge."""
    m = prop("door_panel")

    def p(s):
        w, h = 2.6, 2.6
        for i in range(5):
            x = -(i + .5) * w / 5
            s.box((x, 0, 0), (w / 5 - .02, .22, h - .04), "tint" if i % 2 else "tint_light", bevel=.01)
        for z in (-.85, .85):
            s.box((-w / 2, -.12, z), (w - .1, .04, .2), "iron_dark")
            for i in range(6):
                s.sphere((-.2 - i * .44, -.145, z), 1, "iron", segments=6, rings=3, scale=(.03, .02, .03))
        s.box((-w / 2, -.12, 0), (w - .3, .035, .12), "tint_shade", rotation=(0, 40, 0))
        s.torus((-w + .3, -.15, 0), .09, .02, "iron", sides=8, rotation=(90, 0, 0))   # ring handle
        s.box((-w + .3, -.13, .1), (.08, .03, .08), "iron_dark")
    m.piece("Panel", build=p, symmetric=False)
    return m


def gate_panel() -> Model:
    """Iron bar gate leaf (cage gate), 4.2 x 2.8 x 0.25, origin on the hinge like door_panel."""
    m = prop("gate_panel")

    def p(s):
        w, h = 4.2, 2.8
        for i in range(11):
            x = -(i + .5) * w / 11
            s.cylinder((x, 0, 0), .035, h - .1, "tint", sides=6)
            s.cone((x, 0, h / 2 - .02), .05, .12, "tint_shade", sides=4)
        for z in (-1.15, 0, 1.15):
            s.box((-w / 2, 0, z), (w - .05, .08, .1), "tint_shade")
        s.box((-w + .1, 0, 0), (.14, .14, h), "iron_dark")
        s.box((-.07, 0, 0), (.14, .14, h), "iron_dark")
    m.piece("Gate", build=p, symmetric=False)
    return m


def lever_base() -> Model:
    """Stone base of a floor lever (0.48 x 0.26 x 0.45), origin at its bottom centre."""
    m = prop("lever_base")

    def b(s):
        s.box((0, 0, .1), (.48, .45, .2), "stone", bevel=.03)
        s.box((0, 0, .22), (.36, .3, .06), "stone_dark", bevel=.01)
        for sx in (-1, 1):
            s.box((sx * .12, 0, .3), (.05, .14, .14), "iron_dark", bevel=.01)
    m.piece("Base", build=b)
    return m


def lever_handle() -> Model:
    """Lever arm 1 m long along +Z from its pivot (origin), grip coloured with the tint."""
    m = prop("lever_handle")

    def h(s):
        s.cylinder((0, 0, .45), .035, .9, "iron", sides=6)
        s.cylinder((0, 0, 0), .07, .2, "iron_dark", sides=8, rotation=(0, 90, 0))
        s.ico((0, 0, .95), .1, "tint", subdiv=1, scale=(1, 1, 1.3))
    m.piece("Handle", build=h, symmetric=False)
    return m


# ---------------------------------------------------------------------------
# Decor
def torch() -> Model:
    """Wall torch 0.6 m tall (centred); the flame sits above the top."""
    m = prop("torch")

    def t(s):
        s.cylinder((0, 0, -.05), .025, .5, "wood", sides=6, radius_top=.035)
        s.cylinder((0, 0, .22), .05, .08, "cloth_brown", sides=7, radius_top=.06)
        s.cylinder((0, 0, -.12), .04, .04, "iron_dark", sides=6)
        s.box((0, .06, -.12), (.03, .12, .03), "iron_dark")
        s.box((0, .12, -.12), (.12, .02, .14), "iron_dark")

    def coal(s):
        s.cylinder((0, 0, .27), .055, .03, "fire_orange", sides=7)
    m.piece("Torch", build=t, symmetric=False)
    m.piece("Glow", build=coal, glow=True)
    return m


def spike() -> Model:
    """A sharpened stake for spike pits, 0.7 m tall (centred)."""
    m = prop("spike")

    def p(s):
        s.cylinder((0, 0, -.12), .05, .46, "wood_dark", sides=5, radius_top=.04)
        s.cone((0, 0, .23), .04, .24, "bone", sides=5)
        s.torus((0, 0, -.3), .052, .012, "straw", sides=6, tube_sides=3)
    m.piece("Spike", build=p)
    return m


def tent() -> Model:
    """A goblin tent, 1 x 1 x 1 (centred; scaled to its footprint)."""
    m = prop("tent")

    def t(s):
        s.poly([(-.5, -.5, -.5), (0, -.5, .5), (0, .5, .5), (-.5, .5, -.5)], "tint", thickness=.02)
        s.poly([(.5, .5, -.5), (0, .5, .5), (0, -.5, .5), (.5, -.5, -.5)], "tint_shade", thickness=.02)
        s.poly([(-.5, .5, -.5), (0, .5, .5), (.5, .5, -.5)], "tint_deep", thickness=.02)
        s.poly([(.5, -.5, -.5), (0, -.5, .5), (-.04, -.5, -.5)], "tint_light", thickness=.02)
        s.cylinder((0, -.52, .02), .02, 1.06, "wood_dark", sides=5)
        s.cylinder((0, .52, .02), .02, 1.06, "wood_dark", sides=5)
        s.cylinder((0, 0, .5), .018, 1.1, "wood_dark", sides=5, rotation=(90, 0, 0))
        s.rock((.3, -.6, -.47), .06, "stone", subdiv=0, seed=2)
    m.piece("Tent", build=t, symmetric=False)
    return m


def rune_stone() -> Model:
    """One standing stone of a Return Circle (no writing), 0.2 x 0.3 x 0.2, origin at the base."""
    m = prop("rune_stone")

    def r(s):
        s.box((0, 0, .14), (.17, .13, .28), "stone_light", bevel=.02, taper=(.75, .8))
        s.sphere((0, -.066, .17), 1, "eye_green", segments=6, rings=3, scale=(.035, .005, .02))
    m.piece("Stone", build=r)
    return m


def circle() -> Model:
    """A Return Circle floor disc, diameter 1 (scaled), 0.04 thick (centred)."""
    m = prop("circle")

    def c(s):
        s.cylinder((0, 0, -.005), .5, .03, "stone_dark", sides=24)
        s.torus((0, 0, .012), .46, .02, "tint", sides=24, tube_sides=4, rotation=(0, 0, 0))
        s.torus((0, 0, .012), .3, .015, "tint", sides=20, tube_sides=4)
        for i in range(12):
            a = i * math.tau / 12
            s.box((math.cos(a) * .38, math.sin(a) * .38, .012), (.1, .02, .01), "tint_light", rotation=(0, 0, math.degrees(a)))
    m.piece("Circle", build=c)
    return m


def cultist() -> Model:
    """A masked cultist in a hooded robe, standing (1.75 m), origin at the feet.
    Used for the fallen cultist in Rite of Return (laid down in Unity)."""
    m = prop("cultist", "remains")
    m.height = 1.75
    body = [
        limb((0, 0, .08), (0, 0, 1.25), .34, .17),                                   # flared robe
        ellipsoid((0, 0, .06), (.36, .3, .08), k=.05),                                # hem on the floor
        ellipsoid((0, 0, 1.3), (.25, .16, .12), k=.1),                                # shoulders
        sphere((0, .0, 1.52), .15, k=.04, color="tint_shade"),                       # head in the hood
        limb((0, .04, 1.5), (0, .1, 1.74), .17, .03, k=.04, color="tint_shade"),     # pointed hood
        ellipsoid((0, -.115, 1.5), (.1, .05, .12), k=.01, color="bone", bias=.03),   # mask
        limb((.24, -.02, 1.27), (.16, -.2, 1.0), .085, .075, k=.05, mirror=True),     # sleeves
        limb((.12, -.24, 1.0), (.0, -.27, 1.0), .07, .065, k=.03, mirror=True),
        sphere((.03, -.31, 1.0), .05, k=.02, color="skin", mirror=True),             # folded hands
        box((0, 0, .92), (.8, .7, .06), op="paint", color="tint_deep"),             # sash
        box((0, -.2, .45), (.12, .2, .9), op="paint", color="tint_light"),           # front panel
        ellipsoid((.042, -.16, 1.535), (.022, .04, .018), op="sub", color="black", mirror=True),   # eye holes
    ]
    m.piece("Cultist", body, faces=1400, voxel=.02)
    return m


RECIPES = {
    "staff": staff, "boot": boot, "barrel": barrel, "crate": crate, "rock": rock, "pebble": pebble,
    "brazier": brazier, "pedestal": pedestal, "relic": relic, "cheese": cheese, "scripture": scripture,
    "key": key, "fire_pot": fire_pot, "door_frame": door_frame, "door_panel": door_panel, "gate_panel": gate_panel,
    "lever_base": lever_base, "lever_handle": lever_handle, "torch": torch, "spike": spike, "tent": tent,
    "rune_stone": rune_stone, "circle": circle, "cultist": cultist,
}
