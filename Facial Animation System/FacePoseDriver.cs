// ---------------------------------------------------------------------------
// FacePoseDriver.cs
//
// Runtime facial pose solver. Sits on the imported "FacePoses" GameObject.
// The imported weight curves drive the 29 public float fields below (via
// FacePoseCurveRebinder.cs at import time); every LateUpdate each facial
// bone is rebuilt from the character's pose-library JSON as:
//
//     localPosition = neutralPos + Convert( Σ weight × translateDelta )
//     localRotation = neutralRot * MayaXYZQuat( Convert( Σ weight × rotateDelta ) )
//
// Rotation deltas are composed as a quaternion offset in Maya's XYZ rotate
// order on top of the imported neutral rotation — NOT added euler-to-euler.
// (The skeleton bakes orientation into jointOrients, so Unity's neutral
// local rotation is large and ZXY-decomposed; euler addition scrambles axes.)
//
// Axis Conversion is applied LIVE every frame — switch it in the Inspector
// DURING Play mode and the face updates instantly. Find the correct mode
// empirically with JawOpen (rotation direction), EyesLeft (gaze direction),
// Smile_L (corner must move outward on the character's left).
// ---------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

public class FacePoseDriver : MonoBehaviour
{
    public enum AxisConversionMode
    {
        None,       // deltas used as-is
        MirrorX,    // Maya->Unity default: negate tx; negate ry, rz
        MirrorZ     // alternative FBX paths: negate tz; negate rx, ry
    }

    [Header("Setup")]
    [Tooltip("The character's pose library JSON exported from Maya (ExportPoseLibrary).")]
    public TextAsset poseLibraryJson;

    [Tooltip("Root of the skeleton hierarchy (e.g. 'Root'). Bones are resolved by name under this.")]
    public Transform skeletonRoot;

    [Tooltip("Maya->Unity axis handling. LIVE: change this in Play mode and the face responds instantly. Test with JawOpen / EyesLeft / Smile_L.")]
    public AxisConversionMode axisConversion = AxisConversionMode.MirrorX;

    [Tooltip("LIVE. Multiplies translation deltas. If corner/lip movement is hugely exaggerated, the FBX import scale differs from Maya units — try 0.01 (Maya cm -> Unity m). Rotations are unaffected by units.")]
    public float translationScale = 1.0f;

    [Tooltip("LIVE debug. Alternative rotation composition (delta * neutral instead of neutral * delta). Try this if rotations land on the WRONG AXIS (e.g. jaw twists instead of opening).")]
    public bool deltaBeforeNeutral = false;

    // -----------------------------------------------------------------------
    // Pose weights. The import-time rebinder targets these BY NAME —
    // do not rename. Order matches the Maya canonical list.
    // -----------------------------------------------------------------------
    [Header("Pose Weights (driven by animation clips)")]
    // Mouth (11)
    public float JawOpen;
    public float Smile_L;       public float Smile_R;
    public float Frown_L;       public float Frown_R;
    public float MouthWide_L;   public float MouthWide_R;
    public float MouthNarrow_L; public float MouthNarrow_R;
    public float UpperLipRaise;
    public float LowerLipDown;
    // Brows (6)
    public float BrowUp_L;      public float BrowUp_R;
    public float BrowSad_L;     public float BrowSad_R;
    public float BrowAngry_L;   public float BrowAngry_R;
    // Eyes (10)
    public float Blink_L;       public float Blink_R;
    public float EyeWide_L;     public float EyeWide_R;
    public float Squint_L;      public float Squint_R;
    public float EyesUp;        public float EyesDown;
    public float EyesLeft;      public float EyesRight;
    // Cheeks (2)
    public float CheekRaise_L;  public float CheekRaise_R;

    // -----------------------------------------------------------------------
    // JSON schema (matches ExportPoseLibrary output).
    // -----------------------------------------------------------------------
    [Serializable] public class BoneTR      { public string bone; public float[] t; public float[] r; }
    [Serializable] public class Pose        { public string name; public BoneTR[] bones; }
    [Serializable] public class PoseLibrary { public string character; public BoneTR[] neutral; public Pose[] poses; }

    // -----------------------------------------------------------------------
    // Runtime state — deltas kept RAW (Maya values); conversion happens
    // per-frame so the Inspector toggle works live.
    // -----------------------------------------------------------------------
    class RuntimePose
    {
        public FieldInfo weightField;
        public int[]     boneIndex;
        public Vector3[] deltaT;   // raw Maya translate deltas
        public Vector3[] deltaR;   // raw Maya rotate deltas (degrees, XYZ order)
    }

    Transform[]  _boneTr;
    string[]     _boneNames;
    Vector3[]    _neutralPos;
    Quaternion[] _neutralRot;
    Vector3[]    _accumT;     // scratch: raw summed deltas
    Vector3[]    _accumR;
    readonly List<RuntimePose> _poses = new List<RuntimePose>();

    void Awake()
    {
        if (poseLibraryJson == null)
        {
            Debug.LogError("FacePoseDriver: no pose library JSON assigned.", this);
            enabled = false;
            return;
        }
        if (skeletonRoot == null)
            skeletonRoot = transform.root;

        // Guard: with several characters in a scene, pointing skeletonRoot at
        // another character resolves ALL bones successfully (standardized
        // names!) and silently drives the WRONG character's face.
        if (skeletonRoot.root != transform.root)
        {
            Debug.LogWarning(
                $"FacePoseDriver on '{transform.root.name}': skeletonRoot '{skeletonRoot.name}' "
                + $"belongs to a DIFFERENT hierarchy ('{skeletonRoot.root.name}'). "
                + "This will animate that character's face, not this one's. "
                + "Point skeletonRoot at this character's own Root.", this);
        }

        PoseLibrary lib = JsonUtility.FromJson<PoseLibrary>(poseLibraryJson.text);
        if (lib == null || lib.neutral == null || lib.neutral.Length == 0)
        {
            Debug.LogError("FacePoseDriver: pose library JSON failed to parse or has no neutral.", this);
            enabled = false;
            return;
        }

        // --- Resolve bones; neutral = the imported (bind) local pose,
        //     captured before any animation evaluation touches it ---
        var boneIndexByName = new Dictionary<string, int>();
        var bones = new List<Transform>();
        var names = new List<string>();
        var nPos  = new List<Vector3>();
        var nRot  = new List<Quaternion>();

        foreach (BoneTR n in lib.neutral)
        {
            Transform t = FindDeep(skeletonRoot, n.bone);
            if (t == null)
            {
                Debug.LogWarning($"FacePoseDriver: bone '{n.bone}' not found under '{skeletonRoot.name}' — skipped.", this);
                continue;
            }
            boneIndexByName[n.bone] = bones.Count;
            bones.Add(t);
            names.Add(n.bone);
            nPos.Add(t.localPosition);
            nRot.Add(t.localRotation);
        }

        _boneTr     = bones.ToArray();
        _boneNames  = names.ToArray();
        _neutralPos = nPos.ToArray();
        _neutralRot = nRot.ToArray();
        _accumT     = new Vector3[_boneTr.Length];
        _accumR     = new Vector3[_boneTr.Length];

        // --- Build runtime poses (raw deltas), matched to weight fields ---
        Type me = typeof(FacePoseDriver);
        foreach (Pose p in lib.poses)
        {
            FieldInfo f = me.GetField(p.name, BindingFlags.Public | BindingFlags.Instance);
            if (f == null || f.FieldType != typeof(float))
            {
                Debug.LogWarning($"FacePoseDriver: JSON pose '{p.name}' has no matching weight field — skipped.", this);
                continue;
            }

            var idx = new List<int>();
            var dT  = new List<Vector3>();
            var dR  = new List<Vector3>();
            foreach (BoneTR b in p.bones)
            {
                if (!boneIndexByName.TryGetValue(b.bone, out int bi))
                    continue;
                idx.Add(bi);
                dT.Add(ToV3(b.t));
                dR.Add(ToV3(b.r));
            }

            _poses.Add(new RuntimePose
            {
                weightField = f,
                boneIndex   = idx.ToArray(),
                deltaT      = dT.ToArray(),
                deltaR      = dR.ToArray()
            });
        }

        Debug.Log($"FacePoseDriver: '{lib.character}' ready — {_boneTr.Length} bones, {_poses.Count} poses (driving skeleton under '{skeletonRoot.root.name}/{skeletonRoot.name}').", this);
    }

    void LateUpdate()
    {
        int n = (_boneTr != null) ? _boneTr.Length : 0;
        if (n == 0) return;

        for (int i = 0; i < n; i++)
        {
            _accumT[i] = Vector3.zero;
            _accumR[i] = Vector3.zero;
        }

        // additive sum of weighted RAW deltas
        for (int p = 0; p < _poses.Count; p++)
        {
            RuntimePose rp = _poses[p];
            float w = (float)rp.weightField.GetValue(this);
            if (Mathf.Abs(w) < 1e-4f) continue;

            for (int k = 0; k < rp.boneIndex.Length; k++)
            {
                int bi = rp.boneIndex[k];
                _accumT[bi] += w * rp.deltaT[k];
                _accumR[bi] += w * rp.deltaR[k];
            }
        }

        // convert (LIVE — respects the current Inspector setting) and apply
        for (int i = 0; i < n; i++)
        {
            Vector3 t = ConvertT(_accumT[i]) * translationScale;
            Quaternion q = MayaXYZQuat(ConvertR(_accumR[i]));
            _boneTr[i].localPosition = _neutralPos[i] + t;
            _boneTr[i].localRotation = deltaBeforeNeutral
                ? q * _neutralRot[i]
                : _neutralRot[i] * q;
        }
    }

    // -----------------------------------------------------------------------
    // Diagnostics — right-click the component header (gear menu) in the
    // Inspector to run these, in Play mode.
    // -----------------------------------------------------------------------
    [ContextMenu("Debug: Log Resolved Bone Paths")]
    void LogResolvedBonePaths()
    {
        if (_boneTr == null) { Debug.Log("FacePoseDriver: not initialized (enter Play mode)."); return; }
        var sb = new System.Text.StringBuilder("FacePoseDriver resolved bones:\n");
        for (int i = 0; i < _boneTr.Length; i++)
            sb.AppendLine("  " + _boneNames[i] + "  ->  " + FullPath(_boneTr[i]));
        Debug.Log(sb.ToString(), this);
    }

    [ContextMenu("Debug: Log Pose Contents")]
    void LogPoseContents()
    {
        if (_boneTr == null) { Debug.Log("FacePoseDriver: not initialized (enter Play mode)."); return; }
        var sb = new System.Text.StringBuilder("FacePoseDriver pose contents (from JSON):\n");
        foreach (RuntimePose rp in _poses)
        {
            sb.Append("  " + rp.weightField.Name + ": ");
            for (int k = 0; k < rp.boneIndex.Length; k++)
            {
                if (k > 0) sb.Append(", ");
                sb.Append(_boneNames[rp.boneIndex[k]]);
                sb.Append(rp.deltaR[k].sqrMagnitude > 0.0001f ? "(R" : "(");
                sb.Append(rp.deltaT[k].sqrMagnitude > 1e-8f ? "T)" : ")");
            }
            sb.AppendLine();
        }
        Debug.Log(sb.ToString(), this);
    }

    static string FullPath(Transform t)
    {
        string p = t.name;
        while (t.parent != null) { t = t.parent; p = t.name + "/" + p; }
        return p;
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------
    Vector3 ConvertT(Vector3 v)
    {
        switch (axisConversion)
        {
            case AxisConversionMode.MirrorX: return new Vector3(-v.x,  v.y,  v.z);
            case AxisConversionMode.MirrorZ: return new Vector3( v.x,  v.y, -v.z);
            default:                         return v;
        }
    }

    Vector3 ConvertR(Vector3 v)
    {
        switch (axisConversion)
        {
            case AxisConversionMode.MirrorX: return new Vector3( v.x, -v.y, -v.z);
            case AxisConversionMode.MirrorZ: return new Vector3(-v.x, -v.y,  v.z);
            default:                         return v;
        }
    }

    // Quaternion from euler degrees in MAYA's XYZ rotate order
    // (rotate about X first, then Y, then Z).
    static Quaternion MayaXYZQuat(Vector3 e)
    {
        return Quaternion.AngleAxis(e.z, Vector3.forward)
             * Quaternion.AngleAxis(e.y, Vector3.up)
             * Quaternion.AngleAxis(e.x, Vector3.right);
    }

    static Vector3 ToV3(float[] a) =>
        (a != null && a.Length >= 3) ? new Vector3(a[0], a[1], a[2]) : Vector3.zero;

    public static Transform FindDeep(Transform root, string name)
    {
        if (root.name == name) return root;
        for (int i = 0; i < root.childCount; i++)
        {
            Transform hit = FindDeep(root.GetChild(i), name);
            if (hit != null) return hit;
        }
        return null;
    }
}
