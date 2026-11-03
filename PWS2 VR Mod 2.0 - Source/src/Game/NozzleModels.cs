using Il2CppFuturLab.PW2;
using MelonLoader;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace PowerWashVR.Game;

// The game's own nozzle models, for the belt.
//
// Each nozzle's prefab is instantiated through Addressables under an inactive
// holder - so none of its scripts ever wake up - and only its meshes are
// copied into a plain, static model. One load per nozzle, kept for the
// session.
internal sealed class NozzleModels
{
    private readonly MelonLogger.Instance log;
    private GameObject? holder;
    private readonly Dictionary<int, Request> requests = new();

    private sealed class Request
    {
        internal AsyncOperationHandle<GameObject>? Handle;
        internal GameObject? Model;
        internal bool Failed;
        internal float Deadline;
        internal BaseEquipmentData? Data;
    }

    private readonly float maxLength;

    internal NozzleModels(MelonLogger.Instance log, float maxLength = 0.25f)
    {
        this.log = log;
        this.maxLength = maxLength;
    }

    // Loaded ahead of need (when a level is loaded): queued and started one a
    // frame (Tick), so a level start does not hitch on dozens at once.
    private readonly Queue<BaseEquipmentData> preload = new();
    private readonly HashSet<int> queued = new();

    internal void Preload(BaseEquipmentData item)
    {
        if (item is null || item == null || requests.ContainsKey(item.UniqueId) || !queued.Add(item.UniqueId))
            return;
        preload.Enqueue(item);
    }

    internal void Tick()
    {
        while (preload.Count > 0)
        {
            var item = preload.Dequeue();
            queued.Remove(item.UniqueId);
            if (item is null || item == null || requests.ContainsKey(item.UniqueId))
                continue;
            Get(item);
            return;
        }

        // Finished loads become models without being asked for.
        if (Time.frameCount % 10 == 0)
        {
            foreach (var (_, request) in requests)
            {
                if (request.Model is null && !request.Failed && request.Handle is not null && request.Data is not null)
                    Poll(request.Data, request);
            }
        }
    }

    // A ready model template (inactive), or null while it loads or if it
    // cannot be loaded.
    internal GameObject? Get(BaseEquipmentData nozzle)
    {
        var id = nozzle.UniqueId;
        if (!requests.TryGetValue(id, out var request))
        {
            request = new Request { Data = nozzle };
            requests[id] = request;
            Start(nozzle, request);
        }

        if (request.Model is not null || request.Failed)
            return request.Model;

        Poll(nozzle, request);
        return request.Model;
    }

    private void Start(BaseEquipmentData nozzle, Request request)
    {
        try
        {
            EnsureHolder();
            var reference = nozzle.Prefab;
            if (reference is null || !reference.RuntimeKeyIsValid())
            {
                request.Failed = true;
                return;
            }

            request.Handle = Addressables.InstantiateAsync(reference.RuntimeKey, holder!.transform, false, true);
            request.Deadline = Time.unscaledTime + 15f;
        }
        catch (Exception exception)
        {
            request.Failed = true;
            log.Msg($"belt: nozzle model not loadable ({exception.GetType().Name}); using a plain token");
        }
    }

    private void Poll(BaseEquipmentData nozzle, Request request)
    {
        var handle = request.Handle;
        if (handle is null)
        {
            request.Failed = true;
            return;
        }

        try
        {
            if (!handle.IsDone)
            {
                if (Time.unscaledTime > request.Deadline)
                    request.Failed = true;
                return;
            }

            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result is null || handle.Result == null)
            {
                request.Failed = true;
                return;
            }

            request.Model = Copy(handle.Result, nozzle.name);
            UnityEngine.Object.Destroy(handle.Result);
            request.Handle = null;
            if (request.Model is null)
                request.Failed = true;
        }
        catch (Exception exception)
        {
            request.Failed = true;
            log.Msg($"belt: nozzle model failed ({exception.GetType().Name})");
        }
    }

    // Meshes only, re-rooted so the model is centred and its long axis is +Z.
    private GameObject? Copy(GameObject source, string name)
    {
        var filters = source.GetComponentsInChildren<MeshFilter>(true);
        if (filters is null || filters.Length == 0)
            return null;

        var model = new GameObject($"Nozzle model {name}");
        model.SetActive(false);
        model.transform.SetParent(holder!.transform, false);
        var root = source.transform;
        var bounds = new Bounds();
        var any = false;

        for (var index = 0; index < filters.Length; index++)
        {
            var filter = filters[index];
            var mesh = filter is null || filter == null ? null : filter.sharedMesh;
            var renderer = filter is null ? null : filter.GetComponent<MeshRenderer>();
            if (mesh is null || mesh == null || renderer is null || renderer == null)
                continue;

            var part = new GameObject(filter.name);
            part.transform.SetParent(model.transform, false);
            var transform = filter.transform;
            part.transform.localPosition = root.InverseTransformPoint(transform.position);
            part.transform.localRotation = Quaternion.Inverse(root.rotation) * transform.rotation;
            part.transform.localScale = transform.lossyScale;
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var copy = part.AddComponent<MeshRenderer>();
            copy.sharedMaterials = Plain(renderer.sharedMaterials);
            copy.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

            var local = mesh.bounds;
            var centre = part.transform.localPosition + part.transform.localRotation
                * Vector3.Scale(local.center, part.transform.localScale);
            var size = Vector3.Scale(local.size, part.transform.localScale);
            if (!any)
            {
                bounds = new Bounds(centre, size);
                any = true;
            }
            else
            {
                bounds.Encapsulate(new Bounds(centre, size));
            }
        }

        if (!any)
        {
            UnityEngine.Object.Destroy(model);
            return null;
        }

        // Centre it and turn its longest side to +Z.
        var extent = bounds.size;
        var longest = extent.x >= extent.y && extent.x >= extent.z ? Vector3.right
            : extent.y >= extent.z ? Vector3.up : Vector3.forward;
        var turn = Quaternion.FromToRotation(longest, Vector3.forward);
        var wrapper = new GameObject($"Nozzle {name}");
        wrapper.SetActive(false);
        wrapper.transform.SetParent(holder!.transform, false);
        model.transform.SetParent(wrapper.transform, false);
        model.transform.localRotation = turn;
        model.transform.localPosition = -(turn * bounds.center);

        // A nozzle is a few centimetres; anything far off is scaled to fit.
        var length = Mathf.Max(extent.x, Mathf.Max(extent.y, extent.z));
        if (length > maxLength || length < 0.02f)
            model.transform.localScale = Vector3.one * (0.08f / Mathf.Max(1e-4f, length));
        model.SetActive(true);
        return wrapper;
    }

    // The nozzles share the first-person washer's materials, which the flat
    // game draws with its own first-person projection (in the eye of the
    // player holding the washer, not where the model is): a copy of each
    // material with that switched off, so the model draws where it is for
    // everyone (other players included, without VR).
    private readonly Dictionary<IntPtr, Material> plain = new();

    private static int toggleId;

    // The first-person switch's property id: known in VR, otherwise read
    // from the local player's first-person visuals.
    private static int ToggleId()
    {
        if (WasherFixes.ToggleProperty != 0)
            return WasherFixes.ToggleProperty;
        if (toggleId != 0)
            return toggleId;
        try
        {
            var visuals = UnityEngine.Object.FindObjectOfType<CharacterVisualsFirstPerson>();
            if (visuals is not null && visuals != null)
                toggleId = visuals.m_fovTogglePropID;
        }
        catch
        {
            // Keywords and names only then.
        }

        return toggleId;
    }

    private Material[] Plain(Material[] source)
    {
        var result = new Material[source.Length];
        for (var index = 0; index < source.Length; index++)
        {
            var material = source[index];
            if (material is null || material == null)
                continue;
            if (!plain.TryGetValue(material.Pointer, out var copy) || copy == null)
            {
                copy = new Material(material) { name = material.name + " (plain)" };
                try
                {
                    var keywords = copy.shaderKeywords;
                    for (var key = 0; keywords is not null && key < keywords.Length; key++)
                    {
                        if (!string.IsNullOrEmpty(keywords[key]) && keywords[key].Contains("FOV", StringComparison.OrdinalIgnoreCase))
                            copy.DisableKeyword(keywords[key]);
                    }

                    var shader = copy.shader;
                    var count = shader is null ? 0 : shader.GetPropertyCount();
                    for (var property = 0; property < count; property++)
                    {
                        var name = shader!.GetPropertyName(property);
                        var type = shader.GetPropertyType(property);
                        if (name is null || (type != UnityEngine.Rendering.ShaderPropertyType.Float && type != UnityEngine.Rendering.ShaderPropertyType.Range))
                            continue;
                        if (name.Contains("FOV", StringComparison.OrdinalIgnoreCase) && name.Contains("Toggle", StringComparison.OrdinalIgnoreCase))
                            copy.SetFloat(name, 0f);
                    }

                    var toggle = ToggleId();
                    if (toggle != 0 && copy.HasProperty(toggle))
                        copy.SetFloat(toggle, 0f);
                }
                catch
                {
                    // Drawn as it is then.
                }

                plain[material.Pointer] = copy;
            }

            result[index] = copy;
        }

        return result;
    }

    private void EnsureHolder()
    {
        if (holder is not null && holder != null)
            return;

        holder = new GameObject("PowerWashVR Nozzle Models");
        holder.SetActive(false);
        UnityEngine.Object.DontDestroyOnLoad(holder);
    }
}
