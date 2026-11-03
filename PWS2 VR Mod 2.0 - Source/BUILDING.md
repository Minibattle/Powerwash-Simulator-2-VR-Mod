# Building PowerWash VR

Requirements: Windows, the .NET SDK (6 or newer), PowerWash Simulator 2 with
MelonLoader 0.7.3 installed and the game launched once (that first launch
generates `MelonLoader\Il2CppAssemblies`, which the project references).

```
dotnet build -c Release -p:GamePath="C:\Program Files (x86)\Steam\steamapps\common\PowerWash Simulator 2"
```

The DLL is written to `bin\Release\net6.0\PowerWashVR.dll`; copy it to
`<game>\Mods`. `installer\` and `installer-linux/` hold the scripts shipped in the release zips
(Install.cmd puts MelonLoader, Unity's OpenXR plugin and the mod in place).

Layout:
- `src/Mod.cs` - entry point (MelonMod), frame order, settings
- `src/OpenXR/` - OpenXR loader/session, bindings, hand tracking
- `src/Input/` - controller state
- `src/Game/` - everything that touches the game (rig, hands, washer,
  belt, backpack, UI, smooth camera, multiplayer body sync, patches)
