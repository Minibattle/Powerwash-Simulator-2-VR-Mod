using System.Linq;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;

namespace PowerWashVR.Game;

// Rendering settings for VR, all aimed at frame time.
//
//  - Eye-buffer resolution scale and the desktop mirror mode.
//  - Renderer features off that cost a lot and draw into one eye only
//    (the volumetric fog and light scattering passes keep a single per-camera
//    buffer, so in stereo they are correct in the last eye rendered only).
//  - Post effects off that are wrong or uncomfortable in a headset.
//  - The player's rigidbody interpolated, so walking is smooth at 90-120 Hz
//    instead of stepping at the physics rate.
//
// None of this runs per frame. Scans happen after scene loads and on a slow
// timer, because the game re-enables some features itself.
internal sealed class RenderTweaks
{
    private readonly MelonLogger.Instance log;
    private float nextScan = float.MaxValue;
    private int scansLeft;
    private string appliedMirror = "";
    private IntPtr smoothedBody;
    private IntPtr checkedController;

    internal RenderTweaks(MelonLogger.Instance log)
    {
        this.log = log;
    }

    internal void OnVrStarted(Settings settings)
    {
        try
        {
            XRSettings.eyeTextureResolutionScale = settings.ResolutionScale;
        }
        catch (Exception exception)
        {
            log.Warning($"render: resolution scale not applied ({exception.GetType().Name})");
        }

        appliedMirror = "";
        ResidentDrawer(settings);
        Fsr(settings);
        ScheduleScans();
    }

    // GPU Resident Drawer: instanced drawing with GPU occlusion culling. The
    // culling is worked out for one view, so in multi-pass stereo the other
    // eye loses what the first eye could not see; off unless asked for.
    private void ResidentDrawer(Settings settings)
    {
        try
        {
            var assets = new List<RenderPipelineAsset?> { GraphicsSettings.defaultRenderPipeline, QualitySettings.renderPipeline,
                GraphicsSettings.currentRenderPipeline };
            var seen = new HashSet<IntPtr>();
            foreach (var asset in assets)
            {
                var urp = asset?.TryCast<UniversalRenderPipelineAsset>();
                if (urp is null || urp == null || !seen.Add(urp.Pointer))
                    continue;
                log.Msg($"render: \"{urp.name}\" GPU resident drawer {urp.m_GPUResidentDrawerMode}, GPU occlusion culling "
                    + $"{urp.m_GPUResidentDrawerEnableOcclusionCullingInCameras}");
                if (settings.GpuResidentDrawer)
                    continue;
                urp.m_GPUResidentDrawerEnableOcclusionCullingInCameras = false;
                urp.m_GPUResidentDrawerMode = GPUResidentDrawerMode.Disabled;
                log.Msg($"render: \"{urp.name}\" GPU resident drawer switched off");
            }
        }
        catch (Exception exception)
        {
            log.Warning($"render: GPU resident drawer not changed ({exception.GetType().Name}: {exception.Message})");
        }
    }

    // AMD FSR 1 (URP's own): the scene renders at a lower scale and is
    // upscaled and sharpened to the eye buffer. Fixed for the session.
    private void Fsr(Settings settings) => ApplyFsr(Settings.FsrScale(settings.Fsr), log);

    // The filter each URP asset had before FSR changed it (put back when off).
    private static readonly Dictionary<IntPtr, (UpscalingFilterSelection Filter, float Scale)> beforeFsr = new();

    // FSR 1 at this render scale (0 = off). VR applies it once at the start;
    // flat play whenever the setting changes.
    internal static void ApplyFsr(float scale, MelonLogger.Instance log)
    {
        Patches.FsrScale = scale;
        try
        {
            var assets = new List<RenderPipelineAsset?> { GraphicsSettings.defaultRenderPipeline, QualitySettings.renderPipeline,
                GraphicsSettings.currentRenderPipeline };
            var seen = new HashSet<IntPtr>();
            foreach (var asset in assets)
            {
                var urp = asset?.TryCast<UniversalRenderPipelineAsset>();
                if (urp is null || urp == null || !seen.Add(urp.Pointer))
                    continue;
                if (scale > 0f)
                {
                    if (!beforeFsr.ContainsKey(urp.Pointer))
                        beforeFsr[urp.Pointer] = (urp.upscalingFilter, urp.renderScale);
                    urp.upscalingFilter = UpscalingFilterSelection.FSR;
                    urp.fsrOverrideSharpness = true;
                    urp.fsrSharpness = 0.9f;
                    Patches.ForceRenderScale(urp, scale);
                    log.Msg($"render: FSR 1 on \"{urp.name}\" (render scale {urp.renderScale:0.##}, filter {urp.upscalingFilter})");
                }
                else if (beforeFsr.TryGetValue(urp.Pointer, out var before))
                {
                    beforeFsr.Remove(urp.Pointer);
                    urp.upscalingFilter = before.Filter;
                    Patches.ForceRenderScale(urp, Patches.RequestedRenderScale > 0f ? Patches.RequestedRenderScale : before.Scale);
                    log.Msg($"render: FSR 1 off on \"{urp.name}\" (render scale {urp.renderScale:0.##}, filter {urp.upscalingFilter})");
                }
            }

            if (scale <= 0f && seen.Count > 0 && beforeFsr.Count == 0)
                log.Msg("render: FSR 1 off");
        }
        catch (Exception exception)
        {
            log.Warning($"render: FSR 1 not applied ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private void LogTerrains()
    {
        try
        {
            var terrains = Terrain.activeTerrains;
            if (terrains is null)
                return;
            // A terrain with more than four texture layers draws the rest in
            // an extra "add" pass, which in multi-pass stereo reaches one eye
            // only (the green ground). Drawn instanced it vanished from both.
            // At basemap distance 0 the terrain uses its baked base map, which
            // holds every layer in a single pass: the same in both eyes.
            for (var index = 0; index < terrains.Length; index++)
            {
                var terrain = terrains[index];
                if (terrain is null || terrain == null || !fixedTerrains.Add(terrain.Pointer))
                    continue;
                var layers = terrain.terrainData is { } data && data != null && data.terrainLayers is { } list ? list.Length : 0;
                if (terrain.drawInstanced)
                    terrain.drawInstanced = false;
                if (layers > 4)
                {
                    terrain.basemapDistance = 0f;
                    log.Msg($"render: terrain \"{terrain.name}\" has {layers} texture layers; drawn from its base map (all layers, both eyes)");
                }
                else
                {
                    log.Msg($"render: terrain \"{terrain.name}\" has {layers} texture layer(s); left as it is");
                }
            }

            if (terrains.Length == lastTerrains)
                return;
            lastTerrains = terrains.Length;
            for (var index = 0; index < terrains.Length && index < 6; index++)
            {
                var terrain = terrains[index];
                if (terrain is null || terrain == null)
                    continue;
                var data = terrain.terrainData;
                var material = terrain.materialTemplate;
                log.Msg($"render: terrain \"{terrain.name}\" material \"{(material is null || material == null ? "none" : material.name)}\" "
                    + $"shader \"{(material is null || material == null || material.shader is null ? "default" : material.shader.name)}\", basemap distance {terrain.basemapDistance}");
                log.Msg($"render: terrain \"{terrain.name}\" draw instanced {terrain.drawInstanced}, details "
                    + $"{(data is null ? 0 : data.detailPrototypes.Length)} (density {terrain.detailObjectDensity}, "
                    + $"distance {terrain.detailObjectDistance}), trees {(data is null ? 0 : data.treeInstanceCount)}, draw details {terrain.drawTreesAndFoliage}");
            }
        }
        catch (Exception exception)
        {
            log.Msg($"render: terrain scan failed ({exception.GetType().Name})");
        }
    }

    private int lastTerrains = -1;
    private readonly HashSet<IntPtr> fixedTerrains = new();

    internal void OnSceneLoaded() => ScheduleScans();

    // A few scans shortly after a load (content streams in), then a slow
    // heartbeat that catches the game switching things back on.
    private void ScheduleScans()
    {
        scansLeft = 3;
        nextScan = Time.unscaledTime + 1f;
    }

    internal void Update(Settings settings, PlayerRig rig)
    {
        ApplyMirror(settings);

        if (settings.SmoothPlayerBody)
            SmoothBody(rig);

        var now = Time.unscaledTime;
        if (now < nextScan)
            return;

        if (scansLeft > 0)
        {
            scansLeft--;
            nextScan = now + 3f;
        }
        else
        {
            nextScan = now + 60f;
        }

        // Always off, whatever the saved settings say: screen-space passes
        // that draw into one eye only in multi-pass stereo.
        var features = settings.DisabledRenderFeatures.Concat(OneEyeFeatures);
        if (!settings.AmbientOcclusion)
            features = features.Append("ScreenSpaceAmbientOcclusion");
        DisableFeatures(features.ToArray());
        if (!settings.LightBeams)
            DisableBeams();
        LogTerrains();
        DisablePostEffects(settings.DisabledPostEffects.Concat(OneEyeEffects).ToArray());
    }

    // The window is drawn by the spectator camera; the XR mirror blit is
    // turned off so it does not compete with it (or cost anything).
    private void ApplyMirror(Settings settings)
    {
        if (appliedMirror.Length > 0)
            return;

        appliedMirror = "none";
        try
        {
            XRSettings.gameViewRenderMode = GameViewRenderMode.None;
            XRSettings.showDeviceView = false;
        }
        catch (Exception exception)
        {
            log.Warning($"render: mirror mode not applied ({exception.GetType().Name})");
        }
    }

    // ModulatedOutline is off: it costs a full-screen pass per eye (tried on
    // in 2.4.0 for the last-dirt highlight; FPS dropped).
    private static readonly string[] OneEyeFeatures = { "Buto", "LightScattering", "SceneOverlay", "ModulatedOutline" };
    private static readonly string[] OneEyeEffects = { "ScreenSpaceLensFlare", "LensDistortion", "PaniniProjection" };
    private bool featuresListed;

    // Volumetric light beams (and the dust that floats in them) are drawn by
    // their own shader, which only reaches one eye in multi-pass stereo.
    private void DisableBeams()
    {
        try
        {
            var beams = 0;
            var dust = 0;
            var found = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppVLB.VolumetricLightBeam>());
            for (var index = 0; found is not null && index < found.Length; index++)
            {
                var beam = found[index]?.TryCast<Behaviour>();
                if (beam is null || beam == null)
                    continue;
                beam.gameObject.SetActive(false);
                beams++;
            }

            var particles = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppVLB.VolumetricDustParticles>());
            for (var index = 0; particles is not null && index < particles.Length; index++)
            {
                var item = particles[index]?.TryCast<Behaviour>();
                if (item is null || item == null)
                    continue;
                item.enabled = false;
                dust++;
            }

            var grass = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<Il2CppFuturLab.PW2.ShellTextureGeometry>());
            var grassCount = grass is null ? 0 : grass.Length;
            if (grassCount != lastGrass)
            {
                for (var index = 0; grass is not null && index < grass.Length && index < 6; index++)
                {
                    var patch = grass[index]?.TryCast<Il2CppFuturLab.PW2.ShellTextureGeometry>();
                    if (patch is null || patch == null)
                        continue;
                    var mesh = patch.m_meshRenderer;
                    log.Msg($"render: grass \"{patch.name}\" enabled {patch.isActiveAndEnabled} mode {patch.m_grassMode} "
                        + $"hide mesh {patch.m_hideMesh} renderer {(mesh is null || mesh == null ? "none" : $"{mesh.enabled} \"{mesh.sharedMaterial?.name}\"")}");
                }
            }
            if (beams + dust > 0 || grassCount != lastGrass)
                log.Msg($"render: {beams} light beam(s) and {dust} dust effect(s) switched off; {grassCount} shell-grass patch(es) present "
                    + $"(not drawn in VR, grass skips so far {Patches.GrassSkips})");
            lastGrass = grassCount;
        }
        catch (Exception exception)
        {
            log.Warning($"render: light beam scan failed ({exception.GetType().Name})");
        }
    }

    private int lastGrass = -1;

    // The game's material override passes draw chosen objects with another
    // material. One that draws into its own screen-sized texture (read back
    // by other shaders, e.g. a ground overlay) makes that texture for one
    // eye only in multi-pass stereo, so those are switched off.
    private bool OverrideToOneEyeTarget(ScriptableRendererFeature feature)
    {
        try
        {
            var over = feature.TryCast<Il2CppFuturLab.PW2.ObjectMaterialOverrideFeature>();
            var data = over?.m_passData;
            if (data is null)
                return false;
            var material = data.Material;
            var description = $"\"{feature.name}\" material \"{(material is null || material == null ? "none" : material.name)}\" "
                + $"target {(data.CustomRenderTarget ? data.RenderTargetName : "camera")} layers {data.CullingMask.value} event {data.PassEvent}";
            if (!data.CustomRenderTarget)
            {
                if (overridesLogged.Add(feature.Pointer))
                    log.Msg($"render: material override {description} left on");
                return false;
            }

            // Switching these off did not change the one-eye ground (v1.9.0
            // test), and other effects read their texture: left on.
            if (overridesLogged.Add(feature.Pointer))
                log.Msg($"render: material override {description} left on");
            return false;
        }
        catch (Exception exception)
        {
            log.Msg($"render: material override not inspected ({exception.GetType().Name})");
            return false;
        }
    }

    private readonly HashSet<IntPtr> overridesLogged = new();

    private static string Details(ScriptableRendererFeature feature)
    {
        try
        {
            var objects = feature.TryCast<RenderObjects>();
            var settings = objects?.settings;
            if (settings is null)
                return "";
            var material = settings.overrideMaterial;
            var camera = settings.cameraSettings;
            return $" (event {settings.Event}, layers {settings.filterSettings.LayerMask.value}, queue {settings.filterSettings.RenderQueueType}, "
                + $"override \"{(material is null || material == null ? "none" : material.name)}\", camera override "
                + $"{(camera is not null && camera.overrideCamera ? $"fov {camera.cameraFieldOfView}, offset {camera.offset}" : "none")})";
        }
        catch
        {
            return "";
        }
    }

    private void DisableFeatures(string[] names)
    {
        if (names.Length == 0)
            return;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<ScriptableRendererFeature>());
            for (var index = 0; index < all.Length; index++)
            {
                var feature = all[index]?.TryCast<ScriptableRendererFeature>();
                if (feature is null || !feature.isActive)
                    continue;

                var type = feature.GetIl2CppType().Name;
                if (type == "ObjectMaterialOverrideFeature" && OverrideToOneEyeTarget(feature))
                    continue;
                if (!Matches(type, names))
                {
                    if (!featuresListed)
                        log.Msg($"render: {type} \"{feature.name}\" left on{Details(feature)}");
                    continue;
                }

                feature.SetActive(false);
                log.Msg($"render: {type} switched off");
            }

            featuresListed = true;
        }
        catch (Exception exception)
        {
            featuresListed = true;
            log.Warning($"render: renderer feature scan failed ({exception.GetType().Name})");
        }
    }

    private void DisablePostEffects(string[] names)
    {
        if (names.Length == 0)
            return;

        try
        {
            var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<VolumeComponent>());
            var count = 0;
            for (var index = 0; index < all.Length; index++)
            {
                var component = all[index]?.TryCast<VolumeComponent>();
                if (component is null || !component.active)
                    continue;

                if (!Matches(component.GetIl2CppType().Name, names))
                    continue;

                component.active = false;
                count++;
            }

            if (count > 0)
                log.Msg($"render: {count} post effect(s) switched off");
        }
        catch (Exception exception)
        {
            log.Warning($"render: post effect scan failed ({exception.GetType().Name})");
        }
    }

    private void SmoothBody(PlayerRig rig)
    {
        var player = rig.Controller;
        if (player is null || player.Pointer == checkedController)
            return;

        checkedController = player.Pointer;

        try
        {
            var physical = player.m_controller;
            if (physical is null || physical == null)
                return;

            var body = physical.Rigidbody;
            if (body is null || body == null || body.Pointer == smoothedBody)
                return;

            smoothedBody = body.Pointer;
            if (body.interpolation == RigidbodyInterpolation.None)
            {
                body.interpolation = RigidbodyInterpolation.Interpolate;
                log.Msg("render: player body interpolation on");
            }
        }
        catch (Exception exception)
        {
            smoothedBody = new IntPtr(-1);
            log.Warning($"render: player body not reachable ({exception.GetType().Name})");
        }
    }

    private static bool Matches(string name, string[] fragments)
    {
        for (var index = 0; index < fragments.Length; index++)
        {
            if (name.Contains(fragments[index], StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
