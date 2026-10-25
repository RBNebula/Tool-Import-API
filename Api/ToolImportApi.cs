using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using BepInEx.Logging;
using MineMogul.ToolImportApi.Core;

namespace MineMogul.ToolImportApi.Api
{
    public static class ToolImportApi
    {
        private static readonly object Sync = new object();
        private static readonly List<PendingApiRegistration> PendingBeforeInit = new List<PendingApiRegistration>(8);

        private static ToolImportRegistry _sink;

        internal static void Initialize(ToolImportRegistry sink, ManualLogSource logger)
        {
            if (sink == null)
            {
                return;
            }

            List<PendingApiRegistration> staged = null;
            lock (Sync)
            {
                _sink = sink;

                if (PendingBeforeInit.Count > 0)
                {
                    staged = new List<PendingApiRegistration>(PendingBeforeInit.Count);
                    for (int i = 0; i < PendingBeforeInit.Count; i++)
                    {
                        staged.Add(PendingBeforeInit[i]);
                    }

                    PendingBeforeInit.Clear();
                }
            }

            if (staged == null)
            {
                return;
            }

            for (int i = 0; i < staged.Count; i++)
            {
                ToolRegistrationResult result = sink.Register(staged[i].Request, staged[i].SourceAssembly);
                logger?.LogInfo(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Flushed queued registration " + result.FullToolId + ": " + result.Message);
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public static ToolRegistrationResult RegisterTool(ToolRegistrationRequest request)
        {
            if (request == null)
            {
                return ToolRegistrationResult.Fail(null, "Registration request is null.");
            }

            ToolRegistrationRequest normalized = CloneRequest(request);
            Assembly sourceAssembly = normalized.AssetAssembly ?? Assembly.GetCallingAssembly();
            if (sourceAssembly == null)
            {
                return ToolRegistrationResult.Fail(null, "Unable to determine source assembly for embedded resources.");
            }

            normalized.AssetAssembly = sourceAssembly;
            string fullToolId = BuildFullToolId(normalized.ModId, normalized.ToolId);

            ToolImportRegistry sink;
            lock (Sync)
            {
                if (_sink == null)
                {
                    PendingBeforeInit.Add(new PendingApiRegistration(normalized, sourceAssembly));
                    return ToolRegistrationResult.Ok(fullToolId, "Queued for registration (Tool Import API not initialized yet).");
                }

                sink = _sink;
            }

            return sink.Register(normalized, sourceAssembly);
        }

        public static bool TrySpawnTool(string fullToolId, out string message)
        {
            if (!TryGetSink(out ToolImportRegistry sink))
            {
                message = "Tool Import API is not initialized yet.";
                return false;
            }

            return sink.TrySpawnTool(fullToolId, out message);
        }

        public static bool TrySpawnAnyRegisteredTool(out string message)
        {
            if (!TryGetSink(out ToolImportRegistry sink))
            {
                message = "Tool Import API is not initialized yet.";
                return false;
            }

            return sink.TrySpawnFirstTool(out message);
        }

        private static ToolRegistrationRequest CloneRequest(ToolRegistrationRequest request)
        {
            ToolRegistrationRequest clone = new ToolRegistrationRequest
            {
                ModId = request.ModId,
                ToolId = request.ToolId,
                CacheNamespace = request.CacheNamespace,
                DisplayName = request.DisplayName,
                Description = request.Description,
                IconResourceName = request.IconResourceName,
                WorldModelResourceName = request.WorldModelResourceName,
                ViewModelResourceName = request.ViewModelResourceName,
                AssetAssembly = request.AssetAssembly,
                PreferredSavableId = request.PreferredSavableId,
                ArmPose = CloneArmPose(request.ArmPose),
                ViewModelTransform = CloneViewModelTransform(request.ViewModelTransform),
                EnableCustomPositioning = request.EnableCustomPositioning,
                CustomPositioningResourceName = request.CustomPositioningResourceName,
                CustomSaveData = request.CustomSaveData,
                CustomLoadData = request.CustomLoadData
            };

            if (request.ShopMetadata != null)
            {
                clone.ShopMetadata = new ToolShopMetadata
                {
                    Price = request.ShopMetadata.Price,
                    Category = request.ShopMetadata.Category,
                    IsLockedByDefault = request.ShopMetadata.IsLockedByDefault,
                    MaxStackSize = request.ShopMetadata.MaxStackSize,
                    QAction = request.ShopMetadata.QAction,
                    ShopBlockId = request.ShopMetadata.ShopBlockId
                };
            }

            return clone;
        }

        private static ArmPoseRequest CloneArmPose(ArmPoseRequest armPose)
        {
            if (armPose == null)
            {
                return null;
            }

            ArmPoseRequest clone = new ArmPoseRequest
            {
                PreferredArmPair = armPose.PreferredArmPair,
                BoneOverrides = new List<ArmBonePose>(armPose.BoneOverrides != null ? armPose.BoneOverrides.Count : 0)
            };

            if (armPose.BoneOverrides == null)
            {
                return clone;
            }

            for (int i = 0; i < armPose.BoneOverrides.Count; i++)
            {
                ArmBonePose source = armPose.BoneOverrides[i];
                if (source == null)
                {
                    continue;
                }

                clone.BoneOverrides.Add(new ArmBonePose
                {
                    BoneName = source.BoneName,
                    BonePath = source.BonePath,
                    LocalPosition = source.LocalPosition,
                    LocalEulerAngles = source.LocalEulerAngles,
                    LocalScale = source.LocalScale
                });
            }

            return clone;
        }

        private static ToolViewTransformRequest CloneViewModelTransform(ToolViewTransformRequest viewModelTransform)
        {
            if (viewModelTransform == null)
            {
                return null;
            }

            return new ToolViewTransformRequest
            {
                TransformPath = viewModelTransform.TransformPath,
                LocalPosition = viewModelTransform.LocalPosition,
                LocalEulerAngles = viewModelTransform.LocalEulerAngles,
                LocalScale = viewModelTransform.LocalScale
            };
        }

        private static string BuildFullToolId(string modId, string toolId)
        {
            if (string.IsNullOrWhiteSpace(modId))
            {
                return toolId ?? "<missing-tool-id>";
            }

            if (string.IsNullOrWhiteSpace(toolId))
            {
                return modId + ".<missing-tool-id>";
            }

            return modId + "." + toolId;
        }

        private static bool TryGetSink(out ToolImportRegistry sink)
        {
            sink = null;
            lock (Sync)
            {
                if (_sink == null)
                {
                    return false;
                }

                sink = _sink;
                return true;
            }
        }

        private readonly struct PendingApiRegistration
        {
            public readonly ToolRegistrationRequest Request;
            public readonly Assembly SourceAssembly;

            public PendingApiRegistration(ToolRegistrationRequest request, Assembly sourceAssembly)
            {
                Request = request;
                SourceAssembly = sourceAssembly;
            }
        }
    }
}
