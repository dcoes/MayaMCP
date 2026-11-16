# Facial Pose Pipeline: Handoff for Claude Code

This file hands the facial retargeting pipeline to Claude Code so it can be extended from a repo. Read it top to bottom before touching any script. Sections 3 and 4 are the rules. Section 9 is the work queue.

Owner's goal, in one line: turn a studio-specific facial pipeline into a **rig-agnostic tool**, then prove it in a demo reel where one human facial performance drives a human, a goblin and a canine (Advanced Skeleton sample rig: Cody or Snook).

---

## 1. What the system does

Faces are animated on a normal control rig in Maya. The pipeline turns each character's face into **29 named poses** (bone deltas from a neutral). Any facial animation can then be reduced to **29 weight curves**, and those curves play on **any** character that has its own library of the same 29 poses.

```
 MAYA (per character, once)                          MAYA (per animation)
 ┌───────────────────────────────┐                   ┌────────────────────────────────┐
 │ SaveNeutral()                 │                   │ animate on control rig         │
 │ sculpt pose N, CapturePose(N) │                   │ SolveFaceWeights()  (Python)   │
 │ ... x29  (or mirror)          │                   │  -> 29 weight keys on FacePoses│
 │ ExportFaceFiles()             │                   │ bake + export FBX from Root    │
 └──────────────┬────────────────┘                   └───────────────┬────────────────┘
                │ <Name>.flib (Maya master)                          │ animation FBX
                │ <Name>.json (Unity dictionary)                     │ (weight curves)
                ▼                                                    ▼
 UNITY ┌────────────────────────────────────────────────────────────────────────────┐
       │ FacePoseCurveRebinder (import time): curves "Animator.JawOpen" ->          │
       │   FacePoseDriver.JawOpen field; strips stale facial bone curves            │
       │ FacePoseDriver (LateUpdate): bone = neutral + Σ weight × delta             │
       └────────────────────────────────────────────────────────────────────────────┘
```

**The contract:** the 29 pose *names* are universal. The *bones inside* each pose are per-character data. A dog can answer `BrowUp_L` with an ear instead of a brow. That is the whole trick, and it is what the reel shows.

---

## 2. File inventory

**Current layout (2026-09-28, supersedes the rest of this section where they differ):**

| File (project root unless noted) | Role |
|---|---|
| `face_pose_tools.mel` | The WHOLE facial pipeline in one script with its window built in (`FacePoseUI()`, opens on paste): capture, mirror, audit, cleanup, .flib/.json, live preview (was `connect_face_poses.mel`), transfer (was `transfer_pose_library.mel`), blendshape heads, `.fwc` weight-curve files. Universal: per-character joint list `FacePoseLib.faceBones`, blendshape nodes `FacePoseLib.faceShapes`, mirror axis `FacePoseLib.mirrorAxis`. |
| `face_weight_solver.py` | Solver, still separate (the window's "Locate Solver..." loads it). Reads the same per-character channels, blendshape targets included. |
| `FacePoseCurveRebinder.cs` | Strip list now comes from every pose library JSON in the project. `GetVersion()` = 3. |
| `ARKit_FacePoses.txt` | Starter preset: the 29 poses as ARKit blendshape mixes (`ApplyShapePreset`). |
| `AutoRigger_CC/skeleton_matcher_tools.mel` | Body pipeline in one script with its window (`SkeletonMatcherUI()`): name matchers + skeleton template replace the three hardcoded CC4 scripts. |
| `AutoRigger_CC/VR_StandardPlayer.sktpl` | `#place` rules for the VR skeleton; joint lines are filled by one Capture Template click on the reference. |
| `NameMatchers/` | Project copies of AS-format matchers: VR (completed: Spine2, twists, Jaw, HeadEnd), CC4, and an identity AdvancedSkeleton one. |
| `retired/`, `AutoRigger_CC/retired/` | The old separate scripts, kept for reference only. Don't paste them after the new ones. |

Everything above was run in Maya 2023 standalone (mayapy) against HumanMale/GoblinMale: all section 10.1 mirror oracles pass, solver recovers known weights on bones and blendshapes, template -> locators -> skeleton reproduces the reference exactly, rename/restore and copy-skin-by-name work. The two windows themselves can't open in batch mode, so they are unverified until someone presses the buttons.

**Previous layout (history):**

| File | Role | Notes |
|---|---|---|
| `face_pose_tools.mel` | Capture, storage, preview, **mirror**, audit, cleanup, .flib/.json export, extras | Revised this round (see 2.1) |
| `face_weight_solver.py` | `SolveFaceWeights()`, `CutFacialBoneKeys()`: bone animation to 29 weight keys (pure Python NNLS) | Revised this round (see 2.1). Loading it only defines functions and prints one line. |
| `face_pose_ui.mel` | Window over all of the above | Now merged into `face_pose_tools.mel` |
| `connect_face_poses.mel` | Live preview network (plusMinusAverage/multDoubleLinear) | Now merged. Its `DisconnectFacePoses` errored (Maya deletes the empty set itself); fixed in the merge. |
| `transfer_pose_library.mel` | Pre-fill a new character's poses from another's (no control rig case) | Now merged; duplicated `int $bi` line fixed. |
| `FacePoseDriver.cs` | Unity runtime | Unchanged |
| `FacePoseCurveRebinder.cs` | Unity Editor AssetPostprocessor (must live in an `Editor` folder) | Was `GetVersion()` = 2 |
| `HumanMale.flib/.json`, `GoblinMale.flib/.json` | Real libraries; use as test fixtures | |
| `animator_guide.docx` | Artist-facing guide, **owner's own wording** | Not edited. Its `MirrorPose(2)` / `MirrorPose(18)` lines still work. |
| `unity_implementation.docx` | Engineer-facing Unity setup | |

### 2.1 What changed in the latest revision

1. **Mirror moved into `face_pose_tools.mel`** and rebuilt. The old `MirrorPose` used a fixed flip mask (negate tx, ry, rz). The captured HumanMale library proves that mask wrong for this skeleton: every eyelid and eye pair flips **all** translations and **keeps** rotations (e.g. `Blink_L` UpperEyelid `0 -0.049 0.050 18.07 0 0` vs `Blink_R` `0 0.049 -0.050 18.07 0 0`), and LipCorner keeps `ry` (`JawOpen` has `ry 30` on both sides). The old mask would have produced a wrong Blink_R. The new one measures frames (section 7).
   - New: `MirrorPoseLeftToRight(N)`, `MirrorPoseRightToLeft(N)`, `MirrorReport()`, center-pose symmetrize.
   - `MirrorPose(N)` kept with its old meaning (mirrors from the side of the pose picked), so the animator guide stays correct. On a center pose it now errors with the two directional commands to use.
   - `MirrorFaceBoneName()` kept as an alias of the new `FaceSwapSide()`.
2. **Extra bones now survive a .flib round trip.** Before, `SaveLibraryFile` wrote only Neutral + poses, so `LoadLibraryFile` into a fresh scene lost `FacePoseLib.extraBones`. Capture, audit and ResetFace then silently ignored the extras. Now the .flib starts with `#extraBones Ear_L Ear_R ...` and the loader restores it. Old loaders ignore that line (anything before the first `=` block is discarded), and old .flib files without it load exactly as before. **Existing .flib files saved before this change (e.g. GoblinMale.flib) don't have the line:** after loading one, re-run `AddFacialBone` for each extra, or load and re-save once.
3. **Solver now includes extras.** `face_weight_solver.py` used only the 18 hardcoded names, so ear keys on a goblin animation were ignored when solving. New `_face_bones()` adds `FacePoseLib.extraBones`, used by `SolveFaceWeights` and `CutFacialBoneKeys`.
4. **Window revised:** proc names are plain PascalCase (`FacePoseUI...`, no prefix), its mirror duplicate is gone (it calls the tools'), and it gained Preview Selected (with weight slider), Reset Face and Cut Facial Bone Keys (with confirm). The pose list still works when no tools are loaded.

---

## 3. Non-negotiable conventions

1. **Proc names:** plain PascalCase, no prefix (`CapturePose`, not `fpCapturePose`). Owner's explicit choice; collision risk is accepted.
2. **One script per pipeline, window inside it:** `face_pose_tools.mel` (face) and `AutoRigger_CC/skeleton_matcher_tools.mel` (body). No `source` dependencies. Each ends with its help print, then opens its window, so paste-and-execute works. Never split the UI back out into its own file. **Plain ASCII only** in every script (no em dashes, arrows, curly quotes).
3. **Shared helpers stay identical:** the name matcher block (`ReadNameMatcher`, `MatcherBuildName`, `MatcherName`, `MatcherLookup`) is a byte-identical copy in both MEL scripts; change both together. The solver's `_face_joints()`/`_face_shapes()` must match `FaceJointList()`/`FaceShapeChannels()` in the MEL.
4. **Uniqueness guard:** any code that touches a bone by short name goes through `ResolveFaceBone()` (MEL) / `_resolve_bone()` (Python). Non-unique names otherwise cause silent multi-object edits.
5. **Forward slashes** in every MEL path. Backslashes get eaten as escapes and writes fail silently.
6. **No numpy/scipy.** Stock Maya Python doesn't ship them. `face_weight_solver.py` is the only non-MEL Maya script.
7. **Independence:** every step is separately callable. Never auto-run one step inside another. (The existing exception, `CaptureAllAndExport`, is an explicit one-swoop convenience the owner asked for.)
8. **Artist-facing UX:** pose selection by number (`CapturePose(2)`), file dialogs over typed paths, no Unity authoring for animators.
9. **Surgical edits:** change existing scripts and docs in place. **Never regenerate `animator_guide.docx`**; insert additions into it in the owner's wording.
10. **Pose-by-pose capture is the primary workflow.** The timeline route (`CaptureAllPoseFrames`) is optional.
11. **Writing style for docs:** no em dashes; plain, down-to-earth tone.
12. If the pose list ever changes: update every copy (tools, UI, connect, transfer, solver `POSE_NAMES`, driver fields, rebinder `PoseNames`) and bump `FacePoseCurveRebinder.GetVersion()`.

---

## 4. Hard-won lessons (do not relearn these)

- **Axis conversion is live, per frame**, in the driver (`None` / `MirrorX` / `MirrorZ`). Find the mode empirically once per FBX export path with JawOpen (rotation direction), EyesLeft (gaze), Smile_L (corner moves out on the character's left). Axis settings cannot fix a *no motion* problem; diagnose binding first.
- **`translationScale = 0.01`** is the standard Maya cm to Unity m fix, set on the prefab.
- **Rotation composition:** `neutralLocalRotation * MayaXYZQuat(convertedDeltaSum)`, `deltaBeforeNeutral = false`. Euler addition in Unity scrambles axes because Unity's neutral is ZXY-decomposed with jointOrient baked in.
- **Wrong character moves, no error:** standardized bone names mean a mis-assigned `skeletonRoot` resolves perfectly on another character. The driver warns in `Awake` if `skeletonRoot.root != transform.root`. Always check this in multi-character scenes (this is exactly the reel setup).
- **`AddFacialBone` before `SaveNeutral`.** Extras missing from the neutral silently skip their deltas.
- **Unity makes no AnimationClip** unless at least one standard TRS property is keyed somewhere in the FBX.
- **The rebinder must be reimported once** after install/version bump (right-click > Reimport on existing FBXs).
- **Stray-bone contamination** (a bone drifting into unrelated poses) is found with `FindBoneInPoses` / Unity's "Debug: Log Pose Contents" and removed with `RemoveBoneFromPoses`. JSON-only fix: re-export JSON, swap the TextAsset, FBX untouched.
- **A single sign mask can't mirror a real face rig.** Different bones on the same skeleton use different conventions (section 2.1). Measure frames.
- **Worst-case failure of any component is a neutral face.** Body animation is unaffected. Keep it that way.

---

## 5. Data formats

### 5.1 `.flib` (Maya master, MEL-parseable plain text)

```
#extraBones Ear_L Ear_R           <- optional; only when extras are registered
=Neutral
<bone> tx ty tz rx ry rz          <- absolute channel values at rest
...
=<PoseName>
<bone> dtx dty dtz drx dry drz    <- deltas from Neutral; only bones that moved
...
```
Values are Maya channel space: translate in the **parent** frame, rotate in degrees (joint rotate channels, XYZ order, on top of jointOrient). Capture epsilons: 0.0005 units translate, 0.01 degrees rotate (`CapturePoseCurrent`).

### 5.2 In-scene storage (from `face_pose_tools.mel`)

- Group `FacePoseLib` (empty transform, `EnsureFacePoseLib`), with string attribute `extraBones` (space-separated).
- One **transform** per pose under it: `FPose_<PoseName>` with string attribute `poseData` (same line format as the .flib, no `=` header). `FPose_Neutral` holds absolute values. Written only through `StoreFacePoseData(name, data)`.
- `FacePoses` transform under `Root` (`CreateFacePosesNode`): 29 keyable double attributes, 0-1, named exactly like the poses. This is what animation keys, what the FBX carries, and what the rebinder binds. With no `Root` it is created at world with a warning.

### 5.3 `.json` (Unity)

```json
{ "character": "GoblinMale",
  "neutral": [ {"bone":"Jaw","t":[x,y,z],"r":[x,y,z]}, ... ],
  "poses":   [ {"name":"JawOpen","bones":[{"bone":"Jaw","t":[..],"r":[..]}]}, ... ] }
```
`character` is the base name picked in the `ExportFaceFiles` save dialog. The driver builds its bone set from `neutral`, so **the Unity side already accepts any bone names.** Poses bind to fields by name via reflection.

---

## 6. Hardcoded assumptions (what still ties it to one skeleton)

| Assumption | Where | Effect on a new rig (e.g. Cody/Snook) |
|---|---|---|
| 18 core bone names | `CoreFaceBones()` (tools, UI), `FacePoseBones()` copies (connect, transfer), solver `FACE_BONES`, rebinder `FaceBones` | **Not a blocker.** Missing core bones are skipped: `SaveNeutral` warns once per missing name, capture/preview/reset skip them. Register the dog's face joints with `AddFacialBone` and everything works. Cost: 18 warnings per `SaveNeutral`, and `AddFacialBone` refuses a name that matches a core name. |
| 29 pose names | every MEL file, solver `POSE_NAMES`, driver fields, rebinder `PoseNames` | Fine for the reel. The 29 are the contract. |
| Proportion measurements use `LipCorner_L/R`, `Eye_L/R` | `transfer_pose_library.mel` | Falls back to unscaled translations with a warning. |
| Region guess by name (`*Lip*`, `*Eye*`, ...) | `FaceBoneRegion()` in transfer | Muzzle/ear bones get no scaling. |
| Side tokens | Mirror: `FaceSideRules()` table (handles `_L/_R`, `_l/_r`, `L_/R_`, `Left/Right`). Audit CROSS-SIDE: `gmatch "*_L"/"*_R"` on **core** bones only | Audit's cross-side check does nothing for a rig whose bones are all extras. |
| Rebinder strips curves only for the 18 human bone names | `FacePoseCurveRebinder.FaceBones` | Stale bone curves on a new rig's face bones aren't stripped (harmless in play mode, misleading in edit-mode preview). |
| `Root` as top node / fallback path `Root/FacePoses` | `CreateFacePosesNode`, rebinder | AS rigs have a different top group. Parent `FacePoses` under the exported skeleton root. |
| Mirror plane = world YZ | `FaceMirrorMap()` | Rigs built facing another axis need a setting. |

---

## 7. The mirror (in `face_pose_tools.mel`)

### 7.1 Commands

```mel
MirrorPoseLeftToRight(N);   // N can be either pose of a pair; builds the R pose from the L pose
MirrorPoseRightToLeft(N);   // builds the L pose from the R pose
MirrorPoseLeftToRight(1);   // center pose (JawOpen): rebuild its R-side bones from its L-side bones
MirrorPose(N);              // old command: mirrors FROM the side of the pose you pick
MirrorReport();             // prints each bone pair's map + neutral symmetry check
```
Pose pairing uses the same side rules as bones, so `EyesLeft` pairs with `EyesRight` through the `Left/Right` rule. Center poses are **symmetrized in place**: from-side and center bones stay as captured, to-side bones are rebuilt from their partners. Output goes through `StoreFacePoseData`, so it works while a control rig drives the bones. The printout matches CapturePose's (`[N bones: ...]`).

### 7.2 Math (why it is rig-agnostic)

For source bone S and target bone D (a center bone is its own target):

- **Translate** lives in the parent frame. Let `pS_i`, `pD_j` be unit axis rows of each bone's **parent** world matrix, and `M` flip world X. Then
  `C_t[i][j] = (pS_i · M) · pD_j`, and `delta'_j = Σ_i delta_i · C_t[i][j]`.
- **Rotate** channels act about the joint's own axes. Rotations are pseudovectors, so a reflection reverses their sense:
  `C_r[i][j] = -(aS_i · M) · aD_j`, with `a` = unit rows of each bone's **own** world matrix.
- A map is "clean" when each row has one ±1 entry: the normal symmetric-rig case. Otherwise it is flagged OBLIQUE; translations stay exact, rotations become a small-angle approximation.

Sanity check of the formula against the data: world-aligned frames give tx flips, rx keeps, ry/rz flip; behavior-mirrored frames (Maya's mirrorJoint "behavior") give all translations flip, all rotations keep. The second is exactly what the human eyelids show.

**Frames are measured from the live scene, so run mirror with the face at neutral.** It warns if any facial bone is off neutral by more than 0.01.

### 7.3 Known limits / follow-ups

- Mirror plane is hardcoded (world X flips).
- The mirrored pose is not re-audited automatically. Run `AuditPoseLibrary()` after.
- Live-scene symmetrize (mirror the current sculpt on the bones before capture) is not built. Only possible on a bare skeleton, since rig-driven channels can't be set.
- A mirrored pose copies strays faithfully (a stray `LipCorner_R` in `Smile_L` becomes a stray `LipCorner_L` in `Smile_R`). Clean the source first.

---

## 8. The window (`face_pose_ui.mel`)

Paste it into the MEL tab; the window opens. **Scripts > Load Pipeline Scripts...** sources `face_pose_tools.mel`, `connect_face_poses.mel`, `transfer_pose_library.mel` and exec's `face_weight_solver.py` from one folder (remembered in `optionVar FacePoseUIScriptDir`).

- Every button calls the same typed command through `evalEcho` (Python calls are printed as `python("...")`), so the Script Editor shows exactly what ran.
- Buttons check `exists <proc>` first and warn if the script isn't loaded.
- Pose list: row 0 = Neutral, rows 1-29 = the pose table, with ok/-- status. Multi-select drives Mirror and "Remove Joint From Selected Poses".
- Sections: Poses (save neutral, capture, mirror both ways, mirror report, preview at weight, reset face), Library, Facial Bones, Timeline Capture, Animation/Preview/Unity (create FacePoses node, solve, connect/disconnect preview, cut facial bone keys), Transfer, Scripts.
- Mirror's overwrite confirmation uses the tools' `FaceSideOf`/`FaceSwapSide` through `eval`, so the UI file never calls a proc it doesn't define.

**Unverified in Maya:** none of this has run yet. Bracket balance was checked mechanically. First session should press every button once (section 10.1).

---

## 9. Work queue (in order)

**Status 2026-09-28:** Phase 1 was validated in Maya standalone (not the window). Phase 2 is done: `faceBones`, `SetFaceBonesFromSelection`, `#faceBones` in .flib, the cross-side audit with `FaceSideOf`, and rebinder option A. Phase 3 is done: landmark pairs (`SetTransferLandmarks`), region by nearest landmark, and name mapping via AS matchers (`TransferPoseLibraryMapped`). The body pipeline generalization started with the owner's go-ahead (`skeleton_matcher_tools.mel`). New: the blendshape -> bones workflow (`AddFaceShapesFromSelection`, `ApplyShapePreset`, solve, `ExportFaceWeightCurves` / `ImportFaceWeightCurves`). Still open: the mirror note in `animator_guide.docx`, pressing every button in both windows once, and a real CC4 export through the body tool.

### Phase 1: Validate in Maya (on HumanMale)
Run section 10.1. Fix anything that fails. Then add a short mirror note to `animator_guide.docx` in the owner's voice (the two directional commands and "face at rest").

### Phase 2: Per-character bone set (quality of life, no longer a blocker)
- Add a `faceBones` string attribute on `FacePoseLib`. `CoreFaceBones()` returns it when present, else the 18-name default (old characters unchanged). This removes the 18 warnings and lets `AddFacialBone` accept any name.
- New procs `SetFaceBonesFromSelection()` / `ListFaceBones()`; wire into the UI's Facial Bones section.
- Persist it in the .flib the same way as `#extraBones` (e.g. `#faceBones ...`).
- Update every copy (tools, UI, connect, transfer) and the solver's `_face_bones()`.
- Audit CROSS-SIDE: use `FaceSideOf` on all bones in `FacePoseBones()` instead of `gmatch` on core names, with a flag to keep extras exempt as today.
- Rebinder: the strip list can't be hardcoded any more. Option A: strip bones listed in any pose-library `*.json` in the project (scan once per import, cache). Option B: a small ScriptableObject registry. Discuss with the owner; bump `GetVersion()`.

### Phase 3: Transfer generalization
- Replace `LipCorner`/`Eye` measurement pairs with user-picked landmark pairs stored on `FacePoseLib`, and region assignment per bone instead of name globbing.
- Bone-name map for transfer between rigs with different naming. Reuse the `*_NameMatcher.txt` format already in the project (`sideLeft`, `sideRight`, `sideBeforeName`, `sideUnderScore`, then `canonical=target` lines).
- Check the duplicated `int $bi` line (section 2).

### Phase 4 (optional): Pose set as data
Only if the owner wants poses beyond the 29. The driver binds curves to 29 **named fields** by reflection; changing that means changing both Unity scripts together. Verify how Unity binds animation curves to non-field targets before designing it. Not needed for the reel.

### Later: skeleton pipeline generalization (separate effort)
Do not start without the owner. Orientation must come from a **reference skeleton's actual jointOrients** (aim-constraint orientation was tried and abandoned). The generic path is `CaptureTemplate(rootJoint)` recording name/parent/jointOrient/rotateOrder/side per joint, replacing the hardcoded 116-bone table. The project's stored `build_skeleton_from_locators.mel` is **stale** (old aim-constraint version); get the latest from the owner.

---

## 10. Testing

### 10.1 Mirror oracles from real data (HumanMale)

These captured pairs are near-exact mirrors, so mirroring one side should reproduce the other. **Save the library first** (`ExportFaceFiles()`) because mirror overwrites, then reload it after testing.

| Command | Expected result (compare with the captured pose) |
|---|---|
| `MirrorReport();` | Eye / UpperEyelid / LowerEyelid pairs: all `t` flip, all `r` keep. LipCorner: `tx` flips, `ty`/`tz` keep, `ry` keeps. Brow0/Brow1/CheekBone: `tx` flips. No "neutral asymmetric" on the human except tiny values. |
| `MirrorPoseLeftToRight(18);` (Blink) | `UpperEyelid_R ≈ 0 0.0491 -0.0502 18.07 0 0`, `LowerEyelid_R ≈ 0.0116 -0.0442 -0.0235 -12.67 0 -7.92` |
| `MirrorPoseLeftToRight(6);` (MouthWide) | `LipCorner_R ≈ -0.553 0.145 -0.453 0 0 0`; `UpperLip_Mid`/`LowerLip_Mid` unchanged |
| `MirrorPoseLeftToRight(26);` (EyesLeft -> EyesRight) | `Eye_L ry ≈ -35.62`, `Eye_R ry ≈ +35.62` |
| `MirrorPose(2);` then `MirrorPose(3);` | Same as the directional commands (L->R, then R->L) |
| `MirrorPose(1);` | Errors, naming `MirrorPoseLeftToRight(1)` / `MirrorPoseRightToLeft(1)` |
| `MirrorPoseLeftToRight(1);` (JawOpen) | Jaw, lips and LipCorner_L unchanged; LipCorner_R rebuilt ≈ `0 -1.144 -0.431 0 30 0` |

If `MirrorReport` disagrees with the first row, the frames the code measures are not the ones the data implies. Investigate before trusting any mirror. Smile_L/Smile_R on the human were sculpted differently on purpose; they are **not** an oracle.

### 10.2 .flib extras round trip
Goblin scene: `AddFacialBone("Ear_L"); AddFacialBone("Ear_R");` then `SaveLibraryFile(...)`. File's first line is `#extraBones Ear_L Ear_R`. New scene, `LoadLibraryFile(...)`, `ListFacialBones();` lists both ears.

### 10.3 GoblinMale observations (expected, not bugs)
- Neutral LipCorner is asymmetric (`tx` 6.136 vs -6.482). `MirrorReport` will print "neutral asymmetric"; that is the model.
- The library has asymmetries the mirror can clean up: `BrowUp_R` moves `Brow0_Mid` but `BrowUp_L` doesn't; `UpperLipRaise` contains a `LipCorner_R` stray; `Blink_L` has a small upper-lid rotation that `Blink_R` lacks.

### 10.4 Unity checks for any new character
1. `FacePoseDriver` log: "`<name>` ready: N bones, 29 poses", with N = that character's bone count.
2. `skeletonRoot` points at **this** character's root (the Awake warning).
3. Axis mode + `translationScale` via JawOpen / EyesLeft / Smile_L.
4. "Debug: Log Pose Contents" shows no stray bones.

---

## 11. Reel plan: human -> goblin -> canine

1. Pick Cody or Snook from the Advanced Skeleton sample rigs. Inspect: face joints (not blendshapes) deform the mesh, joint names are unique, side suffixes.
2. Build a **bare deformation skeleton scene** for preview. Preview, ResetFace and the live network need channels not driven by the AS rig. Capture and mirror work on the rigged scene.
3. Register the dog's face joints with `AddFacialBone` (window: select joints, "Add as Extra Bones"), then `SaveNeutral()` with the face at rest. Expect 18 "bone not found" warnings until Phase 2.
4. Author the 29 poses on the AS face controls and capture. Interpret, don't copy: `BrowUp` can perk the ears, `BrowAngry` can pin them back, `MouthNarrow` can push the muzzle. Mirror halves the sided work; check `MirrorReport()` on the dog first.
5. Save the library right away (`ExportFaceFiles`); the extras now travel in the .flib.
6. Solve one human performance once (`SolveFaceWeights()` in the human scene). The same weight curves play on all three characters.
7. Reel shot: three characters side by side in Unity, each with its own JSON and **its own `skeletonRoot`**, one clip.

---

## 12. Quick reference: typed commands

```mel
// library
SaveNeutral();  CapturePose(N);  PoseTable();  LibraryStatus();  AuditPoseLibrary();
ExportFaceFiles();  LoadLibraryFileUI();  SaveLibraryFile("C:/p/x.flib");  LoadLibraryFile("C:/p/x.flib");
// preview (bare skeleton)
PreviewPose(N, 1.0);  ResetFace();
// mirror (face at rest)
MirrorPoseLeftToRight(N);  MirrorPoseRightToLeft(N);  MirrorPose(N);  MirrorReport();
// bones
AddFacialBone("Ear_L");  RemoveFacialBone("Ear_L");  ListFacialBones();
FindBoneInPoses("LipCorner_R");  RemoveBoneFromPose(26, "LipCorner_R");  RemoveBoneFromPoses("LipCorner_R", {12,13,14});
// timeline route
CaptureAllPoseFrames(0);  CaptureAllAndExport(0);
// unity + preview
CreateFacePosesNode();  ExportPoseLibrary("C:/p/x.json", "Name");  ConnectFacePoses();  DisconnectFacePoses();
// transfer (no control rig)
ExportPoseLibraryRaw("C:/path/src.raw");  TransferPoseLibrary("C:/path/src.raw", 0);
// window
FacePoseUI();
```
```python
SolveFaceWeights()                       # PYTHON tab; playback range -> 'Facial' anim layer
SolveFaceWeights(start=1, end=240, anim_layer=None)
CutFacialBoneKeys()                      # destructive, on a copy of the file
```
