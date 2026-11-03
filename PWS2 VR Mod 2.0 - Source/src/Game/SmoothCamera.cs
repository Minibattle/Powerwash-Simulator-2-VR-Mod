using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace PowerWashVR.Game;

// The Smooth Camera (VR SETTINGS): a separate flat camera for the desktop
// window that follows the head with its shake taken out, at its own field of
// view. It renders into a texture the size of the window (never into the
// headset), and the mirror copies that texture to the window instead of the
// right eye. It costs one more render of the scene, so it is off by default.
internal sealed class SmoothCamera
{
    private readonly MelonLogger.Instance log;
    private GameObject? root;
    private Camera? camera;
    private Vector3 position;
    private Quaternion rotation = Quaternion.identity;
    private bool placed;
    private IntPtr copiedFrom;
    private RenderTexture? texture;
    private const int WasherLayers = 8192;
    private const int SprayLayer = 1 << 20;

    // What the window shows while the smooth camera is on.
    internal RenderTexture? Output => Active ? texture : null;

    internal SmoothCamera(MelonLogger.Instance log)
    {
        this.log = log;
    }

    // The mirror stays off while this draws the window.
    internal bool Active
    {
        get => active;
        private set
        {
            active = value;
            Patches.KeepFirstPersonVisuals = value;
        }
    }

    private bool active;

    internal void Update(PlayerRig rig, Settings settings, bool vr)
    {
        var want = vr && settings.SmoothCamera && rig.Camera is not null && rig.HasPose;
        if (!want)
        {
            if (Active)
            {
                Active = false;
                if (camera is not null && camera != null)
                {
                    camera.enabled = false;
                    camera.targetTexture = null;
                }

                if (texture is not null && texture != null)
                    texture.Release();
                texture = null;
                log.Msg("smooth camera: off, the window mirrors the right eye");
            }

            return;
        }

        var eye = rig.Camera!;
        if (camera is null || camera == null)
            Build();
        if (camera is null || camera == null)
            return;

        // Only what the eye camera sees (layers) and how it clears: no
        // rendering options of its own.
        // While loading the window shows the same void as the headset (only
        // the loading screen, on blue); the eye's settings are copied again
        // once that is over.
        if (UiPresenter.VoidActive)
        {
            var mask = 1 << Props.UiLayer;
            if (camera.cullingMask != mask)
                camera.cullingMask = mask;
            if (camera.clearFlags != CameraClearFlags.SolidColor)
                camera.clearFlags = CameraClearFlags.SolidColor;
            if (camera.backgroundColor != UiPresenter.VoidBlue)
                camera.backgroundColor = UiPresenter.VoidBlue;
            copiedFrom = IntPtr.Zero;
        }
        else if (copiedFrom != eye.Pointer)
        {
            copiedFrom = eye.Pointer;
            // The eye camera leaves the washer's layer to a pass of its own
            // (drawn with the flat game's first-person tricks); this camera
            // draws that layer in the normal way.
            camera.cullingMask = eye.cullingMask | WasherLayers | SprayLayer;
            try
            {
                var data = eye.GetUniversalAdditionalCameraData();
                // The spray (light-beam and VFX water) fades against the depth
                // and colour textures; without them it draws nothing here.
                var own = camera.GetUniversalAdditionalCameraData();
                own.requiresDepthTexture = true;
                own.requiresColorTexture = true;
                own.renderPostProcessing = data.renderPostProcessing;
                log.Msg($"smooth camera: eye culling mask {eye.cullingMask}, renderer {data.m_RendererIndex}, "
                    + $"depth {data.requiresDepthOption}, colour {data.requiresColorOption}, post {data.renderPostProcessing}; "
                    + $"own mask {camera.cullingMask}, depth and colour textures on");
            }
            catch (Exception exception)
            {
                log.Warning($"smooth camera: camera data not copied ({exception.GetType().Name})");
            }
            camera.clearFlags = eye.clearFlags;
            camera.backgroundColor = eye.backgroundColor;
            camera.nearClipPlane = Mathf.Max(0.02f, eye.nearClipPlane);
            camera.farClipPlane = eye.farClipPlane;
        }

        var width = Mathf.Max(320, Screen.width);
        var height = Mathf.Max(180, Screen.height);
        if (texture is null || texture == null || texture.width != width || texture.height != height)
        {
            if (texture is not null && texture != null)
                texture.Release();
            texture = new RenderTexture(width, height, 24, RenderTextureFormat.Default) { name = "PowerWashVR Smooth Camera" };
            texture.Create();
            camera.targetTexture = texture;
        }

        camera.fieldOfView = settings.SmoothCameraFov;
        if (!camera.enabled)
            camera.enabled = true;

        // Smoothed in the play space (relative to the player's body), so
        // walking, turning and the game moving the player carry the camera
        // straight along; only the head's own shake is filtered. Level horizon.
        var headPosition = rig.HeadPosition;
        var headRotation = rig.HeadRotation;
        var forward = headRotation * Vector3.forward;
        var level = Quaternion.LookRotation(forward, Vector3.up);

        if (!placed || (headPosition - position).sqrMagnitude > 1f)
        {
            position = headPosition;
            rotation = level;
            placed = true;
        }
        else
        {
            var t = MathUtil.Damp(Mathf.Max(0.01f, settings.SmoothCameraSmoothing), Time.unscaledDeltaTime);
            position = Vector3.Lerp(position, headPosition, Mathf.Min(1f, t * 3f));
            rotation = Quaternion.Slerp(rotation, level, t);
        }

        Apply(rig);
        if (Time.unscaledTime >= nextSprayLook)
        {
            nextSprayLook = Time.unscaledTime + 4f;
            InspectSpray();
        }

        if (!Active)
        {
            Active = true;
            log.Msg($"smooth camera: on ({settings.SmoothCameraFov:0}° field of view)");
        }
    }

    private int sprayLogs;
    private float nextSprayLook;
    private readonly HashSet<IntPtr> sprayMaterialsSeen = new();

    // While the smooth camera is on: the spray's materials get the washer's
    // first-person projection switched off (the same fix that made the washer
    // show here), and the first few looks are logged in detail - where the
    // spray is, whether this camera's view contains it, its materials.
    private void InspectSpray()
    {
        try
        {
            var all = UnityEngine.Object.FindObjectsOfType(Il2CppInterop.Runtime.Il2CppType.Of<Il2CppFuturLab.PW2.SprayVfx>());
            for (var index = 0; all is not null && index < all.Length; index++)
            {
                var spray = all[index]?.TryCast<Il2CppFuturLab.PW2.SprayVfx>();
                if (spray is null || spray == null)
                    continue;

                var renderers = spray.GetComponentsInChildren<Renderer>(true);
                var log = Diag.Verbose && sprayLogs < 2 && spray.IsActive;
                var parts = new List<string>();
                var planes = GeometryUtility.CalculateFrustumPlanes(camera!);
                for (var r = 0; renderers is not null && r < renderers.Length; r++)
                {
                    var renderer = renderers[r];
                    if (renderer is null || renderer == null)
                        continue;
                    // The first-person spray draws on a layer of its own (20)
                    // that the eye camera gets through another route; this
                    // camera draws it directly.
                    var bit = 1 << renderer.gameObject.layer;
                    if ((camera!.cullingMask & bit) == 0 && BodyHider.Role(renderer).Length == 0)
                    {
                        camera.cullingMask |= bit;
                        this.log.Msg($"spray: layer {renderer.gameObject.layer} (\"{renderer.name}\") added to the smooth camera");
                    }

                    var materials = renderer.sharedMaterials;
                    var notes = new List<string>();
                    for (var slot = 0; materials is not null && slot < materials.Length; slot++)
                    {
                        var material = materials[slot];
                        if (material is null || material == null)
                            continue;
                        var first = sprayMaterialsSeen.Add(material.Pointer);
                        if (log || first)
                            notes.Add(Describe(material));
                        if (first)
                            Neutralise(material);
                    }

                    if (log && parts.Count < 12)
                    {
                        var bounds = renderer.bounds;
                        parts.Add($"{renderer.name}[{renderer.GetIl2CppType().Name} layer {renderer.gameObject.layer} "
                            + $"{(renderer.enabled ? "on" : "off")}{(renderer.forceRenderingOff ? " forced-off" : "")} "
                            + $"{(renderer.gameObject.activeInHierarchy ? "active" : "inactive")}{BodyHider.Role(renderer)} visible {renderer.isVisible} "
                            + $"bounds {bounds.center:F2} size {bounds.size:F2} in view {GeometryUtility.TestPlanesAABB(planes, bounds)} "
                            + $"dist {Vector3.Distance(bounds.center, camera!.transform.position):F2} | {string.Join(" | ", notes)}]");
                    }
                }

                if (log)
                {
                    sprayLogs++;
                    var path = spray.transform.parent is { } parent && parent != null ? Path(parent) : "-";
                    this.log.Msg($"spray ({sprayLogs}): \"{spray.name}\" under \"{path}\", smooth camera at "
                        + $"{camera!.transform.position:F2} near {camera.nearClipPlane:F2} far {camera.farClipPlane:F0} mask {camera.cullingMask}: "
                        + string.Join("; ", parts));
                }
            }
        }
        catch (Exception exception)
        {
            if (sprayLogs++ < 6)
                log.Msg($"spray: not inspected ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private static string Describe(Material material)
    {
        var keywords = material.shaderKeywords;
        var values = new List<string>();
        foreach (var (label, id) in new[] { ("fovToggle", WasherFixes.ToggleProperty), ("minDist", WasherFixes.MinDistProperty), ("depthScale", WasherFixes.ScaleProperty) })
        {
            if (id != 0 && material.HasProperty(id))
                values.Add($"{label} {material.GetFloat(id):0.##}");
        }

        return $"{material.shader.name} queue {material.renderQueue} keywords [{(keywords is null ? "" : string.Join(",", keywords))}] {string.Join(" ", values)}";
    }

    private void Neutralise(Material material)
    {
        var changed = false;
        if (WasherFixes.ToggleProperty != 0 && material.HasProperty(WasherFixes.ToggleProperty) && material.GetFloat(WasherFixes.ToggleProperty) != 0f)
        {
            material.SetFloat(WasherFixes.ToggleProperty, 0f);
            changed = true;
        }

        var keywords = material.shaderKeywords;
        for (var key = 0; keywords is not null && key < keywords.Length; key++)
        {
            if (keywords[key] is { } name && name.Contains("FOV", StringComparison.OrdinalIgnoreCase))
            {
                material.DisableKeyword(name);
                changed = true;
            }
        }

        if ((WasherFixes.MinDistProperty != 0 && material.HasProperty(WasherFixes.MinDistProperty))
            || (WasherFixes.ScaleProperty != 0 && material.HasProperty(WasherFixes.ScaleProperty)))
        {
            WasherFixes.ExtraMaterials.Add(material);
            changed = true;
        }

        if (changed)
            log.Msg($"spray: first-person projection switched off on \"{material.name}\"");
    }

    private static string Path(Transform node)
    {
        var names = new List<string>();
        for (var current = node; current is not null && current != null && names.Count < 8; current = current.parent)
            names.Add(current.name);
        names.Reverse();
        return string.Join("/", names);
    }

    // Also run just before rendering, with the play space where it is now.
    internal void Reapply(PlayerRig rig)
    {
        if (Active && rig.HasPose)
            Apply(rig);
    }

    private void Apply(PlayerRig rig)
    {
        if (camera is null || camera == null)
            return;
        var toWorld = rig.TrackingToWorldRotation;
        camera.transform.SetPositionAndRotation(rig.TrackingToWorldPosition + toWorld * position, toWorld * rotation);
    }

    private void Build()
    {
        try
        {
            root = new GameObject("PowerWashVR Smooth Camera");
            UnityEngine.Object.DontDestroyOnLoad(root);
            camera = root.AddComponent<Camera>();
            BodyHider.SmoothCameraPointer = camera.Pointer;
            camera.stereoTargetEye = StereoTargetEyeMask.None;
            camera.depth = -50f;
            camera.enabled = false;
            placed = false;
            copiedFrom = IntPtr.Zero;
        }
        catch (Exception exception)
        {
            log.Warning($"smooth camera: not created ({exception.GetType().Name}: {exception.Message})");
            camera = null;
        }
    }
}
