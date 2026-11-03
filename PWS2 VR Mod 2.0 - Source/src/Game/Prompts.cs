using HarmonyLib;
using Il2CppFuturLab.PW2;
using MelonLoader;
using UnityEngine;

namespace PowerWashVR.Game;

// No key or gamepad button glyphs in VR: they name keys that do nothing here.
//
// Every prompt glyph the game shows comes from GlyphProvider (images) or
// TMPLocalizationGlyphProvider.CreateSpriteTag (glyphs inside text). While VR
// runs, the first answers with an invisible sprite (so the game's layout code
// still gets a sprite) and the second with no text.
internal static class Prompts
{
    private const string Prefix = "PWVR:";
    private static Sprite? blank;
    private static MelonLogger.Instance? log;

    internal static void Install(HarmonyLib.Harmony harmony, MelonLogger.Instance logger)
    {
        log = logger;
        var provider = typeof(GlyphProvider);
        foreach (var method in provider.GetMethods())
        {
            var parameters = method.GetParameters();
            if (method.Name == "GetGlyph" && parameters.Length >= 1 && parameters[0].ParameterType == typeof(UnityEngine.InputSystem.InputAction))
                Patch(harmony, method, nameof(AfterGetGlyph));
            else if (method.Name == "GetGlyphs" && parameters.Length >= 1 && parameters[0].ParameterType == typeof(UnityEngine.InputSystem.InputAction))
                Patch(harmony, method, nameof(AfterGetGlyphs));
        }

        foreach (var method in typeof(TMPLocalizationGlyphProvider).GetMethods())
        {
            if (method.Name == "CreateSpriteTag")
                Patch(harmony, method, nameof(AfterCreateSpriteTag));
        }
    }

    private static void Patch(HarmonyLib.Harmony harmony, System.Reflection.MethodInfo method, string handler)
    {
        try
        {
            harmony.Patch(method, postfix: new HarmonyMethod(typeof(Prompts).GetMethod(handler,
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)));
        }
        catch (Exception exception)
        {
            log?.Warning($"prompts: {method.Name} not patched ({exception.GetType().Name})");
        }
    }

    private static void AfterGetGlyph(ref Sprite __result)
    {
        if (!Patches.VrActive)
            return;

        try
        {
            __result = Blank();
        }
        catch
        {
            // Keep the game's own glyph.
        }
    }

    private static void AfterGetGlyphs(ref Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite> __result)
    {
        if (!Patches.VrActive)
            return;

        try
        {
            __result = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppReferenceArray<Sprite>(new[] { Blank() });
        }
        catch
        {
            // Keep the game's own glyphs.
        }
    }

    private static void AfterCreateSpriteTag(ref string __result)
    {
        if (Patches.VrActive)
            __result = "";
    }

    private static Sprite Blank()
    {
        if (blank is not null && blank != null)
            return blank;

        var texture = new Texture2D(4, 4, TextureFormat.RGBA32, false) { name = Prefix };
        var clear = new Color32(0, 0, 0, 0);
        var pixels = new Color32[16];
        for (var index = 0; index < pixels.Length; index++)
            pixels[index] = clear;
        texture.SetPixels32(pixels);
        texture.Apply(false, true);
        blank = Sprite.Create(texture, new Rect(0, 0, 4, 4), new Vector2(0.5f, 0.5f), 100f);
        blank.name = Prefix;
        UnityEngine.Object.DontDestroyOnLoad(blank);
        UnityEngine.Object.DontDestroyOnLoad(texture);
        return blank;
    }
}
