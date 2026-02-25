using System.Reflection;
using System.Collections.Generic;
using UnityEngine;

namespace MineMogul.ToolImportApi.Api
{
    public sealed class ToolRegistrationRequest
    {
        public string ModId;
        public string ToolId;
        public string CacheNamespace;
        public string DisplayName;
        public string Description;
        public string IconResourceName;
        public string WorldModelResourceName;
        public string ViewModelResourceName;
        public ToolShopMetadata ShopMetadata;
        public Assembly AssetAssembly;
        public int? PreferredSavableId;
        public ArmPoseRequest ArmPose;
        public ToolViewTransformRequest ViewModelTransform;
        public bool EnableCustomPositioning;
        public string CustomPositioningResourceName;
    }

    public sealed class ToolShopMetadata
    {
        public int Price = 500;
        public string Category = "Tools";
        public bool IsLockedByDefault;
        public int MaxStackSize = 1;
        public string QAction = "Rotate";
        public int? ShopBlockId;
    }

    public sealed class ArmPoseRequest
    {
        public string PreferredArmPair;
        public List<ArmBonePose> BoneOverrides = new List<ArmBonePose>(32);
    }

    public sealed class ArmBonePose
    {
        public string BoneName;
        public string BonePath;
        public Vector3? LocalPosition;
        public Vector3? LocalEulerAngles;
        public Vector3? LocalScale;
    }

    public sealed class ToolViewTransformRequest
    {
        public string TransformPath;
        public Vector3? LocalPosition;
        public Vector3? LocalEulerAngles;
        public Vector3? LocalScale;
    }
}
