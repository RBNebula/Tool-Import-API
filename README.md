# Tool Import API

Tool Import API is a BepInEx plugin for MineMogul that lets other mods register custom tools from embedded assets.

You provide:
- `tool-icon.png` (shop/inventory icon)
- `tool-world.glb` (in-world dropped model)
- `tool-view.glb` (first-person held model)
- Optional arm pose file: `yourmod.yourtool.arm-pose.cs`

Tool Import API handles:
- Registration queue (safe to call during `Awake`)
- Runtime tool creation from your embedded assets
- Save/load prefab registration
- Optional per-instance custom save/load callbacks
- Optional ModMogul shop integration
- Optional arm pose override loading from embedded `.arm-pose.cs`

## Requirements
- BepInEx 5.x
- MineMogul
- .NET Framework target compatible with your mod setup (typically `net472`)
- Hard dependency on `com.main.toolimportapi` in your tool plugin

## Quick Start

### 1) Minimal register call
```csharp
using BepInEx;
using MineMogul.ToolImportApi.Api;

[BepInPlugin("com.example.mytool", "My Tool", "1.0.0")]
[BepInDependency("com.main.toolimportapi", BepInDependency.DependencyFlags.HardDependency)]
public sealed class MyToolPlugin : BaseUnityPlugin
{
    private void Awake()
    {
        ToolRegistrationResult result = ToolImportApi.RegisterTool(new ToolRegistrationRequest
        {
            ModId = "examplemod",
            ToolId = "mytool",
            CacheNamespace = "examplemod",
            DisplayName = "My Tool",
            Description = "Example imported tool.",
            IconResourceName = "MyTool.Assets.Images.tool-icon.png",
            WorldModelResourceName = "MyTool.Assets.Models.tool-world.glb",
            ViewModelResourceName = "MyTool.Assets.Models.tool-view.glb"
        });

        Logger.LogInfo($"Register: {result.Success} {result.FullToolId} {result.Message}");
    }
}
```

### 2) Enable custom arm pose (optional)
```csharp
request.EnableCustomPositioning = true;
request.CustomPositioningResourceName = "examplemod.mytool.arm-pose.cs";
```

If custom positioning is enabled but no pose file is found, Tool Import API falls back to default pose and continues.

### 3) Save custom per-instance tool state (optional)
Use `CustomSaveData` and `CustomLoadData` in `ToolRegistrationRequest` when your tool has runtime settings (fire mode, zoom level, attachment state, etc.).

```csharp
[System.Serializable]
private sealed class GunStateSaveData
{
    public int FireMode;
    public bool SafetyOn;
}

ToolRegistrationRequest request = new ToolRegistrationRequest
{
    ModId = "examplemod",
    ToolId = "mygun",
    CacheNamespace = "examplemod",
    DisplayName = "My Gun",
    Description = "Example gun with persistent state.",
    IconResourceName = "MyTool.Assets.Images.tool-icon.png",
    WorldModelResourceName = "MyTool.Assets.Models.tool-world.glb",
    ViewModelResourceName = "MyTool.Assets.Models.tool-view.glb",

    CustomSaveData = tool =>
    {
        GunViewModelController vm = tool.GetComponentInChildren<GunViewModelController>(true);
        if (vm == null)
        {
            return null;
        }

        return JsonUtility.ToJson(new GunStateSaveData
        {
            FireMode = vm.CurrentFireMode,
            SafetyOn = vm.SafetyOn
        });
    },

    CustomLoadData = (tool, customJson) =>
    {
        if (string.IsNullOrWhiteSpace(customJson))
        {
            return;
        }

        GunStateSaveData saved = JsonUtility.FromJson<GunStateSaveData>(customJson);
        if (saved == null)
        {
            return;
        }

        GunViewModelController vm = tool.GetComponentInChildren<GunViewModelController>(true);
        if (vm == null)
        {
            return;
        }

        vm.CurrentFireMode = saved.FireMode;
        vm.SafetyOn = saved.SafetyOn;
    }
};
```

Notes:
- Keep this payload small and JSON-serializable.
- The callback runs per tool instance, so different copies of the same tool can keep different saved settings.

## Full Minimal Mod Example

### Folder layout
```text
MyToolMod/
  MyToolMod.csproj
  MyToolPlugin.cs
  Assets/
    Models/
      tool-world.glb
      tool-view.glb
    Images/
      tool-icon.png
    Poses/
      examplemod.mytool.arm-pose.cs   (optional)
```

### `MyToolMod.csproj`
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net472</TargetFramework>
    <LangVersion>latest</LangVersion>
    <Nullable>enable</Nullable>
    <AssemblyName>MyToolMod</AssemblyName>
    <RootNamespace>MyToolMod</RootNamespace>
  </PropertyGroup>

  <ItemGroup>
    <Reference Include="BepInEx">
      <HintPath>..\Libs\BepInEx.dll</HintPath>
    </Reference>
    <Reference Include="Assembly-CSharp">
      <HintPath>..\Libs\Assembly-CSharp.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine">
      <HintPath>..\Libs\UnityEngine.dll</HintPath>
    </Reference>
    <Reference Include="UnityEngine.CoreModule">
      <HintPath>..\Libs\UnityEngine.CoreModule.dll</HintPath>
    </Reference>
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\Tool Import API\ToolImportApi.csproj" />
  </ItemGroup>

  <ItemGroup>
    <Compile Remove="Assets\Poses\*.arm-pose.cs" />

    <EmbeddedResource Include="Assets\Models\tool-world.glb">
      <LogicalName>MyToolMod.Assets.Models.tool-world.glb</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="Assets\Models\tool-view.glb">
      <LogicalName>MyToolMod.Assets.Models.tool-view.glb</LogicalName>
    </EmbeddedResource>
    <EmbeddedResource Include="Assets\Images\tool-icon.png">
      <LogicalName>MyToolMod.Assets.Images.tool-icon.png</LogicalName>
    </EmbeddedResource>

    <!-- Optional: include if file exists; safe when missing -->
    <EmbeddedResource Include="Assets\Poses\*.arm-pose.cs">
      <LogicalName>%(Filename)%(Extension)</LogicalName>
    </EmbeddedResource>
  </ItemGroup>
</Project>
```

### `MyToolPlugin.cs` (base code only)
```csharp
using BepInEx;
using MineMogul.ToolImportApi.Api;
using UnityEngine;

namespace MyToolMod;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("com.main.toolimportapi", BepInDependency.DependencyFlags.HardDependency)]
public sealed class MyToolPlugin : BaseUnityPlugin
{
    public const string PluginGuid = "com.example.mytoolmod";
    public const string PluginName = "My Tool Mod";
    public const string PluginVersion = "1.0.0";

    private const string ModId = "examplemod";
    private const string ToolId = "mytool";
    private const string FullToolId = ModId + "." + ToolId;

    private const string IconResource = "MyToolMod.Assets.Images.tool-icon.png";
    private const string WorldResource = "MyToolMod.Assets.Models.tool-world.glb";
    private const string ViewResource = "MyToolMod.Assets.Models.tool-view.glb";
    private const string PoseResource = FullToolId + ".arm-pose.cs";

    private void Awake()
    {
        ToolRegistrationRequest request = new ToolRegistrationRequest
        {
            ModId = ModId,
            ToolId = ToolId,
            CacheNamespace = ModId,
            DisplayName = "My Tool",
            Description = "Example imported tool.",
            IconResourceName = IconResource,
            WorldModelResourceName = WorldResource,
            ViewModelResourceName = ViewResource,
            EnableCustomPositioning = true,
            CustomPositioningResourceName = PoseResource,
            ShopMetadata = new ToolShopMetadata
            {
                Price = 500,
                Category = "Tools",
                IsLockedByDefault = false,
                MaxStackSize = 1,
                QAction = "Rotate"
            }
        };

        ToolRegistrationResult result = ToolImportApi.RegisterTool(request);
        if (!result.Success)
        {
            Logger.LogWarning("[MyTool] Registration failed: " + result.Message);
            return;
        }

        Logger.LogInfo("[MyTool] Registration ok: " + result.FullToolId + " (" + result.Message + ")");
    }
}
```

## Arm Pose Workflow (`.arm-pose.cs`)

1. Launch the game and close for first time run.
2. Enable Tool Import API arm pose editor in `com.main.toolimportapi.cfg`:
   - `ArmPoseEditor.EnableUi = true`
3. Launch game and equip your tool.
4. Press `F8` to open the pose editor.
5. Adjust arm variant, arm bones, and view model transform.
6. If required click `Re-Detect Tool Target` or `Refresh Rig` Buttons.
7. Click `Capture Current Rotations`.
6. Click `export`. File is written to:
   - `BepInEx/plugins/ToolImportAPI/ArmPoseExports/<fullToolId>.arm-pose.cs`
7. Copy that file into your mod:
   - `Assets/Poses/<fullToolId>.arm-pose.cs`
8. Rebuild and redeploy your mod.

## What You Need For Your Mod (and where)

Use this structure in your mod project:

```text
YourMod/
  YourMod.csproj
  YourModPlugin.cs
  Assets/
    Models/
      your-tool-world.glb
      your-tool-view.glb
    Images/
      your-tool-icon.png
    Poses/
      yourmod.yourtool.arm-pose.cs   (optional)
```

File placement:
- Plugin code file: `YourModPlugin.cs` at project root (or your normal code folder).
- Project file: `YourMod.csproj` at project root.
- World model: `Assets/Models/your-tool-world.glb`
- View model: `Assets/Models/your-tool-view.glb`
- Icon: `Assets/Images/your-tool-icon.png`
- Optional arm pose: `Assets/Poses/<fullToolId>.arm-pose.cs`

In `ToolRegistrationRequest`:
- `WorldModelResourceName` must match your embedded world model logical name.
- `ViewModelResourceName` must match your embedded view model logical name.
- `IconResourceName` must match your embedded icon logical name.
- If using custom pose:
  - `EnableCustomPositioning = true`
  - `CustomPositioningResourceName = "<fullToolId>.arm-pose.cs"`

## Troubleshooting

### Build fails with pose `.cs` errors
Cause: pose file is being compiled as code.  
Fix: add:
```xml
<Compile Remove="Assets\Poses\*.arm-pose.cs" />
```

### Registration succeeds but model/icon fails to load
Cause: embedded resource name mismatch.  
Fix: log and verify:
```csharp
foreach (string name in typeof(MyToolPlugin).Assembly.GetManifestResourceNames())
{
    Logger.LogInfo(name);
}
```

### Custom pose enabled but not applied
Check:
- `EnableCustomPositioning = true`
- `CustomPositioningResourceName = "<fullToolId>.arm-pose.cs"`
- pose file is embedded in your DLL
- exported file name exactly matches your `fullToolId`

If not found, Tool Import API logs a warning and uses default pose.

## Current Public API Fields (`ToolRegistrationRequest`)
- `ModId`
- `ToolId`
- `CacheNamespace`
- `DisplayName`
- `Description`
- `IconResourceName`
- `WorldModelResourceName`
- `ViewModelResourceName`
- `ShopMetadata`
- `PreferredSavableId`
- `ArmPose`
- `ViewModelTransform`
- `EnableCustomPositioning`
- `CustomPositioningResourceName`
