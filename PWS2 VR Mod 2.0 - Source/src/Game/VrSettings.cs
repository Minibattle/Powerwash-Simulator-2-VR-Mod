using Il2CppFuturLab;
using Il2CppFuturLab.PW2;
using Il2CppFuturLab.PW2.UI;
using Il2CppFuturLab.Settings;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using MelonLoader;
using Il2CppTMPro;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;
using SettingData = Il2CppSystem.Collections.Generic.Dictionary<Il2CppFuturLab.Settings.SettingType, Il2CppSystem.Object>;

namespace PowerWashVR.Game;

// The game's own settings screen, made the VR settings screen.
//
//  - VR VIDEO: every video setting is the VR one while VR runs, kept in
//    UserData/PowerWashVR/VrVideoSettings.txt (Medium on the first run, VSync
//    always off, no exclusive full screen).
//  - GAMEPLAY: its settings are kept in VrGameplaySettings.txt (the first run
//    starts from the values you had), and the tab shows only Vibration
//    Intensity (the game's gamepad vibration setting: Normal, Light, Off),
//    which scales the controllers' vibration.
//  - VR CONTROLS: only a Left Handed toggle (the mod's own row).
//
// The game's settings file is never written in VR: its one settings write
// (SettingsSaveManager.TrySave(string), patched) is skipped while VR runs,
// while the game closes after VR, and for a few seconds after VR stops. VR's
// values live only in the mod's own files (VrVideoSettings.txt,
// VrGameplaySettings.txt), and the game's file keeps exactly your own
// settings, crash or not. (Older versions' recovery marker is still honoured
// once by a flat start.)
internal sealed class VrSettings
{
    internal static VrSettings? Current { get; private set; }

    private readonly MelonLogger.Instance log;
    private static readonly string Folder = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "PowerWashVR");
    private readonly string path = Path.Combine(Folder, "VrVideoSettings.txt");
    private readonly string gameplayPath = Path.Combine(Folder, "VrGameplaySettings.txt");
    private readonly string flatPath = Path.Combine(Folder, "FlatSettings.txt");
    private readonly string markerPath = Path.Combine(Folder, "vr-settings-in-use.marker");
    private readonly HashSet<SettingType> videoTypes = new();
    private readonly HashSet<SettingType> gameplayTypes = new();

    private SettingsManager? manager;
    private SettingsSaveManager? saver;
    private readonly HashSet<SettingType> owned = new();
    private readonly Dictionary<SettingType, Il2CppSystem.Object?> flat = new();

    private bool active;
    private float nextTry, nextPoll, nextLabels, valuesLogAt = -1f;
    private int failures;
    private int vsyncChecks;
    private string lastPipeline = "";

    // Set while the VR values are put in place (the render scale may change then).
    internal static bool Applying;
    private float loggedRenderScale = -1f;
    private int loggedResolutionSkips;

    internal VrSettings(MelonLogger.Instance log)
    {
        this.log = log;
        // 1.0.16 saved VR values as the game's own: once, the game's settings
        // are put back from FlatSettings.txt (your own values from before).
        try
        {
            // 2.0.1 and older: a settings save on the way out could keep VR's
            // values as the game's own. Once, the flat values are put back.
            var done202 = Path.Combine(Folder, "flat-repair-2.0.2.done");
            if (!File.Exists(done202))
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(flatPath))
                {
                    File.WriteAllText(markerPath, "2.0.2 repair");
                    log.Msg("settings: your own (non-VR) settings will be put back once from FlatSettings.txt");
                }

                File.WriteAllText(done202, DateTime.Now.ToString("s"));
            }

            var done = Path.Combine(Folder, "flat-repair-1.0.17.done");
            if (!File.Exists(done))
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(flatPath))
                {
                    File.WriteAllText(markerPath, "1.0.17 repair");
                    log.Msg("settings: your own (non-VR) settings will be put back once from FlatSettings.txt");
                }

                File.WriteAllText(done, DateTime.Now.ToString("s"));
            }
        }
        catch
        {
            // Nothing to repair.
        }
    }

    internal bool Active => active;

    internal void Update(GameState state, Settings settings, bool vr)
    {
        if (!vr)
        {
            if (active)
                End();
            return;
        }

        var now = Time.unscaledTime;
        if (!active)
        {
            if (now >= nextTry && failures < 30)
            {
                nextTry = now + 1f;
                Begin(state, settings);
            }

            return;
        }

        if (valuesLogAt > 0f && now >= valuesLogAt)
        {
            valuesLogAt = -1f;
            LogValues("VR values in effect");
        }

        // The Left Handed row drives the mod's own setting; the window never
        // goes exclusive full screen.
        if (now >= nextPoll)
        {
            nextPoll = now + 0.25f;
            this.settings = settings;
            PollRows();
            GuardWindowMode();
            ReadVibration();
            if (Patches.DataLogKinds.Length > 0)
            {
                log.Msg($"settings: the game saved its settings in VR (its settings log line skipped); value classes: {Patches.DataLogKinds}");
                Patches.DataLogKinds = "";
            }

            if (Patches.ResolutionSkips != loggedResolutionSkips)
            {
                loggedResolutionSkips = Patches.ResolutionSkips;
                if (loggedResolutionSkips <= 10)
                    log.Msg($"settings: the game set the window resolution; left alone in VR ({loggedResolutionSkips} so far)");
            }

            if (Patches.DeferredRenderScale >= 0f && Patches.DeferredRenderScale != loggedRenderScale)
            {
                loggedRenderScale = Patches.DeferredRenderScale;
                log.Msg($"settings: render scale {loggedRenderScale:0.##} kept for the next start (changing it in VR crashes the game)");
            }

            if (Patches.PipelineApplied != lastPipeline)
            {
                lastPipeline = Patches.PipelineApplied;
                log.Msg($"settings: graphics applied in place (no pipeline swap in VR): {lastPipeline}");
            }
            if (++vsyncChecks % 8 == 0)
                VSyncOff();
            if (vsyncChecks % 8 == 4 && state.InSettings && manager is not null)
                WriteVr(manager.m_settingData);
            if (state.InSettings)
                HideFullscreenItem();
        }

        if (state.InSettings && now >= nextLabels)
        {
            nextLabels = now + 0.5f;
            Relabel(settings);
        }

    }

    // Set once the game is closing: VR's values are not put in place again
    // (the game still runs frames while it quits), and every settings write
    // from then on is given the flat values.
    internal static bool Quitting { get; private set; }
    internal static VrSettings? Last { get; private set; }
    private static float endedAt = -100f;

    // While VR's values are in the game's memory, while closing after a VR
    // session, and for a few seconds after VR stops (a save already on its
    // way with VR's values), the game's settings file is left alone.
    internal static bool KeepsGameFileClosed =>
        Patches.VrActive || Applying || (Current is not null && Current.active) || (Quitting && Last is not null)
        || Time.unscaledTime - endedAt < 5f;

    // VR's values into the mod's own files.
    internal void SaveVr()
    {
        if (active && manager is not null)
            WriteVr(manager.m_settingData);
    }

    private void Begin(GameState state, Settings settings)
    {
        if (Quitting)
            return;
        try
        {
            // Not while the game is still starting up or loading: its
            // systems that apply settings may not be ready yet.
            var name = state.StateName;
            if (name.Length == 0 || name.Contains("Splash", StringComparison.Ordinal) || name.Contains("Loading", StringComparison.Ordinal))
                return;

            var current = state.Viewport?.CurrentState;
            var found = current?.SettingsManager;
            if (found is null || !found.Initialized)
                return;

            manager = found;
            saver = found.m_settingsSaveManager?.TryCast<SettingsSaveManager>();
            if (saver is null)
            {
                failures++;
                log.Warning("settings: the game's settings saver was not found; VR video settings are not separated");
                return;
            }

            owned.Clear();
            flat.Clear();
            videoTypes.Clear();
            gameplayTypes.Clear();
            var manifest = found.Manifest;
            if (manifest.TryGetSettingsCollectionForScreenType(SettingsCollectionType.Video, out var video) && video is not null)
            {
                foreach (var setting in video.Settings)
                {
                    if (setting is not null && setting != null)
                        videoTypes.Add(setting.Type);
                }
            }

            if (manifest.TryGetSettingsCollectionForScreenType(SettingsCollectionType.Gameplay, out var gameplay) && gameplay is not null)
            {
                var names = new List<string>();
                foreach (var setting in gameplay.Settings)
                {
                    if (setting is null || setting == null)
                        continue;
                    gameplayTypes.Add(setting.Type);
                    names.Add(setting.Type.ToString());
                }

                log.Msg($"settings: gameplay tab has {string.Join(", ", names)}");
            }

            gameplayTypes.Add(SettingType.GamepadVibration);
            owned.UnionWith(videoTypes);
            owned.UnionWith(gameplayTypes);

            if (manifest.TryGetSettingsCollectionForScreenType(SettingsCollectionType.Controls, out var controls) && controls is not null)
            {
                var names = new List<string>();
                foreach (var setting in controls.Settings)
                {
                    if (setting is not null && setting != null)
                        names.Add($"{setting.Type}{(setting.Exposed ? "" : " (hidden)")}");
                }

                log.Msg($"settings: controls tab has {string.Join(", ", names)}");
            }

            var data = found.m_settingData;
            LearnClasses(data);
            foreach (var type in owned)
                flat[type] = data.TryGetValue(type, out var value) ? value : null;

            // A marker left behind means the last VR session never put the
            // flat values back: what the game holds now is VR's, and the flat
            // values are the ones in the file.
            if (File.Exists(markerPath) && File.Exists(flatPath))
            {
                var recovered = 0;
                foreach (var (type, text) in ReadLines(flatPath))
                {
                    if (owned.Contains(type) && Parse(type, text) is { } value)
                    {
                        flat[type] = value;
                        recovered++;
                    }
                }

                log.Msg($"settings: the last VR session did not finish; {recovered} flat value(s) taken from {Path.GetFileName(flatPath)}");
                // The game's own file is put right once, before VR's values
                // go in (an older version left VR's values in it).
                try
                {
                    foreach (var (type, value) in flat)
                    {
                        if (value is not null)
                            Set(type, value);
                    }

                    if (Patches.SaveToGameFile(saver!, data))
                    {
                        File.Delete(markerPath);
                        log.Msg("settings: your own settings written back to the game's settings file");
                    }
                }
                catch (Exception exception)
                {
                    log.Warning($"settings: your own settings not written back ({exception.GetType().Name}); a flat start will try again");
                }
            }
            else
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(flatPath, Lines(flat.Where(entry => entry.Value is not null)
                    .Select(entry => (entry.Key, entry.Value!)), null));
            }

            // The game's settings file is not written while VR runs, so no
            // marker is needed; one left by an older version stays until a
            // flat start has put your settings back from FlatSettings.txt.
            LogValues("flat values kept for playing without VR");

            // The VR values: last time's, or the first-run defaults.
            var loaded = 0;
            // Files from before the Medium defaults (they held a copy of the
            // flat settings) are started over.
            Applying = true;
            if (File.Exists(path) && File.ReadLines(path).FirstOrDefault() == FileHeader)
            {
                // One "Setting=Value" per line, each value rebuilt as the
                // type the game holds for that setting.
                foreach (var (type, text) in ReadLines(path))
                {
                    if (videoTypes.Contains(type) && SetText(type, text))
                        loaded++;
                }

                log.Msg($"settings: {loaded} VR setting(s) loaded from {Path.GetFileName(path)}");
            }
            else
            {
                Defaults();
            }

            // Gameplay: last time's VR values; the first run keeps the current
            // ones (which are the defaults written below).
            if (File.Exists(gameplayPath))
            {
                var gameplayLoaded = 0;
                foreach (var (type, text) in ReadLines(gameplayPath))
                {
                    if (gameplayTypes.Contains(type) && SetText(type, text))
                        gameplayLoaded++;
                }

                log.Msg($"settings: {gameplayLoaded} VR gameplay setting(s) loaded from {Path.GetFileName(gameplayPath)}");
            }

            // Never exclusive full screen or VSync in VR.
            FixWindowMode();
            GrassOff();
            VSyncOff();
            ReadVibration();
            active = true;
            Current = this;
            Last = this;
            valuesLogAt = Time.unscaledTime + 3f;
            WriteVr(found.m_settingData);
            Applying = false;
            log.Msg($"settings: VR settings active ({owned.Count} setting(s) kept apart from the game's own)");
        }
        catch (Exception exception)
        {
            failures++;
            log.Warning($"settings: VR settings not set up ({exception.GetType().Name}: {exception.Message})");
            // Half applied: your own values go back in (memory only).
            try
            {
                foreach (var (type, value) in flat)
                {
                    if (value is not null)
                        Set(type, value);
                }
            }
            catch
            {
            }

            Applying = false;
        }
    }

    // First run: the Medium quality preset and a borderless window.
    private const string FileHeader = "# PowerWash VR video settings v2";

    // First run: Medium wherever a setting has a Medium level (the quality
    // preset itself is not an enum and only follows them), a few VR-minded
    // choices, and the preset then reads Custom or Medium as the game decides.
    private void Defaults()
    {
        var applied = new List<string>();
        foreach (var type in owned)
        {
            if (type is SettingType.VideoQualityPreset or SettingType.Grass or SettingType.WindowMode)
                continue;
            var options = Options(type);
            var medium = options.FirstOrDefault(value => value.Equals("Medium", StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(value => value.Contains("Medium", StringComparison.OrdinalIgnoreCase));
            if (medium is not null && SetNamed(type, medium))
                applied.Add($"{type}={medium}");
        }

        // Anti-aliasing at Medium cost: 2x MSAA.
        foreach (var (type, wanted) in new[] { (SettingType.MSAA, "2x"), (SettingType.AntiAliasing, "2x") })
        {
            var match = Options(type).FirstOrDefault(value => value.Contains(wanted, StringComparison.OrdinalIgnoreCase));
            if (match is not null && SetNamed(type, match))
                applied.Add($"{type}={match}");
        }

        try
        {
            manager!.m_settingData.TryGetValue(SettingType.VideoQualityPreset, out var preset);
            log.Msg($"settings: first VR run, Medium defaults: {string.Join(", ", applied)} "
                + $"(quality preset holds a {preset?.GetIl2CppType().FullName ?? "nothing"}, options [{string.Join(", ", Options(SettingType.VideoQualityPreset))}])");
        }
        catch
        {
            log.Msg($"settings: first VR run, Medium defaults: {string.Join(", ", applied)}");
        }

        var presetMedium = Options(SettingType.VideoQualityPreset).FirstOrDefault(value => value.Contains("Medium", StringComparison.OrdinalIgnoreCase));
        if (presetMedium is not null)
            SetNamed(SettingType.VideoQualityPreset, presetMedium);
    }

    private void FixWindowMode()
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(SettingType.WindowMode, out var value) || value is null)
                return;
            var text = value.ToString();
            var options = Options(SettingType.WindowMode, unfiltered: true);
            log.Msg($"settings: window mode \"{text}\", modes [{string.Join(", ", options)}]");
            if (!text.Contains("Exclusive", StringComparison.OrdinalIgnoreCase))
                return;

            var borderless = options.FirstOrDefault(option => option.Contains("Borderless", StringComparison.OrdinalIgnoreCase)
                    || option.Contains("FullScreenWindow", StringComparison.OrdinalIgnoreCase))
                ?? options.FirstOrDefault(option => !option.Contains("Exclusive", StringComparison.OrdinalIgnoreCase));
            if (borderless is not null && SetNamed(SettingType.WindowMode, borderless))
                log.Msg($"settings: window mode changed to \"{borderless}\"");
        }
        catch (Exception exception)
        {
            log.Msg($"settings: window mode not checked ({exception.GetType().Name})");
        }
    }

    // The game's shell grass only reaches one eye; its own quality switch
    // turns it off (in the VR settings only).
    private void GrassOff()
    {
        var options = Options(SettingType.Grass);
        var off = options.FirstOrDefault(value => value.Equals("Off", StringComparison.OrdinalIgnoreCase))
            ?? options.FirstOrDefault(value => value.Contains("Off", StringComparison.OrdinalIgnoreCase));
        log.Msg($"settings: grass options [{string.Join(", ", options)}]");
        if (off is not null && SetNamed(SettingType.Grass, off))
            log.Msg($"settings: grass set to \"{off}\" for VR");
    }

    internal static bool UnfilteredOptions;

    private void VSyncOff()
    {
        var options = Options(SettingType.VSync);
        var off = options.FirstOrDefault(value => value.Equals("Off", StringComparison.OrdinalIgnoreCase));
        if (off is not null && manager!.m_settingData.TryGetValue(SettingType.VSync, out var current) && current is not null
            && current.ToString() != off && SetNamed(SettingType.VSync, off))
            log.Msg("settings: VSync off for VR");
    }

    // Vibration Intensity: the game's gamepad vibration setting scales the
    // controllers' vibration.
    private string lastVibration = "";

    private void ReadVibration()
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(SettingType.GamepadVibration, out var value) || value is null)
                return;
            var text = value.ToString();
            if (text == lastVibration)
                return;
            lastVibration = text;
            Haptics.UserScale = text.Contains("Off", StringComparison.OrdinalIgnoreCase) ? 0f
                : text.Contains("Light", StringComparison.OrdinalIgnoreCase) || text.Contains("Low", StringComparison.OrdinalIgnoreCase) ? 0.5f
                : 1f;
            log.Msg($"settings: Vibration Intensity {text} (x{Haptics.UserScale:0.0})");
        }
        catch
        {
            // Read again shortly.
        }
    }

    private void GuardWindowMode()
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(SettingType.WindowMode, out var value) || value is null)
                return;
            var text = value.ToString();
            if (!text.Equals("Fullscreen", StringComparison.OrdinalIgnoreCase) && !text.Contains("Exclusive", StringComparison.OrdinalIgnoreCase))
                return;
            if (SetNamed(SettingType.WindowMode, "Borderless"))
                log.Msg($"settings: window mode \"{text}\" is not used in VR; back to Borderless");
        }
        catch
        {
            // Checked again shortly.
        }
    }

    // The window mode list keeps its entries (the game maps them by
    // position), but the Fullscreen one is not shown.
    private void HideFullscreenItem()
    {
        try
        {
            var items = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<UnityEngine.UI.Toggle>());
            var seen = new List<string>();
            for (var index = 0; items is not null && index < items.Length; index++)
            {
                var item = items[index]?.TryCast<UnityEngine.UI.Toggle>();
                if (item is null || item == null || !item.name.StartsWith("Item ", StringComparison.Ordinal))
                    continue;
                var label = item.GetComponentInChildren<TextMeshProUGUI>(true);
                var text = (label is null || label == null ? "" : label.text ?? "").Trim();
                seen.Add($"{item.name}/{text}");
                var name = item.name;
                var full = text.Replace(" ", "").Equals("Fullscreen", StringComparison.OrdinalIgnoreCase)
                    || name.Replace(" ", "").EndsWith(":Fullscreen", StringComparison.OrdinalIgnoreCase);
                if (!full)
                    continue;

                // Collapsed rather than only switched off: the list may switch
                // its items back on as it opens.
                var rect = item.transform.TryCast<RectTransform>();
                if (rect is not null && rect.localScale != Vector3.zero)
                {
                    rect.localScale = Vector3.zero;
                    var layout = item.GetComponent<UnityEngine.UI.LayoutElement>() ?? item.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
                    layout.ignoreLayout = true;
                    item.interactable = false;
                    if (fullscreenLogged++ < 3)
                        log.Msg($"settings: Fullscreen entry \"{name}\" hidden");
                }
            }

            if (seen.Count > 0 && itemListsLogged < 4)
            {
                var joined = string.Join(", ", seen);
                if (joined != lastItems)
                {
                    lastItems = joined;
                    itemListsLogged++;
                    if (Diag.Verbose)
                        log.Msg($"settings: open list items: {joined}");
                }
            }
        }
        catch (Exception exception)
        {
            if (fullscreenLogged++ < 3)
                log.Msg($"settings: Fullscreen entry not hidden ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private int fullscreenLogged, itemListsLogged;
    private string lastItems = "";

    private string[] Options(SettingType type, bool unfiltered = false)
    {
        try
        {
            // Most of these settings hold an enum: its names are the options.
            if (manager!.m_settingData.TryGetValue(type, out var current) && current is not null)
            {
                var valueType = current.GetIl2CppType();
                if (valueType.IsEnum)
                {
                    var names = Il2CppSystem.Enum.GetNames(valueType);
                    var enumNames = new string[names.Length];
                    for (var index = 0; index < names.Length; index++)
                        enumNames[index] = names[index] ?? "";
                    return enumNames;
                }
            }

            UnfilteredOptions = unfiltered;
            if (!manager!.Manifest.TryGetValueProvider(type, out var provider, out _) || provider is null)
                return Array.Empty<string>();
            var data = provider.Data;
            Il2CppStringArray? values = data?.TryCast<DropdownStrategy>()?.GetPresetDropdownValues()
                ?? data?.TryCast<WindowModeDropdownStrategy>()?.GetPresetDropdownValues();
            if (values is null)
                return Array.Empty<string>();
            var list = new string[values.Length];
            for (var index = 0; index < values.Length; index++)
                list[index] = values[index] ?? "";
            return list;
        }
        catch
        {
            return Array.Empty<string>();
        }
        finally
        {
            UnfilteredOptions = false;
        }
    }

    // A value of exactly the type the setting already holds: an enum by name,
    // or a string. Anything else is refused - a value of the wrong type
    // brings the game down when it applies or saves it.
    private bool SetNamed(SettingType type, string name)
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(type, out var current) || current is null)
            {
                log.Msg($"settings: {type} has no value to match; left alone");
                return false;
            }

            var valueType = current.GetIl2CppType();
            Il2CppSystem.Object value;
            if (valueType.IsEnum)
                value = Il2CppSystem.Enum.Parse(valueType, name);
            else if (valueType.FullName == "System.String")
                value = Box(name);
            else
            {
                log.Msg($"settings: {type} holds a {valueType.FullName}; left alone");
                return false;
            }

            if (value is null || value.GetIl2CppType().FullName != valueType.FullName)
                return false;
            return Set(type, value);
        }
        catch (Exception exception)
        {
            log.Msg($"settings: {type} = {name} not set ({exception.GetType().Name}: {exception.Message})");
            return false;
        }
    }

    private bool Set(SettingType type, Il2CppSystem.Object value)
    {
        try
        {
            // Every value the mod hands the game is held for good (a value the
            // game's settings were left holding after it was collected
            // crashed the game's save).
            KeepAlive.Add(value);
            manager!.SetValue(new UpdatedSettingValue(type, value));
            return true;
        }
        catch (Exception exception)
        {
            log.Msg($"settings: {type} not set ({exception.GetType().Name}: {exception.Message})");
            return false;
        }
    }

    private static bool Storable(Il2CppSystem.Object value)
    {
        var type = value.GetIl2CppType();
        return type.IsEnum || type.FullName is "System.String" or "System.Int32" or "System.Single" or "System.Boolean";
    }

    private static string Text(Il2CppSystem.Object value)
    {
        var type = value.GetIl2CppType().FullName;
        if (type == "System.Single")
            return value.Unbox<float>().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        if (type == "System.Int32")
            return value.Unbox<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (type == "System.Boolean")
            return value.Unbox<bool>() ? "True" : "False";
        return value.ToString();
    }

    // A saved text value as an object of exactly the type the setting holds.
    private Il2CppSystem.Object? Parse(SettingType type, string text)
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(type, out var current) || current is null)
                return null;
            var valueType = current.GetIl2CppType();
            var name = valueType.FullName;
            if (valueType.IsEnum)
                return Il2CppSystem.Enum.Parse(valueType, text);
            if (name == "System.String")
                return Box(text);
            if (name == "System.Int32" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
                return Boxed(ClassOf(current), number);
            if (name == "System.Single" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real))
                return Boxed(ClassOf(current), BitConverter.SingleToInt32Bits(real));
            if (name == "System.Boolean" && bool.TryParse(text, out var flag))
                return Boxed(ClassOf(current), flag ? 1 : 0);
        }
        catch
        {
            // Not rebuildable.
        }

        return null;
    }

    // A saved text value, rebuilt as exactly the type the setting holds now.
    private bool SetText(SettingType type, string text)
    {
        try
        {
            if (!manager!.m_settingData.TryGetValue(type, out var current) || current is null)
                return false;
            var valueType = current.GetIl2CppType();
            var name = valueType.FullName;
            Il2CppSystem.Object? value = null;
            if (valueType.IsEnum || name == "System.String")
                return SetNamed(type, text);
            if (name == "System.Int32" && int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number))
                value = Boxed(ClassOf(current), number);
            else if (name == "System.Single" && float.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var real))
                value = Boxed(ClassOf(current), BitConverter.SingleToInt32Bits(real));
            else if (name == "System.Boolean" && bool.TryParse(text, out var flag))
                value = Boxed(ClassOf(current), flag ? 1 : 0);
            return value is not null && Set(type, value);
        }
        catch (Exception exception)
        {
            log.Msg($"settings: saved {type}={text} not applied ({exception.GetType().Name})");
            return false;
        }
    }

    // Boxed by the game's own runtime (as the game itself boxes them).
    private static Il2CppSystem.Object BoxValue(int value) => Boxed(Il2CppClassPointerStore<Il2CppSystem.Int32>.NativeClassPtr, value);

    // The class of the value the game holds: a rebuilt value is boxed as
    // exactly that.
    private static IntPtr ClassOf(Il2CppSystem.Object current) => IL2CPP.il2cpp_object_get_class(current.Pointer);

    private static readonly List<Il2CppSystem.Object> KeepAlive = new();

    // A setting type boxed by the game's runtime (for the IDictionary indexer).
    private static readonly Dictionary<SettingType, Il2CppSystem.Object> keys = new();

    private static Il2CppSystem.Object Key(SettingType type)
    {
        if (!keys.TryGetValue(type, out var key))
        {
            key = Il2CppSystem.Enum.ToObject(Il2CppInterop.Runtime.Il2CppType.Of<SettingType>(), (int)type);
            keys[type] = key;
        }

        return key;
    }

    // The class pointers real setting values have (seen in the game's own
    // settings before any VR value goes in).
    private static readonly HashSet<IntPtr> goodClasses = new();
    private int repairs;

    // Just before the game serializes its settings (Patches): any value whose
    // object no longer has a real class (a collected object) is replaced by
    // your own value for that setting, so the save cannot crash on it.
    internal void Repair(Il2CppSystem.Collections.Generic.Dictionary<SettingType, Il2CppSystem.Object> data)
    {
        var bad = new List<SettingType>();
        foreach (var entry in data)
        {
            var value = entry.Value;
            var klass = value is null || value.Pointer == IntPtr.Zero ? IntPtr.Zero : Marshal.ReadIntPtr(value.Pointer);
            if (goodClasses.Count < 64 && !active && klass != IntPtr.Zero)
                goodClasses.Add(klass);
            else if (goodClasses.Count > 0 && value is not null && !goodClasses.Contains(klass))
                bad.Add(entry.Key);
        }

        foreach (var type in bad)
        {
            var replacement = flat.TryGetValue(type, out var kept) && kept is not null && goodClasses.Contains(Marshal.ReadIntPtr(kept.Pointer)) ? kept : null;
            if (repairs++ < 20)
                log.Warning($"settings: {type}'s value was no longer a real object (class {Marshal.ReadIntPtr(data[type].Pointer):x}); "
                    + (replacement is null ? "left as it was" : "your own value saved in its place"));
            if (replacement is not null)
                data.Cast<Il2CppSystem.Collections.IDictionary>()[Key(type)] = replacement;
        }
    }

    // The game's own values, before VR: their classes are the real ones.
    internal void LearnClasses(SettingData data)
    {
        foreach (var entry in data)
        {
            var value = entry.Value;
            if (value is not null && value.Pointer != IntPtr.Zero)
                goodClasses.Add(Marshal.ReadIntPtr(value.Pointer));
        }
    }

    private static Il2CppSystem.Object Boxed(IntPtr klass, int bits)
    {
        var memory = Marshal.AllocHGlobal(8);
        try
        {
            Marshal.WriteInt64(memory, 0);
            Marshal.WriteInt32(memory, bits);
            return new Il2CppSystem.Object(IL2CPP.il2cpp_value_box(klass, memory));
        }
        finally
        {
            Marshal.FreeHGlobal(memory);
        }
    }

    private static Il2CppSystem.Object BoxValue(float value) =>
        Boxed(Il2CppClassPointerStore<Il2CppSystem.Single>.NativeClassPtr, BitConverter.SingleToInt32Bits(value));

    private bool SameType(SettingType type, Il2CppSystem.Object value)
    {
        if (!manager!.m_settingData.TryGetValue(type, out var current) || current is null)
            return false;
        var same = current.GetIl2CppType().FullName == value.GetIl2CppType().FullName;
        if (!same)
            log.Msg($"settings: saved {type} is a {value.GetIl2CppType().FullName}, the game holds a {current.GetIl2CppType().FullName}; skipped");
        return same;
    }

    private static Il2CppSystem.Object Box(string value) => new(IL2CPP.ManagedStringToIl2Cpp(value));

    private static Il2CppSystem.Object Box(bool value) => Boxed(Il2CppClassPointerStore<Il2CppSystem.Boolean>.NativeClassPtr, value ? 1 : 0);

    // Called from the game's settings save (Patches): the VR values go to the
    // VR file, and the game's own save gets a copy holding the flat values.
    // Called around the game's settings save (Patches). Before: the VR values
    // go to the VR file, and the entries the game is about to write are
    // swapped, in place, for the flat values. After: the VR values go back.
    // No new dictionary is made and no key is added or removed.
    private readonly List<(SettingType Type, Il2CppSystem.Object Value)> swapped = new();

    internal void BeforeGameSave(SettingData data)
    {
        swapped.Clear();
        if (!active || data is null)
            return;

        try
        {
            WriteVr(data);
            foreach (var type in owned)
            {
                if (!flat.TryGetValue(type, out var kept) || kept is null)
                    continue;
                if (!data.TryGetValue(type, out var current) || current is null)
                    continue;
                if (current.GetIl2CppType().FullName != kept.GetIl2CppType().FullName)
                    continue;
                swapped.Add((type, current));
                data[type] = kept;
            }

            if (savesLogged++ < 5)
                log.Msg($"settings: game saving its settings; {swapped.Count} VR setting(s) kept out of its save (in {Path.GetFileName(path)})");
        }
        catch (Exception exception)
        {
            log.Warning($"settings: save split failed ({exception.GetType().Name}: {exception.Message})");
            AfterGameSave(data);
        }
    }

    internal void AfterGameSave(SettingData data)
    {
        try
        {
            foreach (var (type, value) in swapped)
                data[type] = value;
        }
        catch (Exception exception)
        {
            log.Warning($"settings: VR values not put back after the save ({exception.GetType().Name})");
        }

        swapped.Clear();
    }

    private int savesLogged;

    // The JSON the game would save, with your own values in place of the VR
    // ones (a copy; the game's live settings are not touched). Also writes
    // the VR values to the mod's files.
    // The JSON the game is about to save (its VR values), with your own
    // values written in, as text: nothing is written into the game's
    // dictionaries (writing boxed numbers into them from here stored them
    // broken, which crashed the game's later saves).
    internal bool FlatJson(SettingsSaveManager save, string vrJson, out string json)
    {
        json = "";
        if (manager is null || string.IsNullOrEmpty(vrJson))
            return false;
        var data = manager.m_settingData;
        // Only while VR's values are the game's (after VR stopped the game
        // holds the flat ones, which must not become VR's).
        if (data is not null && active)
            WriteVr(data);
        var text = vrJson;
        var replaced = 0;
        var missing = 0;
        foreach (var type in owned)
        {
            if (!flat.TryGetValue(type, out var kept) || kept is null || !Storable(kept))
                continue;
            var value = JsonValue(kept);
            var pattern = "\"" + type + "\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\"|[^,}\\]]+)";
            var regex = new System.Text.RegularExpressions.Regex(pattern);
            var match = regex.Match(text);
            if (!match.Success)
            {
                missing++;
                continue;
            }

            var group = match.Groups[1];
            text = text.Substring(0, group.Index) + value + text.Substring(group.Index + group.Length);
            replaced++;
        }

        json = text;
        if (savesLogged++ < 5)
            log.Msg($"settings: the game saved its settings; {replaced} value(s) written as your own (non-VR) ones"
                + (missing > 0 ? $", {missing} not in its save" : "") + $", VR values kept in {Path.GetFileName(path)}");
        return replaced > 0 || missing == 0;
    }

    // A value as the game's JSON writes it.
    private static string JsonValue(Il2CppSystem.Object value)
    {
        var type = value.GetIl2CppType();
        var name = type.FullName;
        if (name == "System.Boolean")
            return value.Unbox<bool>() ? "true" : "false";
        if (name == "System.Int32")
            return value.Unbox<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (name == "System.Single")
        {
            var number = value.Unbox<float>().ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            return number.Contains('.') || number.Contains('E') ? number : number + ".0";
        }

        var textValue = Text(value);
        return "\"" + textValue.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    }

    private string lastWritten = "";

    private readonly Dictionary<SettingType, string> lastValues = new();
    private int changeLogs;

    private void WriteVr(SettingData data)
    {
        try
        {
            // Plain text, not the game's serializer: only values whose type
            // can be rebuilt exactly are kept (enums, numbers, switches, text).
            var video = new List<(SettingType, Il2CppSystem.Object)>();
            var gameplay = new List<(SettingType, Il2CppSystem.Object)>();
            foreach (var type in owned)
            {
                if (!data.TryGetValue(type, out var value) || value is null)
                    continue;
                (videoTypes.Contains(type) ? video : gameplay).Add((type, value));
            }

            // Each VR value that changed since the last write is logged, so
            // a setting that does not stick can be seen.
            foreach (var (type, value) in video.Concat(gameplay))
            {
                string now;
                try
                {
                    now = Text(value);
                }
                catch
                {
                    continue;
                }

                if (Diag.Verbose && lastValues.TryGetValue(type, out var before) && before != now && changeLogs++ < 200)
                    log.Msg($"settings: VR {type} {before} -> {now}");
                lastValues[type] = now;
            }

            var videoLines = Lines(video, FileHeader);
            var gameplayLines = Lines(gameplay, "# PowerWash VR gameplay settings v1");
            var all = string.Join("\n", videoLines) + "\n" + string.Join("\n", gameplayLines);
            if (all == lastWritten)
                return;
            lastWritten = all;

            Directory.CreateDirectory(Folder);
            File.WriteAllLines(path, videoLines);
            File.WriteAllLines(gameplayPath, gameplayLines);
        }
        catch (Exception exception)
        {
            log.Msg($"settings: VR settings files not written ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private static List<string> Lines(IEnumerable<(SettingType Type, Il2CppSystem.Object Value)> values, string? header)
    {
        var lines = new List<string>();
        if (header is not null)
            lines.Add(header);
        foreach (var (type, value) in values)
        {
            if (Storable(value))
                lines.Add($"{type}={Text(value)}");
        }

        return lines;
    }

    private static IEnumerable<(SettingType Type, string Text)> ReadLines(string file)
    {
        foreach (var line in File.ReadAllLines(file))
        {
            var split = line.IndexOf('=');
            if (split <= 0 || line.StartsWith('#') || !Enum.TryParse<SettingType>(line[..split].Trim(), out var type))
                continue;
            yield return (type, line[(split + 1)..].Trim());
        }
    }

    // Playing without VR (no SteamVR, or VR did not start) after a VR session
    // that never finished: the game holds VR's values, so the flat ones are
    // put back from FlatSettings.txt and saved. Checked once a second until
    // the game's settings are up; true once nothing is left to do.
    private bool flatRecovered;

    internal bool RecoverFlat()
    {
        if (flatRecovered || active)
            return true;
        if (!File.Exists(markerPath) || !File.Exists(flatPath))
        {
            flatRecovered = true;
            return true;
        }

        try
        {
            var found = Il2CppFuturLab.PW2.PwsScreenManager.Instance?.MainViewport?.CurrentState?.SettingsManager;
            if (found is null || !found.Initialized)
                return false;
            var save = found.m_settingsSaveManager?.TryCast<SettingsSaveManager>();
            if (save is null)
                return false;

            manager = found;
            var restored = 0;
            foreach (var (type, text) in ReadLines(flatPath))
            {
                if (Parse(type, text) is { } value && Set(type, value))
                    restored++;
            }

            var saved = save.TrySave(found.m_settingData);
            if (saved)
                File.Delete(markerPath);
            manager = null;
            flatRecovered = true;
            log.Msg($"settings: the last VR session did not finish; {restored} of your own setting(s) put back from "
                + $"{Path.GetFileName(flatPath)}{(saved ? " and saved" : " (not saved; tried again next start)")}");
        }
        catch (Exception exception)
        {
            flatRecovered = true;
            manager = null;
            log.Warning($"settings: your own settings not recovered ({exception.GetType().Name}: {exception.Message})");
        }

        return true;
    }

    // VR stopped, or the game is quitting: the flat values go back and the
    // game saves them, so its own settings are what they were.
    internal void Shutdown()
    {
        Quitting = true;
        if (active)
            End();
    }

    private void End()
    {
        // From here on (the flat values going back in may make the game
        // save) the game's file stays closed for a few seconds.
        endedAt = Time.unscaledTime;
        active = false;
        Current = null;
        if (manager is null)
            return;

        try
        {
            WriteVr(manager.m_settingData);
            foreach (var (type, value) in flat)
            {
                if (value is not null)
                    Set(type, value);
            }

            endedAt = Time.unscaledTime;
            // In memory only: the game's file was never changed.
            log.Msg("settings: VR stopped; the game's own settings are back in place (its settings file was not written in VR)");
        }
        catch (Exception exception)
        {
            log.Warning($"settings: flat settings not restored ({exception.GetType().Name})");
        }
    }

    private void LogValues(string title)
    {
        try
        {
            var data = manager!.m_settingData;
            var parts = new List<string>();
            foreach (var type in owned)
                parts.Add($"{type}={(data.TryGetValue(type, out var value) && value is not null ? value.ToString() : "-")}");
            log.Msg($"settings: {title}: {string.Join(", ", parts)}");
        }
        catch
        {
            // Diagnostics only.
        }
    }

    // The tab names and the Left Handed row label. The game localizes them,
    // so its localizer is switched off on the renamed texts.
    private void Relabel(Settings settings)
    {
        try
        {
            var tabs = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<TabButton>());
            for (var index = 0; tabs is not null && index < tabs.Length; index++)
            {
                var tab = tabs[index]?.TryCast<TabButton>();
                var text = tab?.m_text;
                if (tab is null || text is null || text == null)
                    continue;
                var current = text.text?.Trim() ?? "";
                var renamed = current.ToUpperInvariant() switch
                {
                    "VIDEO" => "VR VIDEO",
                    "CONTROLS" => "VR CONTROLS",
                    "GAMEPLAY" => "VR SETTINGS",
                    _ => null,
                };
                if (renamed is null)
                    continue;
                var localizer = tab.m_localizedText;
                if (localizer is not null && localizer != null && localizer.enabled)
                    localizer.enabled = false;
                text.text = char.IsLower(current.LastOrDefault()) ? Title(renamed) : renamed;
                if (Diag.Verbose)
                    log.Msg($"settings: tab \"{current}\" renamed");
            }

            this.settings = settings;
            ControlsPage();
            SettingsPage();
            VideoPage();
            HideRow(SettingType.VSync);
            HideRow(SettingType.SplitScreenStereo);
            RelabelRow(SettingType.RenderScale, "Render Scale (Restart Required)");
        }
        catch (Exception exception)
        {
            if (relabelWarnings++ < 3)
                log.Msg($"settings: relabel failed ({exception.GetType().Name}: {exception.Message})");
        }
    }

    private int relabelWarnings;

    // The mod's own rows: copies of the game's own toggle, dropdown and
    // slider rows with the parts that bind them to a game setting taken out.
    // Toggles are switched by the pointer (Click); dropdowns and sliders are
    // the game's own controls, read back a few times a second (PollRows).
    private enum RowKind { Toggle, Dropdown, Slider }

    private sealed class ModRow
    {
        internal string Label = "";
        internal RowKind Kind;
        internal Func<Settings, bool>? On;
        internal Action<Settings>? Flip;
        internal string[] Options = Array.Empty<string>();
        internal Func<Settings, int>? GetIndex;
        internal Action<Settings, int>? SetIndex;
        internal float Min, Max;
        internal Func<Settings, float>? GetValue;
        internal Action<Settings, float>? SetValue;
        internal Func<float, string> Format = value => value.ToString("0");
        internal GameObject? Row;
        internal UnityEngine.UI.Toggle? Toggle;
        internal Il2CppTMPro.TMP_Dropdown? Dropdown;
        internal UnityEngine.UI.Slider? Slider;
        internal TextMeshProUGUI? Text;
        internal TMP_Text? ValueText;
        internal int LastIndex = -1;
        internal float LastValue = float.NaN;
        internal bool Alive => Row is not null && Row != null;
    }

    private Settings? settings;
    private static readonly float[] SnapAngles = { 15f, 22.5f, 30f, 45f, 60f, 90f };

    private readonly ModRow[] controlRows =
    {
        new()
        {
            Label = "Left Handed", Kind = RowKind.Toggle, On = s => s.LeftHandedSetting,
            Flip = s => s.SetLeftHanded(!s.LeftHandedSetting),
        },
        new()
        {
            Label = "Physical Washer", Kind = RowKind.Toggle, On = s => s.PhysicalWasher,
            Flip = s => s.SetPhysicalWasher(!s.PhysicalWasher),
        },
        new()
        {
            Label = "Physical Attachments", Kind = RowKind.Toggle, On = s => s.PhysicalAttachments,
            Flip = s => s.SetPhysicalAttachments(!s.PhysicalAttachments),
        },
        new()
        {
            Label = "Physical Belt", Kind = RowKind.Toggle, On = s => s.BeltStrap,
            Flip = s => s.SetBeltStrap(!s.BeltStrap),
        },
        new()
        {
            Label = "Swap Hand Buttons", Kind = RowKind.Toggle, On = s => s.SwapHandButtons,
            Flip = s => s.SetSwapHandButtons(!s.SwapHandButtons),
        },
        new()
        {
            Label = "Control Preset", Kind = RowKind.Dropdown, Options = new[] { "Default", "Less Buttons", "R Stick Scan" },
            GetIndex = s => s.ControlPreset, SetIndex = (s, index) => s.SetControlPreset(index),
        },
        new()
        {
            Label = "Turn Mode", Kind = RowKind.Dropdown, Options = new[] { "Smooth", "Snap", "Off" },
            GetIndex = s => s.TurnOff ? 2 : s.SnapTurn ? 1 : 0, SetIndex = (s, index) => s.SetTurnMode(index),
        },
        new()
        {
            Label = "Smooth Turn Sensitivity", Kind = RowKind.Slider, Min = 30f, Max = 360f,
            GetValue = s => s.SmoothTurnSpeed, SetValue = (s, value) => s.SetSmoothTurnSpeed(value),
            Format = value => $"{value:0}°/s",
        },
        new()
        {
            Label = "Snap Turn Sensitivity", Kind = RowKind.Dropdown, Options = SnapAngles.Select(angle => $"{angle:0.#}°").ToArray(),
            GetIndex = s => Nearest(SnapAngles, s.SnapAngle), SetIndex = (s, index) => s.SetSnapAngle(SnapAngles[index]),
        },
    };

    private readonly ModRow[] videoRows =
    {
        new()
        {
            Label = "FSR 1 (Restart Required)", Kind = RowKind.Dropdown,
            Options = new[] { "Off", "Ultra Quality", "Quality", "Balanced", "Performance" },
            GetIndex = s => s.Fsr, SetIndex = (s, index) => s.SetFsr(index),
        },
    };

    private readonly ModRow[] settingRows =
    {
        new()
        {
            Label = "Nozzle Names", Kind = RowKind.Toggle, On = s => s.NozzleNames,
            Flip = s => s.SetNozzleNames(!s.NozzleNames),
        },
        new()
        {
            Label = "Laser Smoothing", Kind = RowKind.Toggle, On = s => s.LaserSmoothing,
            Flip = s => s.SetLaserSmoothing(!s.LaserSmoothing),
        },
        new()
        {
            Label = "Disable Tutorials", Kind = RowKind.Toggle, On = s => s.DisableTutorials,
            Flip = s => s.SetDisableTutorials(!s.DisableTutorials),
        },
        new()
        {
            Label = "Hide Interaction Hints", Kind = RowKind.Toggle, On = s => s.HideInteractionHints,
            Flip = s => s.SetHideInteractionHints(!s.HideInteractionHints),
        },
        new()
        {
            Label = "Belt Height", Kind = RowKind.Dropdown, Options = new[] { "Default", "High", "Higher" },
            GetIndex = s => s.BeltHeight, SetIndex = (s, index) => s.SetBeltHeight(index),
        },
        new()
        {
            Label = "Rumble Fade Out", Kind = RowKind.Dropdown, Options = new[] { "2 Seconds", "5 Seconds", "1 Minute", "Off" },
            GetIndex = s => s.RumbleFadeOut, SetIndex = (s, index) => s.SetRumbleFadeOut(index),
        },
        new()
        {
            Label = "Sprint to Stand", Kind = RowKind.Toggle, On = s => s.SprintToStand,
            Flip = s => s.SetSprintToStand(!s.SprintToStand),
        },
        new()
        {
            Label = "Auto Detect SteamVR", Kind = RowKind.Toggle, On = s => s.AutoDetectSteamVR,
            Flip = s => s.SetAutoDetectSteamVR(!s.AutoDetectSteamVR),
        },
        new()
        {
            Label = "Spectator Camera", Kind = RowKind.Toggle, On = s => s.SmoothCamera,
            Flip = s => s.SetSmoothCamera(!s.SmoothCamera),
        },
        new()
        {
            Label = "Spectator Camera FOV", Kind = RowKind.Slider, Min = 50f, Max = 120f,
            GetValue = s => s.SmoothCameraFov, SetValue = (s, value) => s.SetSmoothCameraFov(value),
            Format = value => $"{value:0}°",
        },
    };

    private static int Nearest(float[] values, float current)
    {
        var best = 0;
        for (var index = 1; index < values.Length; index++)
        {
            if (Mathf.Abs(values[index] - current) < Mathf.Abs(values[best] - current))
                best = index;
        }

        return best;
    }

    private readonly HashSet<string> templateMissingLogged = new();
    private int pagesLogged;
    private bool controlsContainerLogged;

    // VR CONTROLS: the mod's rows only, in the page's main list.
    private void ControlsPage()
    {
        var presets = Row(SettingType.ControlPresets);
        if (presets is null || settings is null)
            return;   // the controls page is not built yet

        // The rows go into the presets list (the one the game itself shows
        // settings rows in); everything else on the page (the key bindings
        // area) is switched off, and that list is stretched to fill the page.
        var container = presets.transform.parent;
        if (container is null)
            return;
        BuildRows(controlRows, container, 0);
        var kept = controlRows.Where(row => row.Alive).Select(row => row.Row!.transform).ToList();
        var hidden = Isolate(kept);
        if (kept.Count > 0)
            Fill(kept[0]);
        if (Diag.Verbose && hidden > 0 && pagesLogged++ < 10)
            log.Msg($"settings: VR CONTROLS page: {hidden} other item(s) hidden");
    }

    // Every container between the row's list and its tab page fills its
    // parent, so a list that was a small box at the top gets the whole page.
    private void Fill(Transform row)
    {
        var pageType = Il2CppType.Of<TabPage>();
        var scroll = (UnityEngine.UI.ScrollRect?)null;
        for (var node = row.parent; node is not null && node != null; node = node.parent)
        {
            // The page's own frame keeps its margins (it leaves room for the
            // tabs above): only what is inside it is stretched.
            if (node.GetComponent(pageType) is { } found && found != null)
                break;
            if (node.parent is { } up && up != null && up.GetComponent(pageType) is { } pageAbove && pageAbove != null)
                break;

            scroll ??= node.GetComponent<UnityEngine.UI.ScrollRect>();
            if (scroll is null)
                continue;   // inside the list itself (content, viewport)

            var rect = node.TryCast<RectTransform>();
            if (rect is null)
                continue;
            if (rect.anchorMin != Vector2.zero || rect.anchorMax != Vector2.one || rect.offsetMin != Vector2.zero || rect.offsetMax != Vector2.zero)
            {
                rect.anchorMin = Vector2.zero;
                rect.anchorMax = Vector2.one;
                rect.offsetMin = Vector2.zero;
                rect.offsetMax = Vector2.zero;
            }

            var layout = node.GetComponent<UnityEngine.UI.LayoutElement>() ?? node.gameObject.AddComponent<UnityEngine.UI.LayoutElement>();
            if (layout.flexibleHeight < 1f)
            {
                layout.flexibleHeight = 1f;
                layout.preferredHeight = -1f;
                layout.minHeight = -1f;
            }
        }

        if (!filledLogged && scroll is not null)
        {
            filledLogged = true;
            log.Msg($"settings: VR CONTROLS list \"{scroll.name}\" stretched to the page");
        }
    }

    private bool filledLogged;

    // The tallest scroll list on the row's tab page (the key binding list on
    // the controls page): the mod's rows go there rather than into the small
    // presets box at the top.
    private Transform? MainList(Transform row)
    {
        var pageType = Il2CppType.Of<TabPage>();
        Transform? page = null;
        for (var node = row; node is not null && node != null; node = node.parent)
        {
            if (node.GetComponent(pageType) is { } found && found != null)
            {
                page = node;
                break;
            }
        }

        if (page is null || !page.gameObject.activeInHierarchy)
            return null;

        UnityEngine.UI.ScrollRect? best = null;
        var bestHeight = 0f;
        // Only the lists that are on screen: the page keeps hidden ones (the
        // other device's bindings) that the rows must not go into.
        var lists = page.GetComponentsInChildren<UnityEngine.UI.ScrollRect>(false);
        if (!controlsContainerLogged)
        {
            var chain = new List<string>();
            for (var node = row; node is not null && node != null && node.Pointer != page.Pointer; node = node.parent)
                chain.Add($"{node.name}{(node.gameObject.activeSelf ? "" : " (off)")}[{node.childCount}]");
            log.Msg($"settings: controls page \"{page.name}\": presets row path {string.Join(" < ", chain)}");
            for (var index = 0; lists is not null && index < lists.Length; index++)
            {
                var list = lists[index];
                if (list is null || list == null)
                    continue;
                var view = list.viewport is not null && list.viewport != null ? list.viewport : list.transform.TryCast<RectTransform>();
                log.Msg($"settings:   list \"{list.name}\" view {(view is null ? 0f : view.rect.height):0} tall, "
                    + $"content \"{list.content?.name}\" with {list.content?.childCount ?? 0} item(s), active {list.isActiveAndEnabled}");
            }
        }
        for (var index = 0; lists is not null && index < lists.Length; index++)
        {
            var list = lists[index];
            if (list is null || list == null || list.content is null || list.content == null || !list.vertical
                || !list.isActiveAndEnabled)
                continue;
            var view = list.viewport is not null && list.viewport != null ? list.viewport : list.transform.TryCast<RectTransform>();
            var height = view is null ? 0f : view.rect.height;
            if (height > bestHeight)
            {
                bestHeight = height;
                best = list;
            }
        }

        if (!controlsContainerLogged)
        {
            controlsContainerLogged = true;
            log.Msg($"settings: VR CONTROLS rows go into \"{(best is null ? row.parent.name : best.content.name)}\" "
                + $"({(best is null ? "the presets box" : $"scroll list {bestHeight:0} tall")})");
        }

        return best?.content;
    }

    // VR SETTINGS (the game's Gameplay tab): Vibration Intensity (the game's
    // own gamepad vibration row, renamed) and the mod's rows under it.
    private void SettingsPage()
    {
        var vibration = Row(SettingType.GamepadVibration);
        if (vibration is null || settings is null)
            return;

        // Any gameplay row shows where the gameplay page is.
        SettingUIElement? anchor = null;
        var rows = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SettingUIElement>());
        for (var index = 0; rows is not null && index < rows.Length; index++)
        {
            var row = rows[index]?.TryCast<SettingUIElement>();
            if (row is not null && row != null && row.m_type != SettingType.GamepadVibration && gameplayTypes.Contains(row.m_type))
            {
                anchor = row;
                break;
            }
        }

        var container = anchor?.transform.parent ?? vibration.transform.parent;
        if (container is null)
            return;
        if (vibration.transform.parent.Pointer != container.Pointer)
        {
            vibration.transform.SetParent(container, false);
            vibration.transform.SetAsFirstSibling();
            log.Msg("settings: Vibration Intensity moved onto the VR SETTINGS page");
        }

        if (!vibration.gameObject.activeSelf)
            vibration.gameObject.SetActive(true);

        var option = vibration.m_option;
        var label = option is null || option == null ? null : option.GetComponent<TextMeshProUGUI>();
        if (label is not null && label != null && label.text != "Vibration Intensity")
        {
            option!.enabled = false;
            label.text = "Vibration Intensity";
        }

        BuildRows(settingRows, container, vibration.transform.GetSiblingIndex() + 1);
        var keep = settingRows.Where(row => row.Alive).Select(row => row.Row!.transform).Append(vibration.transform);
        var hidden = Isolate(keep);
        if (Diag.Verbose && hidden > 0 && pagesLogged++ < 10)
            log.Msg($"settings: VR SETTINGS page: {hidden} other item(s) hidden");
    }

    // VR VIDEO: the mod's rows first, above the game's video rows.
    private void VideoPage()
    {
        if (settings is null)
            return;
        SettingUIElement? anchor = null;
        var rows = UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SettingUIElement>());
        for (var index = 0; rows is not null && index < rows.Length; index++)
        {
            var row = rows[index]?.TryCast<SettingUIElement>();
            // The page's main list: the parent holding the most rows.
            if (row is not null && row != null && videoTypes.Contains(row.m_type) && !row.gameObject.name.StartsWith("PowerWashVR", StringComparison.Ordinal)
                && row.transform.parent is { } parent && (anchor is null || parent.childCount > anchor.transform.parent.childCount))
                anchor = row;
        }

        var container = anchor?.transform.parent;
        if (container is null)
            return;
        BuildRows(videoRows, container, 0);
    }

    // The rows in order, from a sibling position in the container.
    private void BuildRows(ModRow[] specs, Transform container, int start)
    {
        var wanted = start;
        foreach (var spec in specs)
        {
            if (!spec.Alive)
                Build(spec, container);
            if (!spec.Alive)
                continue;

            var row = spec.Row!.transform;
            if (row.parent.Pointer != container.Pointer)
                row.SetParent(container, false);
            if (row.GetSiblingIndex() != wanted)
                row.SetSiblingIndex(wanted);
            if (!spec.Row!.activeSelf)
                spec.Row.SetActive(true);
            Show(spec);
            wanted++;
        }
    }

    private SettingUIElement? Template(RowKind kind)
    {
        var type = kind switch
        {
            RowKind.Dropdown => Il2CppType.Of<SettingDropdown>(),
            RowKind.Slider => Il2CppType.Of<SettingSlider>(),
            _ => Il2CppType.Of<SettingToggle>(),
        };

        var all = Resources.FindObjectsOfTypeAll(type);
        for (var index = 0; all is not null && index < all.Length; index++)
        {
            var candidate = all[index]?.TryCast<SettingUIElement>();
            if (candidate is null || candidate == null || !candidate.gameObject.scene.IsValid()
                || candidate.gameObject.name.StartsWith("PowerWashVR", StringComparison.Ordinal)
                || candidate.TryCast<SettingLanguageDropdown>() is not null)
                continue;
            var ok = kind switch
            {
                RowKind.Dropdown => candidate.TryCast<SettingDropdown>()?.m_dropdown is { } dropdown && dropdown != null,
                RowKind.Slider => candidate.TryCast<SettingSlider>()?.m_slider is { } slider && slider != null,
                _ => candidate.TryCast<SettingToggle>()?.m_toggle is { } toggle && toggle != null,
            };
            if (ok)
                return candidate;
        }

        return null;
    }

    private void Build(ModRow spec, Transform parent)
    {
        var template = Template(spec.Kind);
        if (template is null)
        {
            if (templateMissingLogged.Add(spec.Kind.ToString()))
                log.Msg($"settings: no {spec.Kind} row to copy for the mod's rows yet");
            return;
        }

        // Built under an inactive holder so none of the copy's scripts wake up
        // before the setting binding is removed.
        var holder = new GameObject("PowerWashVR Holder");
        holder.SetActive(false);
        try
        {
            var copy = UnityEngine.Object.Instantiate(template.gameObject, holder.transform, false);
            copy.name = $"PowerWashVR {spec.Label}";
            var element = copy.GetComponent<SettingUIElement>();
            var label = element?.m_option?.GetComponent<TextMeshProUGUI>();
            var toggle = element?.TryCast<SettingToggle>()?.m_toggle;
            var dropdown = element?.TryCast<SettingDropdown>()?.m_dropdown;
            var slider = element?.TryCast<SettingSlider>()?.m_slider;

            // A slider row's number: a text other than the label showing digits.
            TMP_Text? valueText = null;
            if (spec.Kind == RowKind.Slider)
            {
                var texts = copy.GetComponentsInChildren<TMP_Text>(true);
                for (var index = 0; texts is not null && index < texts.Length; index++)
                {
                    var text = texts[index];
                    if (text is null || text == null || (label is not null && text.Pointer == label.Pointer))
                        continue;
                    if ((text.text ?? "").Any(char.IsDigit))
                    {
                        valueText = text;
                        break;
                    }
                }
            }

            var bound = copy.GetComponentsInChildren<SettingUIElement>(true);
            for (var index = 0; bound is not null && index < bound.Length; index++)
                UnityEngine.Object.DestroyImmediate(bound[index]);
            var localizers = copy.GetComponentsInChildren<UnityEngine.Localization.Components.LocalizeStringEvent>(true);
            for (var index = 0; localizers is not null && index < localizers.Length; index++)
                UnityEngine.Object.DestroyImmediate(localizers[index]);

            copy.transform.SetParent(parent, false);
            copy.SetActive(true);

            if (label is not null && label != null)
                label.text = spec.Label;

            if (toggle is not null && toggle != null)
                toggle.interactable = true;

            if (dropdown is not null && dropdown != null)
            {
                dropdown.ClearOptions();
                var options = new Il2CppSystem.Collections.Generic.List<string>();
                foreach (var option in spec.Options)
                    options.Add(option);
                dropdown.AddOptions(options);
                dropdown.interactable = true;
            }

            if (slider is not null && slider != null)
            {
                slider.minValue = spec.Min;
                slider.maxValue = spec.Max;
                slider.wholeNumbers = true;
                slider.interactable = true;
            }

            spec.Row = copy;
            spec.Toggle = toggle;
            spec.Dropdown = dropdown;
            spec.Slider = slider;
            spec.Text = label;
            spec.ValueText = valueText;
            spec.LastIndex = -1;
            spec.LastValue = float.NaN;
            Show(spec);
            if (Diag.Verbose)
                log.Msg($"settings: \"{spec.Label}\" {spec.Kind.ToString().ToLowerInvariant()} row added (copied from \"{template.name}\", "
                + $"label {(label is null ? "missing" : "found")}"
                + (spec.Kind == RowKind.Slider ? $", value text {(valueText is null ? "missing" : "found")}" : "") + ")");
        }
        catch (Exception exception)
        {
            log.Msg($"settings: \"{spec.Label}\" row not added ({exception.GetType().Name}: {exception.Message})");
        }
        finally
        {
            UnityEngine.Object.Destroy(holder);
        }
    }

    // The control shows the setting's current value.
    private void Show(ModRow spec)
    {
        if (settings is null)
            return;

        if (spec.Text is not null && spec.Text != null && spec.Text.text != spec.Label)
            spec.Text.text = spec.Label;

        switch (spec.Kind)
        {
            case RowKind.Toggle when spec.Toggle is not null && spec.Toggle != null:
                var on = spec.On!(settings);
                if (spec.Toggle.isOn != on)
                    spec.Toggle.SetIsOnWithoutNotify(on);
                break;
            case RowKind.Dropdown when spec.Dropdown is not null && spec.Dropdown != null:
                var index = spec.GetIndex!(settings);
                if (spec.Dropdown.value != index)
                    spec.Dropdown.SetValueWithoutNotify(index);
                spec.LastIndex = index;
                break;
            case RowKind.Slider when spec.Slider is not null && spec.Slider != null:
                var value = Mathf.Clamp(spec.GetValue!(settings), spec.Min, spec.Max);
                if (Mathf.Abs(spec.Slider.value - value) > 0.01f)
                    spec.Slider.SetValueWithoutNotify(value);
                spec.LastValue = value;
                if (spec.ValueText is not null && spec.ValueText != null)
                    spec.ValueText.text = spec.Format(value);
                break;
        }
    }

    // Dropdowns and sliders changed by the pointer are read back here.
    private void PollRows()
    {
        if (settings is null)
            return;

        foreach (var spec in controlRows.Concat(settingRows).Concat(videoRows))
        {
            if (!spec.Alive)
                continue;
            try
            {
                if (spec.Kind == RowKind.Dropdown && spec.Dropdown is { } dropdown && dropdown != null)
                {
                    var index = dropdown.value;
                    if (index != spec.LastIndex && index >= 0 && index < spec.Options.Length)
                    {
                        spec.SetIndex!(settings, index);
                        spec.LastIndex = index;
                        log.Msg($"settings: {spec.Label} -> {spec.Options[index]}");
                    }
                }
                else if (spec.Kind == RowKind.Slider && spec.Slider is { } slider && slider != null)
                {
                    var value = Mathf.Round(slider.value);
                    if (float.IsNaN(spec.LastValue) || Mathf.Abs(value - spec.LastValue) > 0.5f)
                    {
                        spec.SetValue!(settings, value);
                        spec.LastValue = value;
                        if (spec.ValueText is not null && spec.ValueText != null)
                            spec.ValueText.text = spec.Format(value);
                        if (sliderLogs++ < 40)
                            log.Msg($"settings: {spec.Label} -> {spec.Format(value)}");
                    }
                }
            }
            catch
            {
                // Read again next time.
            }
        }
    }

    private int sliderLogs;

    // A row of the settings screen by its setting.
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

    // A game row's label, replaced (its localizer off so it stays).
    private void RelabelRow(SettingType type, string label)
    {
        var row = Row(type);
        if (row is null)
            return;
        var text = row.m_option is { } option && option != null ? option.GetComponent<Il2CppTMPro.TextMeshProUGUI>() : null;
        if (text is null || text == null)
        {
            var texts = row.GetComponentsInChildren<Il2CppTMPro.TextMeshProUGUI>(true);
            text = texts is not null && texts.Length > 0 ? texts[0] : null;
        }

        if (text is null || text == null || text.text == label)
            return;
        var localizer = text.GetComponent<UnityEngine.Localization.Components.LocalizeStringEvent>();
        if (localizer is not null && localizer != null && localizer.enabled)
            localizer.enabled = false;
        var was = text.text;
        text.text = label;
        if (relabelLogs++ < 3)
            log.Msg($"settings: \"{was}\" row renamed \"{label}\"");
    }

    private int relabelLogs;

    private static void HideRow(SettingType type)
    {
        var row = Row(type);
        if (row is not null && row.gameObject.activeSelf)
            row.gameObject.SetActive(false);
    }

    // Everything between the kept rows and their tab page that is not on the
    // way to one of them is switched off. Returns how many items it switched off.
    private static int Isolate(IEnumerable<Transform> keepRows)
    {
        var pageType = Il2CppType.Of<TabPage>();
        var keep = new HashSet<IntPtr>();
        var parents = new List<Transform>();
        Transform? page = null;
        foreach (var row in keepRows)
        {
            for (var node = row; node is not null && node != null; node = node.parent)
            {
                keep.Add(node.Pointer);
                if (node.GetComponent(pageType) is { } found && found != null)
                {
                    page = node;
                    break;
                }

                if (node.parent is { } parent && parent != null && !parents.Any(known => known.Pointer == parent.Pointer))
                    parents.Add(parent);
            }
        }

        if (page is null)
            return 0;

        var hidden = 0;
        foreach (var parent in parents)
        {
            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);
                if (child is null || child == null || keep.Contains(child.Pointer) || !child.gameObject.activeSelf)
                    continue;
                child.gameObject.SetActive(false);
                hidden++;
            }
        }

        return hidden;
    }

    // A click on one of the mod's own rows (MenuPointer asks first).
    internal bool Click(GameObject target)
    {
        if (settings is null)
            return false;

        foreach (var spec in controlRows.Concat(settingRows).Concat(videoRows))
        {
            if (!spec.Alive || spec.Kind != RowKind.Toggle)
                continue;
            for (var node = target.transform; node is not null && node != null; node = node.parent)
            {
                if (node.Pointer != spec.Row!.transform.Pointer)
                    continue;
                spec.Flip!(settings);
                Show(spec);
                log.Msg($"settings: {spec.Label} -> {(spec.On!(settings) ? "On" : "Off")}");
                return true;
            }
        }

        return false;
    }

    private static string Title(string text) =>
        string.Join(" ", text.Split(' ').Select(word => word == "VR" ? word : word[0] + word[1..].ToLowerInvariant()));
}
