"""Monster recipes for the demos. Each function returns a bwkit.Model.

Heights follow SpeciesCatalog.cs (Height = top of the model, metres).
Characters face -Y (Unity +Z); their left is +X.
"""
from bwkit import (Model, Piece, V, box, cylinder, ellipsoid, flat_limb, limb, sphere, torus)


def humanoid_bones(m: Model, hip, chest, neck, head_top, shoulder, elbow, wrist, hand_end,
                   hip_joint, knee, ankle, toe):
    """Shared biped skeleton. All points are for the left (+X) side."""
    hz, cz, nz, tz = hip[2], chest[2], neck[2], head_top[2]
    spine = V(hip[0], (hip[1] + chest[1]) / 2, (hz + cz) / 2)
    m.bone("Hips", hip, spine)
    m.bone("Spine", spine, chest, "Hips")
    m.bone("Chest", chest, neck, "Spine")
    head_base = V(neck[0], neck[1] + (head_top[1] - neck[1]) * .25, nz + (tz - nz) * .22)
    m.bone("Neck", neck, head_base, "Chest")
    m.bone("Head", head_base, head_top, "Neck")
    m.bone("UpperArm.L", shoulder, elbow, "Chest", mirror=True)
    m.bone("Forearm.L", elbow, wrist, "UpperArm.L", mirror=True)
    m.bone("Hand.L", wrist, hand_end, "Forearm.L", mirror=True)
    m.bone("Thigh.L", hip_joint, knee, "Hips", mirror=True)
    m.bone("Shin.L", knee, ankle, "Thigh.L", mirror=True)
    m.bone("Foot.L", ankle, toe, "Shin.L", mirror=True)


def eyes(m: Model, center, radii, color="eye_yellow", pupil=(.35, .55), slit=False, bone="Head", rotation=None):
    """Glowing eyes (+ dark pupils) as rigid parts of the head."""
    cx, cy, cz = center
    rx, ry, rz = radii

    def glow(mesh):
        for side in (1, -1):
            mesh.sphere((side * cx, cy, cz), 1.0, color, segments=8, rings=5, scale=(rx, ry, rz), rotation=rotation)

    def pupils(mesh):
        for side in (1, -1):
            if slit:
                mesh.box((side * cx, cy - ry * .82, cz), (rx * .28, ry * .3, rz * 1.5), "black")
            else:
                mesh.sphere((side * cx, cy - ry * .72, cz), 1.0, "black", segments=6, rings=4,
                            scale=(rx * pupil[0], ry * .35, rz * pupil[1]))

    m.piece("Eyes", build=glow, bone=bone, glow=True)
    m.piece("Pupils", build=pupils, bone=bone)


# ---------------------------------------------------------------------------
def goblin() -> Model:
    m = Model("goblin")
    m.height = 1.5
    m.preview_tint = (140, 191, 51)
    humanoid_bones(m, hip=(0, .02, .62), chest=(0, .0, .92), neck=(0, -.05, 1.03), head_top=(0, -.12, 1.47),
                   shoulder=(.2, 0, .97), elbow=(.3, -.02, .76), wrist=(.34, -.08, .56), hand_end=(.35, -.12, .45),
                   hip_joint=(.11, .02, .6), knee=(.14, -.03, .34), ankle=(.14, .03, .09), toe=(.14, -.16, .03))
    m.bone("Ear.L", (.17, -.07, 1.27), (.44, -.02, 1.38), "Head", mirror=True)
    k = .05
    body = [
        ellipsoid((0, .02, .64), (.16, .13, .11), bone="Hips"),
        ellipsoid((0, -.05, .76), (.17, .15, .15), bone="Spine", k=k, color="tint_light"),
        ellipsoid((0, .02, .93), (.2, .13, .12), bone="Chest", k=k),
        limb((0, -.02, .98), (0, -.08, 1.1), .06, .055, bone="Neck", k=.04),
        ellipsoid((0, -.1, 1.22), (.19, .17, .16), bone="Head", k=.05),
        ellipsoid((0, -.05, 1.28), (.17, .15, .14), bone="Head", k=.04),
        ellipsoid((0, -.19, 1.12), (.14, .1, .065), bone="Head", k=.04),                       # jaw
        limb((0, -.26, 1.22), (0, -.43, 1.13), .05, .018, bone="Head", k=.025, color="tint_shade"),  # hooked nose
        ellipsoid((0, -.235, 1.285), (.15, .05, .03), rotation=(-12, 0, 0), bone="Head", k=.025, color="tint_shade"),  # brow
        flat_limb((.15, -.07, 1.27), (.45, -.01, 1.39), .07, .012, flatten=.3, normal=(0, 1, .2), bone="Ear.L", k=.03, mirror=True),
        flat_limb((.17, -.08, 1.27), (.4, -.03, 1.36), .045, .01, flatten=.3, normal=(0, 1, .2), bone="Ear.L", k=.0,
                  color="pink_dark", bias=.004, mirror=True, op="paint"),
        # arms
        limb((.19, 0, .97), (.3, -.02, .76), .058, .045, bone="UpperArm.L", k=.04, mirror=True),
        limb((.3, -.02, .76), (.34, -.08, .57), .045, .05, bone="Forearm.L", k=.025, mirror=True),
        ellipsoid((.35, -.11, .5), (.065, .07, .075), bone="Hand.L", k=.025, mirror=True, color="tint_shade"),
        limb((.31, -.15, .52), (.3, -.2, .47), .022, .016, bone="Hand.L", k=.012, mirror=True, color="tint_shade"),  # thumb
        # legs
        limb((.11, .02, .6), (.14, -.03, .34), .075, .055, bone="Thigh.L", k=.04, mirror=True),
        limb((.14, -.03, .34), (.14, .03, .1), .052, .045, bone="Shin.L", k=.03, mirror=True),
        ellipsoid((.14, -.06, .05), (.075, .15, .052), bone="Foot.L", k=.03, mirror=True, color="tint_shade"),
        # loincloth flap and belt
        box((0, -.13, .53), (.2, .035, .22), rotation=(12, 0, 0), round=.012, bone="Hips", k=.012, color="leather"),
        box((0, .14, .55), (.18, .035, .18), rotation=(-10, 0, 0), round=.012, bone="Hips", k=.012, color="leather"),
        box((0, 0, .675), (1, 1, .06), op="paint", color="leather_dark"),
        sphere((0, -.17, .675), .035, op="paint", color="brass_dark"),
        # mouth: a wide carved grin with teeth
        ellipsoid((0, -.27, 1.115), (.11, .05, .014), rotation=(8, 0, 0), op="sub", k=.006, color="mouth"),
        limb((.05, -.272, 1.125), (.05, -.276, 1.095), .016, .003, bone="Head", color="bone", bias=.01, mirror=True),
        limb((.02, -.266, 1.105), (.02, -.27, 1.13), .012, .003, bone="Head", color="bone", bias=.01, mirror=True),
        # knuckle/knee shading and toenails
        ellipsoid((.14, -.2, .045), (.05, .03, .03), op="paint", color="claw", mirror=True),
    ]
    m.piece("Body", body, faces=2400, voxel=.012, tau=.045)
    eyes(m, (.085, -.255, 1.245), (.045, .028, .032), "eye_yellow", slit=True)

    def club(mesh):
        mesh.cylinder((-.35, -.13, .58), .028, .44, "wood_dark", sides=6, rotation=(20, 0, 0))
        mesh.box((-.35, -.215, .82), (.17, .2, .22), "wood", rotation=(20, 0, 12), bevel=.02)
        mesh.box((-.35, -.215, .84), (.18, .21, .05), "iron_dark", rotation=(20, 0, 12))
        for dx, dz in ((-.08, .86), (.08, .8), (0, .93)):
            mesh.cone((-.35 + dx, -.22, dz), .02, .06, "iron", sides=4, rotation=(0, 90 if dx > 0 else -90, 0) if dx else (0, 0, 0))

    m.piece("Club", build=club, bone="Hand.R")
    m.socket("Socket.Hand", "Hand.R", (-.35, -.13, .48))
    return m


RECIPES = {
    "goblin": goblin,
}
