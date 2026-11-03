using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

// Your own body is never in view: in a headset the third-person model's head
// and mask sit around the eyes, and the first-person rig carries head, mask
// and backpack meshes the flat camera never sees. All of them are hidden
// while you play; only the washer and the mod's own hands are drawn.
//
// The third-person model keeps casting its shadow, and it comes back for the
// job-complete time-lapse and cut-scenes, where it is meant to be seen.
internal sealed class BodyHider
{
    private readonly MelonLogger.Instance log;
    private float nextScan;
    private IntPtr scannedFor;
    private bool hidden;
    private int count;
    private bool loggedCamera;

    private sealed class Entry
    {
        internal Renderer Renderer = null!;
        internal UnityEngine.Rendering.ShadowCastingMode Shadows;
        internal bool FirstPerson;
        internal bool Enabled;
    }

    private readonly List<Entry> entries = new();
    private readonly HashSet<IntPtr> known = new();
    private int lastHierarchy = -1;
    private Transform? thirdRoot, firstRoot;
    private Il2CppFuturLab.PW2.PlayerCharacter? lastCharacter;
    private PlayerRig? lastRig;

    private int HierarchySize() =>
        (thirdRoot is not null && thirdRoot != null ? thirdRoot.hierarchyCount : 0)
        + (firstRoot is not null && firstRoot != null ? firstRoot.hierarchyCount * 7919 : 0);

    // Just before the eyes or the smooth camera draw: a washer the game
    // rebuilt after the late update (taking something off the case, a nozzle
    // swap) is hidden for this very frame too.
    private void CatchRebuild()
    {
        if (lastCharacter is null || lastCharacter == null || lastRig is null)
            return;
        var size = HierarchySize();
        if (size == lastHierarchy)
            return;
        lastHierarchy = size;
        Scan(lastCharacter, lastRig);
        if (hidden)
            Hide();
    }

    internal BodyHider(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void Update(PlayerRig rig, GameState state)
    {
        var character = state.Character;
        var show = character is null || ShowsBody(state.StateName);

        // Backstop for the SetCameraMode patch: whatever route the game took
        // into third person, it goes back to first person while you play.
        if (character is not null && state.Mode != UiMode.Menu && Time.frameCount % 15 == 0)
        {
            try
            {
                if (character.CameraMode == Il2CppFuturLab.PW2.PlayerCameraMode.ThirdPerson)
                {
                    character.SetCameraMode(Il2CppFuturLab.PW2.PlayerCameraMode.FirstPerson);
                    if (!loggedCamera)
                    {
                        loggedCamera = true;
                        log.Msg("body: the game switched to third person; switched back to first person");
                    }
                }
            }
            catch
            {
                // Not every game version has the switch.
            }
        }

        EyeCamera = rig.Camera is { } eye && eye != null ? eye.Pointer : IntPtr.Zero;

        // Safety: if the per-camera hook never sees the eye camera render, the
        // body is hidden for every camera instead.
        if (perCamera && hidden && EyeCamera != IntPtr.Zero)
        {
            if (hiddenSince < 0f)
                hiddenSince = Time.unscaledTime;
            if (Time.unscaledTime - hiddenSince > 3f && Time.unscaledTime - lastEyeBegin > 3f)
            {
                perCamera = false;
                log.Warning("body: the per-camera hook does not see the eyes render; hiding the body for every camera");
                Hide();
            }
        }
        else if (!hidden)
        {
            hiddenSince = -1f;
        }
        // A nozzle or washer swap rebuilds the body's washer: new renderers
        // must be hidden at once (they showed as a second washer and hands
        // for up to a scan interval). The hierarchy size gives it away for
        // free; a slow scan backs it up.
        var changed = false;
        lastCharacter = character;
        lastRig = rig;
        if (character is not null)
        {
            try
            {
                var third = character.ThirdPersonVisuals;
                var first = character.FirstPersonVisuals;
                thirdRoot = third is not null && third != null ? third.transform : null;
                firstRoot = first is not null && first != null ? first.transform : null;
                var size = HierarchySize();
                changed = size != lastHierarchy;
                lastHierarchy = size;
            }
            catch
            {
                // The slow scan covers it.
            }
        }

        if (character is not null && (changed || Time.unscaledTime >= nextScan))
        {
            nextScan = Time.unscaledTime + 1f;
            if (character.Pointer != scannedFor)
            {
                Restore();
                entries.Clear();
                known.Clear();
                scannedFor = character.Pointer;
            }

            Scan(character, rig);
            if (!show)
                Hide();
        }

        if (show && hidden)
            Restore();
        else if (!show && (!hidden || Time.frameCount % 30 == 0))
            Hide();

        if (Time.frameCount % 600 == 0)
            entries.RemoveAll(entry => entry.Renderer is null || entry.Renderer == null);
    }

    // The job-complete screen plays the time-lapse; cut-scenes show the
    // character too.
    private static bool ShowsBody(string state) =>
        state.Contains("JobComplete", StringComparison.Ordinal) || state.Contains("Cutscene", StringComparison.Ordinal)
        || state.Contains("Customisation", StringComparison.Ordinal);

    private void Scan(Il2CppFuturLab.PW2.PlayerCharacter character, PlayerRig rig)
    {
        var before = entries.Count;
        Collect(character.ThirdPersonVisuals, false, rig);
        Collect(character.FirstPersonVisuals, true, rig);
        if (entries.Count != before)
        {
            count = entries.Count;
            log.Msg($"body: {count} body renderer(s) kept out of view");
        }
    }

    private void Collect(Il2CppFuturLab.PW2.BaseCharacterVisuals? visuals, bool firstPerson, PlayerRig rig)
    {
        if (visuals is null || visuals == null)
            return;

        var renderers = visuals.GetComponentsInChildren<Renderer>(true);
        if (renderers is null)
            return;

        var anchor = rig.Anchor;
        var arms = rig.ArmsPivot;
        for (var index = 0; index < renderers.Length; index++)
        {
            var renderer = renderers[index];
            if (renderer is null || renderer == null || known.Contains(renderer.Pointer))
                continue;

            var transform = renderer.transform;

            // The washer in your hand stays. (The arms are drawn by Hands from
            // copies outside this hierarchy; the originals are hidden here too.)
            if (firstPerson && anchor is not null && anchor != null && transform.IsChildOf(anchor))
                continue;

            // Particle and trail effects (spray, splashes) are not body.
            if (renderer.TryCast<SkinnedMeshRenderer>() is null && renderer.TryCast<MeshRenderer>() is null)
                continue;

            known.Add(renderer.Pointer);
            entries.Add(new Entry
            {
                Renderer = renderer,
                Shadows = renderer.shadowCastingMode,
                FirstPerson = firstPerson,
                Enabled = renderer.enabled,
            });
        }
    }

    private void Hide()
    {
        hidden = true;
        foreach (var entry in entries)
        {
            var renderer = entry.Renderer;
            if (renderer is null || renderer == null)
                continue;

            if (entry.FirstPerson)
            {
                if (!renderer.forceRenderingOff)
                    renderer.forceRenderingOff = true;
            }
            else if (!perCamera && renderer.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly)
            {
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
            }
        }
    }

    // The third-person body is taken out only for the cameras that look out
    // of your head (the eyes, the smooth camera): the game's own captures
    // (the job-complete time-lapse is made of pictures taken while you play)
    // still see you. Done around each camera's render.
    private static BodyHider? instance;
    private bool perCamera;
    private bool cameraHidden;
    private float lastEyeBegin = -1f, hiddenSince = -1f;
    internal static IntPtr EyeCamera, SmoothCameraPointer;

    // For diagnostics: is this renderer one the hider handles (and how)?
    internal static string Role(Renderer renderer)
    {
        var self = instance;
        if (self is null)
            return "";
        foreach (var entry in self.entries)
        {
            if (entry.Renderer is not null && entry.Renderer != null && entry.Renderer.Pointer == renderer.Pointer)
                return entry.FirstPerson ? " body-hider:first-person" : " body-hider:third-person";
        }

        return "";
    }

    internal void InstallCameraHooks()
    {
        instance = this;
        try
        {
            var begin = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>>(
                new Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>(OnBeginCamera));
            var end = Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>>(
                new Action<UnityEngine.Rendering.ScriptableRenderContext, Camera>(OnEndCamera));
            UnityEngine.Rendering.RenderPipelineManager.add_beginCameraRendering(begin);
            UnityEngine.Rendering.RenderPipelineManager.add_endCameraRendering(end);
            perCamera = true;
            log.Msg("body: hidden per camera (the game's own captures still see the player)");
        }
        catch (Exception exception)
        {
            perCamera = false;
            log.Warning($"body: per-camera hiding not available ({exception.GetType().Name}); the body is hidden for every camera");
        }
    }

    private static void OnBeginCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
    {
        if (camera is not null && SmoothCameraPointer != IntPtr.Zero && camera.Pointer == SmoothCameraPointer)
            WasherFixes.BeforeOtherCamera();
        var self = instance;
        if (self is null || !self.perCamera || !self.hidden || camera is null)
            return;
        var pointer = camera.Pointer;
        if (pointer != EyeCamera && pointer != SmoothCameraPointer)
            return;
        try
        {
            self.CatchRebuild();
            Backpack.BeforeCamera();
        }
        catch
        {
            // The late update has it next frame.
        }

        self.cameraHidden = true;
        self.lastEyeBegin = Time.unscaledTime;
        foreach (var entry in self.entries)
        {
            var renderer = entry.Renderer;
            if (entry.FirstPerson || renderer is null || renderer == null)
                continue;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.ShadowsOnly;
        }
    }

    private static void OnEndCamera(UnityEngine.Rendering.ScriptableRenderContext context, Camera camera)
    {
        var self = instance;
        if (self is null || !self.cameraHidden)
            return;
        self.cameraHidden = false;
        foreach (var entry in self.entries)
        {
            var renderer = entry.Renderer;
            if (entry.FirstPerson || renderer is null || renderer == null)
                continue;
            renderer.shadowCastingMode = entry.Shadows;
        }
    }

    private void Restore()
    {
        hidden = false;
        foreach (var entry in entries)
        {
            var renderer = entry.Renderer;
            if (renderer is null || renderer == null)
                continue;

            if (entry.FirstPerson)
                renderer.forceRenderingOff = false;
            else
                renderer.shadowCastingMode = entry.Shadows;
        }
    }
}
