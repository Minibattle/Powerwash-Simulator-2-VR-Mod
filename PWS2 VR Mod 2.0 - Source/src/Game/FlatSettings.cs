using Il2CppFuturLab;
using Il2CppFuturLab.PW2;
using Il2CppFuturLab.PW2.UI;
using Il2CppFuturLab.Settings;
using Il2CppInterop.Runtime;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

// Without VR (SteamVR not running, or no headset), the game's own settings
// keep one row of the mod's: "Auto Detect SteamVR", first on the General page. It is a copy of one of the game's toggle rows with its setting
// binding removed; the mouse or pad toggles it as usual and the value is read
// back here twice a second.
internal sealed class FlatSettings
{
    private const string Label = "Auto Detect SteamVR";

    private readonly MelonLogger.Instance log;
    private GameObject? row;
    private UnityEngine.UI.Toggle? toggle;
    private TextMeshProUGUI? text;
    private float nextCheck;
    private bool built;

    internal FlatSettings(MelonLogger.Instance log)
    {
        this.log = log;
    }

    // FSR 1 for flat play: a dropdown first on the game's Video page, off
    // by default, applied straight away (and again if the game swaps its
    // render pipeline, which a quality change does).
    private const string FsrLabel = "FSR 1";
    private static readonly string[] FsrOptions = { "Off", "Ultra Quality", "Quality", "Balanced", "Performance" };
    private GameObject? fsrRow;
    private Il2CppTMPro.TMP_Dropdown? fsrDropdown;
    private TextMeshProUGUI? fsrText;
    private float nextFsrCheck;
    private bool fsrApplied, fsrFailed;

    private void UpdateFsr(Settings settings, float now)
    {
        if (now >= nextFsrCheck)
        {
            nextFsrCheck = now + 2f;
            var want = Settings.FsrScale(settings.FlatFsr);
            var urp = UnityEngine.Rendering.GraphicsSettings.currentRenderPipeline
                ?.TryCast<UnityEngine.Rendering.Universal.UniversalRenderPipelineAsset>();
            var onNow = urp is not null && urp != null && urp.upscalingFilter == UnityEngine.Rendering.Universal.UpscalingFilterSelection.FSR
                && Mathf.Abs(urp.renderScale - want) < 1e-3f;
            if (want > 0f && !onNow)
                RenderTweaks.ApplyFsr(want, log);
            else if (want <= 0f && fsrApplied)
                RenderTweaks.ApplyFsr(0f, log);
            fsrApplied = want > 0f;
        }

        if (fsrRow is not null && fsrRow != null)
        {
            if (fsrText is not null && fsrText != null && fsrText.text != FsrLabel)
                fsrText.text = FsrLabel;
            if (fsrDropdown is not null && fsrDropdown != null)
            {
                var index = fsrDropdown.value;
                if (index != settings.FlatFsr && index >= 0 && index < FsrOptions.Length)
                {
                    settings.SetFlatFsr(index);
                    log.Msg($"settings: {FsrLabel} -> {FsrOptions[index]}");
                    nextFsrCheck = 0f;
                }
            }

            FirstIn(fsrRow);
            return;
        }

        if (fsrFailed)
            return;
        var anchor = Row(SettingType.RenderScale);
        if (anchor is null || !anchor.gameObject.activeInHierarchy)
            return;
        BuildFsr(anchor, settings);
    }

    private void BuildFsr(SettingUIElement anchor, Settings settings)
    {
        SettingDropdown? template = null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SettingDropdown>());
        for (var index = 0; all is not null && index < all.Length; index++)
        {
            var candidate = all[index]?.TryCast<SettingDropdown>();
            if (candidate is not null && candidate != null && candidate.gameObject.scene.IsValid() && candidate.m_dropdown is { } d && d != null
                && candidate.TryCast<SettingLanguageDropdown>() is null && !candidate.gameObject.name.StartsWith("PowerWashVR", StringComparison.Ordinal))
            {
                template = candidate;
                break;
            }
        }

        if (template is null)
            return;

        var holder = new GameObject("PowerWashVR Holder");
        holder.SetActive(false);
        try
        {
            var copy = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false);
            copy.name = $"PowerWashVR {FsrLabel}";
            var element = copy.GetComponent<SettingUIElement>();
            fsrText = element?.m_option?.GetComponent<TextMeshProUGUI>();
            fsrDropdown = element?.TryCast<SettingDropdown>()?.m_dropdown;
            var bound = copy.GetComponentsInChildren<SettingUIElement>(true);
            for (var index = 0; bound is not null && index < bound.Length; index++)
                UnityEngine.Object.DestroyImmediate(bound[index]);
            var localizers = copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true);
            for (var index = 0; localizers is not null && index < localizers.Length; index++)
                UnityEngine.Object.DestroyImmediate(localizers[index]);

            copy.transform.SetParent(anchor.transform.parent, false);
            copy.SetActive(true);
            if (fsrText is not null && fsrText != null)
                fsrText.text = FsrLabel;
            if (fsrDropdown is not null && fsrDropdown != null)
            {
                fsrDropdown.ClearOptions();
                var options = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var option in FsrOptions)
                    options.Add(option);
                fsrDropdown.AddOptions(options);
                fsrDropdown.interactable = true;
                fsrDropdown.SetValueWithoutNotify(settings.FlatFsr);
            }

            fsrRow = copy;
            FirstIn(copy);
            log.Msg($"settings: \"{FsrLabel}\" added to the game's Video settings (copied from \"{template.name}\")");
        }
        catch (Exception exception)
        {
            fsrFailed = true;
            log.Msg($"settings: {FsrLabel} row not added ({exception.GetType().Name}: {exception.Message})");
        }
        finally
        {
            UnityEngine.Object.Destroy(holder);
        }
    }

    // First among the setting rows of its list.
    private static void FirstIn(GameObject node)
    {
        var parent = node.transform.parent;
        if (parent is null)
            return;
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child is null || child == null || child.Pointer == node.transform.Pointer)
                continue;
            if (child.GetComponent<SettingUIElement>() is not null)
            {
                if (node.transform.GetSiblingIndex() > index)
                    node.transform.SetSiblingIndex(index);
                return;
            }
        }
    }

    internal void Update(Settings settings)
    {
        // Clicks: the copied row lost the game's click handling with its
        // setting binding, so a click on the row (anywhere on it) flips the
        // value here.
        if (row is not null && row != null && row.activeInHierarchy)
        {
            try
            {
                if (UnityEngine.Input.GetMouseButtonDown(0) && Over(UnityEngine.Input.mousePosition))
                    Flip(settings);
            }
            catch
            {
                // No legacy mouse input: the toggle's own click is read below.
            }
        }

        var now = Time.unscaledTime;
        if (now < nextCheck)
            return;
        nextCheck = now + 0.5f;
        try
        {
            UpdateFsr(settings, now);
        }
        catch (Exception exception)
        {
            if (!fsrFailed)
            {
                fsrFailed = true;
                log.Msg($"settings: {FsrLabel} failed ({exception.GetType().Name}: {exception.Message})");
            }
        }

        try
        {
            if (row is not null && row != null)
            {
                if (text is not null && text != null && text.text != Label)
                    text.text = Label;
                if (toggle is not null && toggle != null && Time.unscaledTime - lastFlip < 0.6f)
                {
                    // Just flipped by a click here: the toggle shows that.
                    if (toggle.isOn != settings.AutoDetectSteamVR)
                        toggle.SetIsOnWithoutNotify(settings.AutoDetectSteamVR);
                }
                else if (toggle is not null && toggle != null && toggle.isOn != settings.AutoDetectSteamVR)
                {
                    // Toggled by the toggle's own click (pad or mouse).
                    settings.SetAutoDetectSteamVR(toggle.isOn);
                    log.Msg($"settings: {Label} -> {(toggle.isOn ? "On" : "Off")} (takes effect next start)");
                }

                First();
                return;
            }

            // Only while the General page (one of its rows) is on screen.
            var anchor = GeneralRow();

            if (anchor is null)
                return;
            Build(anchor, settings);
        }
        catch (Exception exception)
        {
            if (!built)
            {
                built = true;
                log.Msg($"settings: {Label} row not added ({exception.GetType().Name}: {exception.Message})");
            }
        }
    }

    private float lastFlip = -1f;

    private void Flip(Settings settings)
    {
        // One click may also reach the toggle itself: one flip per click.
        if (Time.unscaledTime - lastFlip < 0.2f)
            return;
        lastFlip = Time.unscaledTime;
        var value = !settings.AutoDetectSteamVR;
        settings.SetAutoDetectSteamVR(value);
        if (toggle is not null && toggle != null)
            toggle.SetIsOnWithoutNotify(value);
        log.Msg($"settings: {Label} -> {(value ? "On" : "Off")} (takes effect next start)");
    }

    private bool Over(Vector3 screen)
    {
        var rect = row!.transform.TryCast<RectTransform>();
        if (rect is null)
            return false;
        var canvas = row.GetComponentInParent<Canvas>();
        var root = canvas is not null && canvas != null ? canvas.rootCanvas : null;
        var camera = root is not null && root.renderMode != RenderMode.ScreenSpaceOverlay ? root.worldCamera : null;
        return RectTransformUtility.RectangleContainsScreenPoint(rect, new Vector2(screen.x, screen.y), camera);
    }

    // First on the General page: before every other setting row there.
    private void First()
    {
        var parent = row!.transform.parent;
        if (parent is null)
            return;
        var first = -1;
        for (var index = 0; index < parent.childCount; index++)
        {
            var child = parent.GetChild(index);
            if (child is null || child == null || child.Pointer == row.transform.Pointer)
                continue;
            if (child.GetComponent<SettingUIElement>() is not null)
            {
                first = index;
                break;
            }
        }

        if (first >= 0 && row.transform.GetSiblingIndex() > first)
            row.transform.SetSiblingIndex(first);
    }

    private void Build(SettingUIElement anchor, Settings settings)
    {
        SettingToggle? template = null;
        var all = Resources.FindObjectsOfTypeAll(Il2CppType.Of<SettingToggle>());
        for (var index = 0; all is not null && index < all.Length; index++)
        {
            var candidate = all[index]?.TryCast<SettingToggle>();
            if (candidate is not null && candidate != null && candidate.gameObject.scene.IsValid()
                && candidate.m_toggle is { } t && t != null && !candidate.gameObject.name.StartsWith("PowerWashVR", StringComparison.Ordinal))
            {
                template = candidate;
                break;
            }
        }

        if (template is null)
            return;

        var holder = new GameObject("PowerWashVR Holder");
        holder.SetActive(false);
        try
        {
            var copy = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false);
            copy.name = $"PowerWashVR {Label}";
            var element = copy.GetComponent<SettingUIElement>();
            text = element?.m_option?.GetComponent<TextMeshProUGUI>();
            toggle = element?.TryCast<SettingToggle>()?.m_toggle;
            var bound = copy.GetComponentsInChildren<SettingUIElement>(true);
            for (var index = 0; bound is not null && index < bound.Length; index++)
                UnityEngine.Object.DestroyImmediate(bound[index]);
            var localizers = copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true);
            for (var index = 0; localizers is not null && index < localizers.Length; index++)
                UnityEngine.Object.DestroyImmediate(localizers[index]);

            copy.transform.SetParent(anchor.transform.parent, false);
            copy.transform.SetSiblingIndex(anchor.transform.GetSiblingIndex() + 1);
            copy.SetActive(true);
            if (text is not null && text != null)
                text.text = Label;
            if (toggle is not null && toggle != null)
            {
                toggle.interactable = true;
                toggle.SetIsOnWithoutNotify(settings.AutoDetectSteamVR);
            }

            row = copy;
            built = true;
            First();
            log.Msg($"settings: \"{Label}\" added to the game's General settings (copied from \"{template.name}\")");
        }
        finally
        {
            UnityEngine.Object.Destroy(holder);
        }
    }

    private static readonly SettingType[] GeneralRows =
    {
        SettingType.Language, SettingType.HudPrompts, SettingType.ReticleVisibility,
        SettingType.TaskMarkers, SettingType.AutoSave, SettingType.Crossplay,
    };

    // One look over the rows on screen for any of the General page's.
    private static SettingUIElement? GeneralRow()
    {
        var rows = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SettingUIElement>());
        for (var index = 0; rows is not null && index < rows.Length; index++)
        {
            var row = rows[index]?.TryCast<SettingUIElement>();
            if (row is not null && row != null && Array.IndexOf(GeneralRows, row.m_type) >= 0 && row.gameObject.activeInHierarchy)
                return row;
        }

        return null;
    }

    private static SettingUIElement? Row(SettingType type)
    {
        var rows = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SettingUIElement>());
        for (var index = 0; rows is not null && index < rows.Length; index++)
        {
            var row = rows[index]?.TryCast<SettingUIElement>();
            if (row is not null && row != null && row.m_type == type)
                return row;
        }

        return null;
    }
}
