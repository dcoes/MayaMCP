# ---------------------------------------------------------------------------
# face_weight_solver.py            *** PYTHON — paste into the PYTHON tab ***
#
# THE SOLVER ("the translator"). Converts an animation keyed on facial BONES
# into weight keys on the FacePoses node, frame by frame, via non-negative
# least squares against the character's pose library.
#
# NO EXTERNAL DEPENDENCIES — pure Python, no numpy/scipy. Runs in Maya's stock
# Python as-is; nothing to install. (This is the approved Python exception to
# the MEL convention: NNLS is impractical in MEL. Everything else is MEL.)
#
# Dual role:
#   1. One-time bulk conversion of the existing bone-keyed animations.
#   2. Standing export step: animators keep animating on the control rig
#      exactly as today; run the solver before export.
#
# Usage (Script Editor, PYTHON tab, after pasting this file):
#   SolveFaceWeights()                          # playback range -> 'Facial' layer
#   SolveFaceWeights(start=1, end=240)
#   SolveFaceWeights(anim_layer=None)           # key directly, no anim layer
#   CutFacialBoneKeys()                         # OPTIONAL, destructive: remove
#                                               # facial bone curves after the
#                                               # result is validated, so the
#                                               # live preview network can
#                                               # drive the bones instead.
#
# Requirements: the character's pose library captured in-scene (FacePoseLib
# nodes + Neutral) and the FacePoses node — both from face_pose_tools.mel.
#
# Built-in validation: per-frame reconstruction error is reported, worst
# frames listed. High error on a frame = the animation used a shape the 29
# poses don't span (a missing-pose detector, not a solver bug).
# ---------------------------------------------------------------------------

import math
import maya.cmds as cmds

CHANNELS = ["translateX", "translateY", "translateZ",
            "rotateX", "rotateY", "rotateZ"]

FACE_BONES = [
    "Jaw", "UpperLip_Mid", "LowerLip_Mid", "Brow0_Mid",
    "Eye_L", "Eye_R",
    "UpperEyelid_L", "UpperEyelid_R",
    "LowerEyelid_L", "LowerEyelid_R",
    "Brow0_L", "Brow0_R",
    "Brow1_L", "Brow1_R",
    "CheekBone_L", "CheekBone_R",
    "LipCorner_L", "LipCorner_R",
]

POSE_NAMES = [
    "JawOpen",
    "Smile_L", "Smile_R", "Frown_L", "Frown_R",
    "MouthWide_L", "MouthWide_R", "MouthNarrow_L", "MouthNarrow_R",
    "UpperLipRaise", "LowerLipDown",
    "BrowUp_L", "BrowUp_R", "BrowSad_L", "BrowSad_R",
    "BrowAngry_L", "BrowAngry_R",
    "Blink_L", "Blink_R", "EyeWide_L", "EyeWide_R",
    "Squint_L", "Squint_R",
    "EyesUp", "EyesDown", "EyesLeft", "EyesRight",
    "CheekRaise_L", "CheekRaise_R",
]


# ---------------------------------------------------------------------------
# Pose library access (same in-scene storage face_pose_tools.mel writes).
# ---------------------------------------------------------------------------
def _load_pose_data(name):
    node = "FPose_" + name
    if not cmds.objExists(node):
        return {}
    if not cmds.attributeQuery("poseData", node=node, exists=True):
        return {}
    data = cmds.getAttr(node + ".poseData") or ""
    out = {}
    for line in data.splitlines():
        tok = line.split()
        if len(tok) == 7:
            out[tok[0]] = [float(v) for v in tok[1:]]
    return out


def _face_bones():
    """Core bones + this character's extras (AddFacialBone), same set
    FacePoseBones() returns in face_pose_tools.mel. Without the extras, ear
    or muzzle keys on a character like the goblin would be ignored."""
    bones = list(FACE_BONES)
    if cmds.objExists("FacePoseLib") and \
            cmds.attributeQuery("extraBones", node="FacePoseLib", exists=True):
        for b in (cmds.getAttr("FacePoseLib.extraBones") or "").split():
            if b and b not in bones:
                bones.append(b)
    return bones


def _resolve_bone(bone):
    """Full unique DAG path for a bone name. If more than one object shares
    the name, getAttr on the short name silently operates on ALL of them —
    this fails loudly instead and names every duplicate."""
    matches = cmds.ls(bone, long=True) or []
    if not matches:
        raise RuntimeError("_resolve_bone: '%s' does not exist in this scene." % bone)
    if len(matches) > 1:
        raise RuntimeError(
            "_resolve_bone: '%s' is NOT UNIQUE — %d objects share this name:\n  %s\n"
            "Rename one in the Outliner so facial bone names are unique, then retry."
            % (bone, len(matches), "\n  ".join(matches)))
    return matches[0]


# ---------------------------------------------------------------------------
# Tiny linear-algebra helpers (plain lists — no numpy).
# ---------------------------------------------------------------------------
def _solve_spd(G, rhs):
    """Solve G x = rhs for a small symmetric positive-definite G via
    Gaussian elimination with partial pivoting. G is a list of lists
    (n x n), rhs a list (n). Returns x as a list."""
    n = len(rhs)
    M = [list(G[i]) + [rhs[i]] for i in range(n)]
    for col in range(n):
        piv = max(range(col, n), key=lambda r: abs(M[r][col]))
        if abs(M[piv][col]) < 1e-12:
            M[piv][col] += 1e-12
        if piv != col:
            M[col], M[piv] = M[piv], M[col]
        inv = 1.0 / M[col][col]
        for r in range(n):
            if r == col:
                continue
            f = M[r][col] * inv
            if f != 0.0:
                for c in range(col, n + 1):
                    M[r][c] -= f * M[col][c]
    return [M[i][n] / M[i][i] for i in range(n)]


def _nnls(cols, AtA, b, ridge, tol=1e-9, max_iter=None):
    """Non-negative least squares (Lawson-Hanson active set), pure Python.

    cols  : list of columns; each column is a list of floats (len m) = A[:, j]
    AtA   : precomputed Gram matrix A^T A (n x n) — constant across frames.
    b     : target vector (len m)
    ridge : L2 regularization added to the Gram diagonal in each solve.
    Returns x (len n), all >= 0.
    """
    n = len(cols)
    if max_iter is None:
        max_iter = 3 * n

    Atb = [sum(cols[j][i] * b[i] for i in range(len(b))) for j in range(n)]

    x = [0.0] * n
    passive = [False] * n

    def gradient(xv):
        w = list(Atb)
        for j in range(n):
            s = 0.0
            row = AtA[j]
            for k in range(n):
                if xv[k] != 0.0:
                    s += row[k] * xv[k]
            s += ridge * xv[j]
            w[j] -= s
        return w

    it = 0
    while True:
        w = gradient(x)
        best_j, best_w = -1, tol
        for j in range(n):
            if not passive[j] and w[j] > best_w:
                best_w, best_j = w[j], j
        if best_j < 0:
            break

        passive[best_j] = True

        while True:
            P = [j for j in range(n) if passive[j]]
            G = [[AtA[p][q] + (ridge if p == q else 0.0) for q in P] for p in P]
            rhs = [Atb[p] for p in P]
            z_small = _solve_spd(G, rhs)
            z = [0.0] * n
            for idx, p in enumerate(P):
                z[p] = z_small[idx]

            if all(z[p] > tol for p in P):
                x = z
                break

            alpha = None
            for p in P:
                if z[p] <= tol:
                    denom = x[p] - z[p]
                    if denom > 1e-15:
                        r = x[p] / denom
                        if alpha is None or r < alpha:
                            alpha = r
            if alpha is None:
                x = z
                break
            x = [x[k] + alpha * (z[k] - x[k]) for k in range(n)]
            for p in P:
                if x[p] <= tol:
                    passive[p] = False
                    x[p] = 0.0

            it += 1
            if it > max_iter:
                break

        it += 1
        if it > max_iter:
            break

    return [v if v > 0.0 else 0.0 for v in x]


# ---------------------------------------------------------------------------
# The solver.
# ---------------------------------------------------------------------------
def SolveFaceWeights(start=None, end=None, step=1,
                     anim_layer="Facial",
                     ridge=0.001,
                     zero_epsilon=0.005,
                     report_worst=10):
    """Solve facial bone animation into FacePoses weight keys.

    start/end   : frame range (defaults to playback range)
    anim_layer  : name of the anim layer to key onto (None = key directly)
    ridge       : L2 regularization; keeps weight curves clean/sparse
    zero_epsilon: solved weights below this are snapped to exactly 0
    """
    neutral = _load_pose_data("Neutral")
    if not neutral:
        raise RuntimeError("No Neutral in the pose library. Capture the library first (face_pose_tools.mel).")
    if not cmds.objExists("FacePoses"):
        raise RuntimeError("No FacePoses node. Run CreateFacePosesNode() first.")

    poses = []
    for p in POSE_NAMES:
        d = _load_pose_data(p)
        if not d:
            cmds.warning("SolveFaceWeights: pose '%s' has no library data — excluded from the basis." % p)
            continue
        if not cmds.attributeQuery(p, node="FacePoses", exists=True):
            cmds.warning("SolveFaceWeights: FacePoses is missing attribute '%s' — excluded." % p)
            continue
        poses.append((p, d))
    if not poses:
        raise RuntimeError("Pose library is empty — nothing to solve against.")

    bones = [b for b in _face_bones() if b in neutral and cmds.objExists(b)]
    bone_paths = {b: _resolve_bone(b) for b in bones}
    rows = [(b, c) for b in bones for c in range(6)]

    n_poses = len(poses)
    raw_cols = []
    for (pname, pdata) in poses:
        col = []
        for (b, c) in rows:
            col.append(pdata[b][c] if b in pdata else 0.0)
        raw_cols.append(col)

    row_scale = []
    for i in range(len(rows)):
        mx = max(abs(raw_cols[j][i]) for j in range(n_poses))
        row_scale.append(mx)
    live = [i for i in range(len(rows)) if row_scale[i] > 1e-9]
    live_rows = [rows[i] for i in live]
    scale = [row_scale[i] for i in live]

    cols = []
    for j in range(n_poses):
        cols.append([raw_cols[j][i] / row_scale[i] for i in live])

    AtA = [[0.0] * n_poses for _ in range(n_poses)]
    m = len(live)
    for a in range(n_poses):
        ca = cols[a]
        for b_ in range(a, n_poses):
            cb = cols[b_]
            s = 0.0
            for i in range(m):
                s += ca[i] * cb[i]
            AtA[a][b_] = s
            AtA[b_][a] = s

    if start is None:
        start = int(cmds.playbackOptions(q=True, min=True))
    if end is None:
        end = int(cmds.playbackOptions(q=True, max=True))

    attrs = ["FacePoses.%s" % p for p, _ in poses]
    if anim_layer:
        if not cmds.animLayer(anim_layer, q=True, exists=True):
            cmds.animLayer(anim_layer)
        cmds.animLayer(anim_layer, e=True, attribute=attrs)

    original_time = cmds.currentTime(q=True)
    errors = []
    over_one = 0
    frames = list(range(int(start), int(end) + 1, int(step)))

    for f in frames:
        cmds.currentTime(f, edit=True)

        b_vec = []
        for k, (bone, c) in enumerate(live_rows):
            val = cmds.getAttr("%s.%s" % (bone_paths[bone], CHANNELS[c])) - neutral[bone][c]
            b_vec.append(val / scale[k])

        x = _nnls(cols, AtA, b_vec, ridge)
        x = [0.0 if v < zero_epsilon else v for v in x]
        if any(v > 1.0 + 1e-6 for v in x):
            over_one += 1

        for j, (pname, _) in enumerate(poses):
            kw = dict(attribute=pname, t=f, v=float(x[j]))
            if anim_layer:
                kw["animLayer"] = anim_layer
            cmds.setKeyframe("FacePoses", **kw)

        num = 0.0
        for i in range(m):
            recon = 0.0
            for j in range(n_poses):
                if x[j] != 0.0:
                    recon += cols[j][i] * x[j]
            d = recon - b_vec[i]
            num += d * d
        den = max(math.sqrt(sum(v * v for v in b_vec)), 1e-9)
        errors.append((math.sqrt(num) / den, f))

    cmds.currentTime(original_time, edit=True)

    errs = [e for e, _ in errors]
    mean_e = sum(errs) / len(errs) if errs else 0.0
    max_e = max(errs) if errs else 0.0
    print("\nSolveFaceWeights: %d frames (%d-%d) -> %d weight curves%s."
          % (len(frames), start, end, len(poses),
             (" on anim layer '%s'" % anim_layer) if anim_layer else ""))
    print("Reconstruction error  mean %.3f   max %.3f" % (mean_e, max_e))
    if over_one:
        cmds.warning("%d frames solved a weight above 1.0 — the FacePoses attr "
                     "limit clamps these. The animation exceeds an authored "
                     "pose extreme; consider re-authoring that pose bigger." % over_one)
    worst = sorted(errors, reverse=True)[:report_worst]
    if worst and worst[0][0] > 0.05:
        print("Worst frames (high error = shape the 29 poses don't span):")
        for e, f in worst:
            if e > 0.05:
                print("  frame %d   error %.3f" % (f, e))
    print("Review visually: ConnectFacePoses() on a preview skeleton, scrub, compare.\n")


# ---------------------------------------------------------------------------
# OPTIONAL, destructive: delete the facial bone animation curves once the
# solved weights are validated. Lets the live preview network claim the bones
# in the same scene. Work on a copy of the file.
# ---------------------------------------------------------------------------
def CutFacialBoneKeys():
    removed = 0
    for b in _face_bones():
        if not cmds.objExists(b):
            continue
        full = _resolve_bone(b)
        for ch in CHANNELS:
            curves = cmds.listConnections("%s.%s" % (full, ch),
                                          s=True, d=False, type="animCurve") or []
            if curves:
                cmds.delete(curves)
                removed += len(curves)
    print("CutFacialBoneKeys: removed %d facial bone anim curves." % removed)


print("face_weight_solver.py loaded (pure Python, no numpy).  "
      "SolveFaceWeights()  |  SolveFaceWeights(start, end)  |  CutFacialBoneKeys()")
