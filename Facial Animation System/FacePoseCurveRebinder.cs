// ---------------------------------------------------------------------------
// FacePoseCurveRebinder.cs      *** MUST live in an "Editor" folder ***
//                               e.g. Assets/Editor/FacePoseCurveRebinder.cs
//
// THE MISSING LINK between the FBX and FacePoseDriver.
//
// Unity imports FBX animated custom properties as curves bound to the
// Animator component ("Animator.JawOpen") - NOT to MonoBehaviour fields.
// FacePoseDriver's 29 public floats therefore never receive values, and the
// face stays neutral even though the data is right there in the clip.
//
// This AssetPostprocessor runs automatically on every model/animation import
// and rebinds each pose-named float curve to the FacePoseDriver component on
// the FacePoses node, so the animation system writes the driver's fields
// directly. Works with Animator, Playables, and Animancer alike (they all
// evaluate the same clip bindings).
//
// Install once, forget. After adding this file, RIGHT-CLICK > REIMPORT every
// already-imported character/animation FBX - the rebind happens at import
// time, so existing imports must be refreshed once.
//
// Success is visible in two places:
//   - Console: "FacePoseCurveRebinder: rebound N pose curves in 'ClipName'..."
//   - Animation window: rows change from "Animator.Jaw Open" to
//     "FacePoses : Face Pose Driver.Jaw Open"
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

public class FacePoseCurveRebinder : AssetPostprocessor
{
    // Must match FacePoseDriver's field names / Maya attribute names exactly.
    static readonly HashSet<string> PoseNames = new HashSet<string>
    {
        // Mouth (11)
        "JawOpen",
        "Smile_L", "Smile_R",
        "Frown_L", "Frown_R",
        "MouthWide_L", "MouthWide_R",
        "MouthNarrow_L", "MouthNarrow_R",
        "UpperLipRaise", "LowerLipDown",
        // Brows (6)
        "BrowUp_L", "BrowUp_R",
        "BrowSad_L", "BrowSad_R",
        "BrowAngry_L", "BrowAngry_R",
        // Eyes (10)
        "Blink_L", "Blink_R",
        "EyeWide_L", "EyeWide_R",
        "Squint_L", "Squint_R",
        "EyesUp", "EyesDown", "EyesLeft", "EyesRight",
        // Cheeks (2)
        "CheekRaise_L", "CheekRaise_R"
    };

    // The facial bones the driver owns at runtime. Stale transform curves on
    // these (from bone-keyed animation left in the FBX) are stripped from
    // every imported clip: the driver overwrites these bones every frame
    // anyway, and leftover curves make edit-mode preview show misleading
    // motion that no driver setting can affect.
    //
    // Built from every pose library JSON in the project (any character, any
    // bone names), once per editor session. After adding a NEW character's
    // JSON, reimport its FBXs (or restart Unity) so its bones are included.
    // The legacy VR list is kept as a fallback when no JSON is found.
    static readonly string[] LegacyFaceBones =
    {
        "Jaw", "UpperLip_Mid", "LowerLip_Mid", "Brow0_Mid",
        "Eye_L", "Eye_R",
        "UpperEyelid_L", "UpperEyelid_R",
        "LowerEyelid_L", "LowerEyelid_R",
        "Brow0_L", "Brow0_R",
        "Brow1_L", "Brow1_R",
        "CheekBone_L", "CheekBone_R",
        "LipCorner_L", "LipCorner_R"
    };

    static HashSet<string> _faceBones;

    static HashSet<string> FaceBones
    {
        get
        {
            if (_faceBones == null) _faceBones = CollectFaceBones();
            return _faceBones;
        }
    }

    static HashSet<string> CollectFaceBones()
    {
        var set = new HashSet<string>();
        int libraries = 0;
        foreach (string file in Directory.GetFiles(Application.dataPath, "*.json", SearchOption.AllDirectories))
        {
            string text;
            try { text = File.ReadAllText(file); }
            catch (IOException) { continue; }
            if (!text.Contains("\"neutral\"") || !text.Contains("\"poses\"")) continue;

            FacePoseDriver.PoseLibrary lib = null;
            try { lib = JsonUtility.FromJson<FacePoseDriver.PoseLibrary>(text); }
            catch (ArgumentException) { continue; }
            if (lib == null || lib.neutral == null) continue;

            foreach (FacePoseDriver.BoneTR b in lib.neutral)
                if (!string.IsNullOrEmpty(b.bone)) set.Add(b.bone);
            libraries++;
        }
        if (libraries == 0)
            set.UnionWith(LegacyFaceBones);
        return set;
    }

    // Used only if the imported file doesn't contain a FacePoses node to
    // measure the real path from (shouldn't happen when exporting from Root).
    const string FallbackPath = "Root/FacePoses";

    // Required when a postprocessor modifies import results: declares an
    // importer version so Unity's import-consistency check doesn't flag
    // "Importer(FBXImporter) generated inconsistent result". Bump this number
    // if the pose list ever changes - it forces a clean reimport everywhere.
    public override uint GetVersion()
    {
        return 3;   // v3: strip list comes from the project's pose library JSONs
    }

    void OnPostprocessAnimation(GameObject root, AnimationClip clip)
    {
        // Compute the FacePoses path from the actual imported hierarchy -
        // never hardcode, in case the node depth ever changes.
        Transform facePoses = FindDeep(root.transform, "FacePoses");
        string targetPath = (facePoses != null)
            ? AnimationUtility.CalculateTransformPath(facePoses, root.transform)
            : FallbackPath;

        EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
        int moved = 0;

        foreach (EditorCurveBinding b in bindings)
        {
            // Match on the RAW property name ("JawOpen"), tolerating any
            // "Something.JawOpen" prefixing an importer version might add.
            // (The Animation window's "Jaw Open" with a space is display-only.)
            string name = b.propertyName;
            int dot = name.LastIndexOf('.');
            if (dot >= 0) name = name.Substring(dot + 1);

            if (!PoseNames.Contains(name)) continue;
            if (b.type == typeof(FacePoseDriver)) continue;   // already rebound

            AnimationCurve curve = AnimationUtility.GetEditorCurve(clip, b);
            if (curve == null) continue;

            EditorCurveBinding rebound = new EditorCurveBinding
            {
                path = targetPath,
                type = typeof(FacePoseDriver),
                propertyName = name
            };
            AnimationUtility.SetEditorCurve(clip, rebound, curve);
            AnimationUtility.SetEditorCurve(clip, b, null);   // remove original
            moved++;
        }

        // --- Pass 2: strip stale facial BONE transform curves. The driver
        //     owns these bones every frame; leftover bone keys only mislead
        //     (especially in edit-mode Animation preview, where the driver
        //     doesn't run and the raw curves play as-is).
        int stripped = 0;
        foreach (EditorCurveBinding b in AnimationUtility.GetCurveBindings(clip))
        {
            if (b.type != typeof(Transform)) continue;
            string leaf = b.path;
            int slash = leaf.LastIndexOf('/');
            if (slash >= 0) leaf = leaf.Substring(slash + 1);
            if (!FaceBones.Contains(leaf)) continue;

            AnimationUtility.SetEditorCurve(clip, b, null);
            stripped++;
        }

        if (moved > 0 || stripped > 0)
        {
            Debug.Log(string.Format(
                "FacePoseCurveRebinder: '{0}' - rebound {1} pose curves -> '{2}', stripped {3} stale facial bone curves  ({4})",
                clip.name, moved, targetPath, stripped, assetPath));
            if (moved > 0 && facePoses == null)
                Debug.LogWarning(
                    "FacePoseCurveRebinder: no 'FacePoses' node found in " + assetPath
                    + " - used fallback path '" + FallbackPath + "'. If your hierarchy"
                    + " differs, the curves won't bind; export the FBX from the top Root.");
        }
    }

    static Transform FindDeep(Transform t, string name)
    {
        if (t.name == name) return t;
        for (int i = 0; i < t.childCount; i++)
        {
            Transform hit = FindDeep(t.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
