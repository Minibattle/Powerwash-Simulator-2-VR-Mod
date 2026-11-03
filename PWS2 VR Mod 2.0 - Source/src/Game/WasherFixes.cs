using System.Linq;
using Il2CppFuturLab.PW2;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

// Everything the flat game does to make a first-person washer look right on a
// monitor, undone for VR, plus the wash direction handed to the game.
//
//  - The washer and arm shaders squeeze the tool into a fixed field of view and
//    compress its depth (_LockFOV and two depth globals on
//    CharacterVisualsFirstPerson). In a headset that puts the drawn washer
//    somewhere other than the hand. The globals are set to identity each frame.
//  - PositionToFOV moves the nozzle anchors to match the squeezed picture. It is
//    disabled, and because it has already baked an offset into the anchors by
//    the time VR starts, each anchor is put back where the third-person washer
//    (which never had that component) has it.
//  - RaySpawnPoint is rotated toward the screen crosshair by the game's aim
//    code; it is held at identity so the jet leaves along the barrel.
//  - Both arms are one mesh hung off the head, unusable in VR, so they are hidden.
//
// The jet's origin and direction for each nozzle are published for the
// RaycastUpdate patch, which writes them into the game's wash job.
internal sealed class WasherFixes
{
    internal const int MaxJets = 8;

    private readonly MelonLogger.Instance log;

    private CharacterVisualsFirstPerson? visuals;
    private int lockId, minDistId, scaleId, toggleId;
    private bool keywordsLogged;
    private EquipmentManager? manager;
    private WashEquipment? equipment;
    private PositionToFOV? disabledFov;
    private float nextRefresh;

    private readonly Transform?[] anchors = new Transform?[MaxJets];
    private readonly Vector3[] anchorRest = new Vector3[MaxJets];
    private readonly Vector3[] twinRest = new Vector3[MaxJets];
    private readonly bool[] twinHasRest = new bool[MaxJets];
    private readonly bool[] anchorHasRest = new bool[MaxJets];
    private readonly Transform?[] spawns = new Transform?[MaxJets];
    // The jet's authored turn off the barrel (the side jets of a multi-tip
    // nozzle fan out; a single jet is straight).
    private readonly Quaternion[] spawnRest = new Quaternion[MaxJets];
    private int jetCount;
    private int lastActiveJets = -1;
    private float nextJetCheck;
    private long multiLogged;

    private readonly List<Renderer> hidden = new();
    private IntPtr hiddenFor;
    private int hideRefreshes;

    // Read by the RaycastUpdate patch.
    internal static readonly Vector3[] JetOrigin = new Vector3[MaxJets];
    internal static readonly Vector3[] JetDirection = new Vector3[MaxJets];
    internal static int JetCount;
    internal static bool Active;
    internal static IntPtr EquipmentPointer;

    internal WashEquipment? Equipment => PlayerRig.Alive(equipment) ? equipment : null;

    internal WasherFixes(MelonLogger.Instance log)
    {
        this.log = log;
        instance = this;
    }

    internal void Reset()
    {
        visuals = null;
        manager = null;
        equipment = null;
        disabledFov = null;
        jetCount = 0;
        JetCount = 0;
        Active = false;
        EquipmentPointer = IntPtr.Zero;
        hidden.Clear();
        hiddenFor = IntPtr.Zero;
        nextRefresh = 0f;
        Array.Clear(anchors);
        Array.Clear(spawns);
    }

    internal void Update(PlayerRig rig)
    {
        if (!rig.IsPlayer || rig.Assembly is null)
        {
            Active = false;
            return;
        }

        // A nozzle swap changes the jet count: the anchors are re-seated
        // straight away (checked ten times a second), not at the next refresh.
        if (Time.unscaledTime >= nextJetCheck && equipment is not null && equipment != null)
        {
            nextJetCheck = Time.unscaledTime + 0.1f;
            try
            {
                var active = equipment.m_activeNozzleCount;
                if (active != lastActiveJets)
                {
                    lastActiveJets = active;
                    nextRefresh = 0f;
                }
            }
            catch
            {
            }
        }

        if (Time.unscaledTime >= nextRefresh)
        {
            nextRefresh = Time.unscaledTime + 1f;
            Refresh(rig);
        }

        ApplyGlobals();
        NeutralDepth();
        ApplyTransforms();
        Publish();
        EnforceHidden();
    }

    // Shader globals and nozzle transforms only, for the pre-render callback.
    internal void Reapply()
    {
        if (!Active)
            return;

        ApplyGlobals();
        NeutralDepth();
        ApplyTransforms();
    }

    private void ApplyGlobals()
    {
        if (visuals is null)
            return;

        Shader.SetGlobalFloat(lockId, 0f);
        Shader.SetGlobalFloat(minDistId, 0f);
        Shader.SetGlobalFloat(scaleId, 1f);
    }

    private void ApplyTransforms()
    {
        for (var index = 0; index < jetCount; index++)
        {
            var anchor = anchors[index];
            if (anchorHasRest[index] && anchor is not null && anchor != null)
                anchor.localPosition = anchorRest[index];

            var spawn = spawns[index];
            if (spawn is not null && spawn != null)
                spawn.localRotation = spawnRest[index];
        }
    }

    private void Publish()
    {
        var count = 0;
        for (var index = 0; index < jetCount; index++)
        {
            var spawn = spawns[index];
            if (spawn is null || spawn == null)
                continue;

            spawn.GetPositionAndRotation(out var position, out var rotation);
            JetOrigin[index] = position;
            JetDirection[index] = rotation * Vector3.forward;
            count = index + 1;
        }

        JetCount = count;
        Active = count > 0;
    }

    private static WasherFixes? instance;
    private Transform? lastAssembly;
    private float nextOtherCameraFix;

    // Just before the smooth camera draws: the first-person projection the
    // game turns back on while it swaps nozzles or washers (new parts, new
    // materials) is taken off again, so the washer and hands are not drawn a
    // second time somewhere else. Globals every time, materials at most four
    // times a second.
    internal static void BeforeOtherCamera()
    {
        var self = instance;
        if (self is null || !Active)
            return;
        try
        {
            self.ApplyGlobals();
            var now = Time.unscaledTime;
            if (now >= self.nextOtherCameraFix && self.lastAssembly is not null && self.lastAssembly != null)
            {
                self.nextOtherCameraFix = now + 0.25f;
                self.PlainProjection(self.lastAssembly);
                self.EnforceHidden();
            }

            self.NeutralDepth();
        }
        catch
        {
            // Next frame.
        }
    }

    private void Refresh(PlayerRig rig)
    {
        var assembly = rig.Assembly!;
        lastAssembly = assembly;
        var root = rig.CameraTransform!.root;

        if (visuals is null || visuals == null)
        {
            visuals = FindUnder<CharacterVisualsFirstPerson>(root);
            if (visuals is not null)
            {
                lockId = visuals.m_fovLockPropID;
                toggleId = visuals.m_fovTogglePropID;
                minDistId = visuals.m_washerDepthMinDistPropID;
                scaleId = visuals.m_washerDepthScalePropID;
                hasDepth.Clear();
                ToggleProperty = toggleId;
                MinDistProperty = minDistId;
                ScaleProperty = scaleId;
                log.Msg("washer: first-person shader lock found and neutralised");
            }
        }

        if (manager is null || manager == null)
            manager = FindUnder<EquipmentManager>(root);

        // The selected equipment changes with the washer, so it is re-read on
        // every refresh rather than cached for good.
        var selected = manager is null ? FindUnder<WashEquipment>(root) : manager.WashEquipment;
        if (selected is null || selected == null)
        {
            equipment = null;
        }
        else if (equipment is null || equipment == null || equipment.Pointer != selected.Pointer)
        {
            equipment = selected;
            Array.Clear(anchors);
            Array.Clear(spawns);
            log.Msg("washer: wash equipment found");
        }

        EquipmentPointer = equipment is null ? IntPtr.Zero : equipment.Pointer;

        if (equipment is null)
        {
            jetCount = 0;
            return;
        }

        DisablePositionToFov();
        RefreshJets(assembly, root);
        HideArms(rig);
        PlainProjection(assembly);
    }

    // The flat game draws the washer with a projection of its own (a fixed
    // first-person field of view), switched on per material. In the eyes it
    // is neutralised through the shader globals; any other camera (the smooth
    // camera) would still draw it there, so the switch is turned off on the
    // washer's materials as well.
    private readonly HashSet<IntPtr> keywordsDone = new();

    private void PlainProjection(Transform assembly)
    {
        if (toggleId == 0)
            return;

        var renderers = assembly.GetComponentsInChildren<Renderer>(true);
        var changed = 0;
        var keywords = keywordsLogged ? null : new HashSet<string>();
        for (var index = 0; renderers is not null && index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer is null || renderer == null)
                continue;
            var materials = renderer.sharedMaterials;
            for (var slot = 0; materials is not null && slot < materials.Length; slot++)
            {
                var material = materials[slot];
                if (material is null || material == null)
                    continue;
                if (material.HasProperty(toggleId) && material.GetFloat(toggleId) != 0f)
                {
                    material.SetFloat(toggleId, 0f);
                    changed++;
                }

                // Each material's keywords are read once (a string array
                // from the engine each time): the game does not turn the
                // projection keyword back on for a material it already has.
                if (!keywordsDone.Add(material.Pointer))
                    continue;
                if (!washerMaterials.Contains(material))
                    washerMaterials.Add(material);

                var enabled = material.shaderKeywords;
                for (var key = 0; enabled is not null && key < enabled.Length; key++)
                {
                    var name = enabled[key];
                    if (string.IsNullOrEmpty(name))
                        continue;
                    keywords?.Add(name);
                    if (name.Contains("FOV", StringComparison.OrdinalIgnoreCase))
                    {
                        material.DisableKeyword(name);
                        changed++;
                    }
                }
            }
        }

        if (!keywordsLogged && keywords is not null && keywords.Count > 0)
        {
            keywordsLogged = true;
            log.Msg($"washer: material keywords {string.Join(", ", keywords)}");
        }

        if (changed > 0 && projectionLogs++ < 5)
            log.Msg($"washer: first-person projection switched off on {changed} material setting(s)");
    }

    // The washer's first-person property ids, and other materials (the
    // spray) held neutral the same way.
    internal static int ToggleProperty, MinDistProperty, ScaleProperty;
    internal static readonly List<Material> ExtraMaterials = new();

    private int projectionLogs;
    private readonly List<Material> washerMaterials = new();
    private bool depthLogged;

    // The game squeezes the washer's depth toward the eye camera (so it does
    // not sink into walls) through properties on its materials, every frame.
    // Seen from any other camera that puts it in the wrong place: they are
    // held neutral on the washer's materials, after the game's update and
    // again just before rendering.
    private void NeutralDepth()
    {
        // Runs several times a frame: gone materials are dropped once a
        // second, and which properties a material has is asked only once.
        var now = Time.unscaledTime;
        if (now >= nextPrune)
        {
            nextPrune = now + 1f;
            washerMaterials.RemoveAll(material => material is null || material == null);
            ExtraMaterials.RemoveAll(material => material is null || material == null);
            if (hasDepth.Count > 512)
                hasDepth.Clear();
        }

        Neutral(washerMaterials);
        Neutral(ExtraMaterials);
    }

    private float nextPrune;
    private readonly Dictionary<IntPtr, (bool MinDist, bool Scale)> hasDepth = new();

    private void Neutral(List<Material> materials)
    {
        for (var index = 0; index < materials.Count; index++)
        {
            var material = materials[index];
            if (material is null)
                continue;
            if (!hasDepth.TryGetValue(material.Pointer, out var has))
            {
                if (material == null)
                    continue;
                has = (minDistId != 0 && material.HasProperty(minDistId), scaleId != 0 && material.HasProperty(scaleId));
                hasDepth[material.Pointer] = has;
            }

            try
            {
                if (has.MinDist && material.GetFloat(minDistId) != 0f)
                    material.SetFloat(minDistId, 0f);
                if (has.Scale && material.GetFloat(scaleId) != 1f)
                {
                    material.SetFloat(scaleId, 1f);
                    if (!depthLogged)
                    {
                        depthLogged = true;
                        log.Msg("washer: per-material depth squeeze neutralised");
                    }
                }
            }
            catch
            {
                // Destroyed since the last prune: pruned next time round.
                nextPrune = 0f;
            }
        }
    }

    private void DisablePositionToFov()
    {
        var fov = equipment!.m_positionToFOV;
        if (fov is null || fov == null)
            return;

        if (disabledFov is not null && disabledFov.Pointer == fov.Pointer && !fov.enabled)
            return;

        fov.enabled = false;
        try
        {
            fov.ResetTransform();
        }
        catch
        {
            // Restoring is a nicety; the anchors are re-seated below anyway.
        }

        disabledFov = fov;
        log.Msg("washer: PositionToFOV disabled");
    }

    private void RefreshJets(Transform assembly, Transform root)
    {
        var equipment = this.equipment!;
        var instances = equipment.m_nozzleInstances;
        if (instances is null)
        {
            jetCount = 0;
            return;
        }

        var twinAssembly = root.Find("Third Person Visuals/EquipmentAnchor/PowerWasher_Assembly");
        var count = Math.Min(instances.Length, MaxJets);
        // The washer always holds three jet slots; only a multi-jet nozzle
        // (the triple) uses more than one, and only it gets the side offsets.
        var active = 1;
        try
        {
            active = equipment.m_activeNozzleCount;
        }
        catch
        {
        }

        var multi = active > 1;

        for (var index = 0; index < count; index++)
        {
            var instance = instances[index];
            var nozzle = instance?.NozzleAnchor;
            if (nozzle is null || nozzle == null)
            {
                anchors[index] = null;
                spawns[index] = null;
                anchorHasRest[index] = false;
                continue;
            }

            var anchor = nozzle.transform;
            if (anchors[index] is null || anchors[index]!.Pointer != anchor.Pointer)
            {
                anchors[index] = anchor;
                anchorHasRest[index] = TwinRest(anchor, assembly, twinAssembly, out anchorRest[index]);
                twinHasRest[index] = anchorHasRest[index];
                twinRest[index] = anchorRest[index];
            }

            spawns[index] = nozzle.RaySpawnPoint;
            spawnRest[index] = Quaternion.identity;
            try
            {
                var raycaster = instance!.Raycaster;
                if (raycaster is not null)
                {
                    spawnRest[index] = Quaternion.Euler(raycaster.RaySpawnPointRotation);
                    // Several jets: each sits at its own authored offset (the
                    // twin lookup put them all on the centre line, one on top
                    // of the other, so three streams looked like one).
                    // Their depth stays the nozzle's own (the authored offset
                    // alone started the streams a little behind the tip).
                    if (multi)
                    {
                        // Depth: at least the main jet's own (the side
                        // jets' anchors sit at 0, behind the tip, so their
                        // streams came out of the back of the nozzle).
                        var offset = raycaster.NozzleAnchorOffset;
                        if (twinHasRest[index])
                            offset.z = Mathf.Max(offset.z, twinRest[index].z);
                        if (twinHasRest[0])
                            offset.z = Mathf.Max(offset.z, twinRest[0].z);
                        anchorRest[index] = offset;
                        anchorHasRest[index] = true;
                    }
                    else
                    {
                        // A single-jet nozzle: back to the anchor's own rest.
                        anchorRest[index] = twinRest[index];
                        anchorHasRest[index] = twinHasRest[index];
                    }
                }
            }
            catch
            {
                // Straight then.
            }
        }

        var multiKey = equipment!.Pointer.ToInt64() ^ ((long)active << 56);
        if (multi && multiLogged != multiKey)
        {
            multiLogged = multiKey;
            var parts = new List<string>();
            for (var index = 0; index < count; index++)
                parts.Add($"{(anchors[index] is { } a && a != null ? a.name : "?")} at {anchorRest[index]} (own {(twinHasRest[index] ? twinRest[index].ToString() : "-")}) "
                    + $"spawn {(spawns[index] is { } sp && sp != null ? sp.localPosition.ToString() : "-")} turned {spawnRest[index].eulerAngles}");
            log.Msg($"washer: {count} jets (active {SafeActive()}): {string.Join("; ", parts)}");
        }

        for (var index = count; index < MaxJets; index++)
        {
            anchors[index] = null;
            spawns[index] = null;
            anchorHasRest[index] = false;
        }

        jetCount = count;
    }

    private string SafeActive()
    {
        try
        {
            return equipment is null ? "?" : equipment.m_activeNozzleCount.ToString();
        }
        catch
        {
            return "?";
        }
    }

    // Where the third-person washer keeps the same anchor. That washer never
    // had PositionToFOV, so its offset is the authored one.
    private static bool TwinRest(Transform anchor, Transform assembly, Transform? twinAssembly, out Vector3 rest)
    {
        anchor.GetLocalPositionAndRotation(out var local, out _);

        // Without a twin, at least remove the sideways part of the baked offset.
        rest = new Vector3(0f, 0f, local.z);

        if (twinAssembly is null || twinAssembly == null)
            return true;

        // Matched by place in the hierarchy (a multi-tip nozzle has several
        // anchors of the same name), by name if the shape differs.
        var path = anchor.name;
        var steps = new List<(int Index, string Name)> { (anchor.GetSiblingIndex(), anchor.name) };
        for (var node = anchor.parent; node is not null && node != null; node = node.parent)
        {
            if (node.Pointer == assembly.Pointer)
            {
                Transform? twin = twinAssembly;
                for (var step = steps.Count - 1; step >= 0 && twin is not null; step--)
                {
                    var (index, name) = steps[step];
                    twin = index < twin.childCount && twin.GetChild(index) is { } child && child != null && child.name == name ? child : null;
                }

                if (twin is null || twin == null)
                    twin = twinAssembly.Find(path);
                if (twin is not null && twin != null)
                    twin.GetLocalPositionAndRotation(out rest, out _);
                return true;
            }

            path = node.name + "/" + path;
            steps.Add((node.GetSiblingIndex(), node.name));
        }

        return true;
    }

    private void HideArms(PlayerRig rig)
    {
        var pivot = rig.ArmsPivot;
        var assembly = rig.Assembly!;

        // Rebuilt when the washer changes, and every few seconds anyway: an
        // outfit change swaps the arm meshes without touching the washer.
        if (hiddenFor != assembly.Pointer || hidden.Count == 0 || ++hideRefreshes >= 10)
        {
            hideRefreshes = 0;
            var before = hidden.Count;
            hidden.Clear();
            hiddenFor = assembly.Pointer;

            if (pivot is not null && pivot != null)
                Collect(pivot, hidden, null);

            CollectBody(assembly, hidden);

            if (hidden.Count > 0 && hidden.Count != before)
                log.Msg($"washer: hiding {hidden.Count} first-person arm/hand renderer(s)");
        }

        EnforceHidden();
    }

    // Every frame: the game re-enables the first-person arm renderers as it
    // pleases (stance and equipment changes). forceRenderingOff is a switch
    // the game itself never touches.
    private void EnforceHidden()
    {
        for (var index = 0; index < hidden.Count; index++)
        {
            var renderer = hidden[index];
            if (renderer is null || renderer == null)
                continue;
            if (!renderer.forceRenderingOff)
                renderer.forceRenderingOff = true;
        }
    }

    private static void Collect(Transform root, List<Renderer> into, HashSet<int>? exclude)
    {
        var all = root.GetComponentsInChildren<Renderer>(true);
        if (all is null)
            return;

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];
            if (renderer is null || renderer == null)
                continue;
            if (exclude is not null && exclude.Contains(renderer.GetInstanceID()))
                continue;
            into.Add(renderer);
        }
    }

    // Hand and glove meshes that hang under the washer itself: every plain mesh
    // renderer that is not part of the gun, extension or nozzles, and not under
    // a nozzle anchor (the jet effects live there).
    private static void CollectBody(Transform assembly, List<Renderer> into)
    {
        var assembler = assembly.GetComponentInChildren<PowerWasherAssembler>(true);
        if (assembler is null || assembler == null)
            return;

        var gun = new HashSet<int>();
        Add(gun, assembler.m_pressureGunRenderer);
        Add(gun, assembler.m_extensionRenderer);
        var nozzles = assembler.m_nozzleRenderers;
        if (nozzles is not null)
        {
            for (var index = 0; index < nozzles.Count; index++)
                Add(gun, nozzles[index]);
        }

        var all = assembly.GetComponentsInChildren<Renderer>(true);
        if (all is null)
            return;

        for (var index = 0; index < all.Length; index++)
        {
            var renderer = all[index];
            if (renderer is null || renderer == null || gun.Contains(renderer.GetInstanceID()))
                continue;

            if (renderer.TryCast<SkinnedMeshRenderer>() is null && renderer.TryCast<MeshRenderer>() is null)
                continue;

            if (UnderNozzle(renderer.transform, assembly))
                continue;

            into.Add(renderer);
        }
    }

    private static void Add(HashSet<int> set, Renderer? renderer)
    {
        if (renderer is not null && renderer != null)
            set.Add(renderer.GetInstanceID());
    }

    private static bool UnderNozzle(Transform node, Transform stop)
    {
        for (var current = node; current is not null && current != null && current.Pointer != stop.Pointer;
             current = current.parent)
        {
            if (current.GetComponent<NozzleAnchor>() is not null)
                return true;
        }

        return false;
    }

    private static T? FindUnder<T>(Transform root) where T : Component
    {
        var all = UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None);
        if (all is null)
            return null;

        for (var index = 0; index < all.Length; index++)
        {
            var candidate = all[index];
            if (candidate is not null && candidate != null && candidate.transform.IsChildOf(root))
                return candidate;
        }

        return null;
    }
}
