using System;
using System.Collections.Generic;
using MineMogul.ToolImportApi.Api;
using UnityEngine;

namespace MineMogul.ToolImportApi.Core
{
    [Serializable]
    internal sealed class CustomPositioningDocument
    {
        public string preferredArmPair;
        public CustomPositioningBoneOverride[] boneOverrides;
        public CustomPositioningViewTransform viewModelTransform;
    }

    [Serializable]
    internal sealed class CustomPositioningBoneOverride
    {
        public string boneName;
        public string bonePath;
        public bool hasLocalPosition;
        public Vector3 localPosition;
        public bool hasLocalEulerAngles;
        public Vector3 localEulerAngles;
        public bool hasLocalScale;
        public Vector3 localScale;
    }

    [Serializable]
    internal sealed class CustomPositioningViewTransform
    {
        public string transformPath;
        public bool hasLocalPosition;
        public Vector3 localPosition;
        public bool hasLocalEulerAngles;
        public Vector3 localEulerAngles;
        public bool hasLocalScale;
        public Vector3 localScale;
    }

    internal static class CustomPositioningConverter
    {
        public static CustomPositioningDocument FromRequests(ArmPoseRequest armPose, ToolViewTransformRequest viewModelTransform)
        {
            return new CustomPositioningDocument
            {
                preferredArmPair = armPose != null ? armPose.PreferredArmPair : null,
                boneOverrides = SerializeBoneOverrides(armPose),
                viewModelTransform = SerializeViewTransform(viewModelTransform)
            };
        }

        public static bool TryToRequests(
            CustomPositioningDocument document,
            out ArmPoseRequest armPose,
            out ToolViewTransformRequest viewModelTransform,
            out string error)
        {
            armPose = null;
            viewModelTransform = null;
            error = null;

            if (document == null)
            {
                error = "Custom positioning document is null.";
                return false;
            }

            List<ArmBonePose> overrides = DeserializeBoneOverrides(document.boneOverrides);

            if (!string.IsNullOrWhiteSpace(document.preferredArmPair) || overrides.Count > 0)
            {
                armPose = new ArmPoseRequest
                {
                    PreferredArmPair = document.preferredArmPair,
                    BoneOverrides = overrides
                };
            }

            viewModelTransform = DeserializeViewTransform(document.viewModelTransform);

            return true;
        }

        private static CustomPositioningBoneOverride[] SerializeBoneOverrides(ArmPoseRequest armPose)
        {
            List<CustomPositioningBoneOverride> serializedOverrides = new List<CustomPositioningBoneOverride>(
                armPose != null && armPose.BoneOverrides != null ? armPose.BoneOverrides.Count : 0);

            if (armPose == null || armPose.BoneOverrides == null)
            {
                return serializedOverrides.ToArray();
            }

            for (int i = 0; i < armPose.BoneOverrides.Count; i++)
            {
                ArmBonePose source = armPose.BoneOverrides[i];
                if (source == null)
                {
                    continue;
                }

                serializedOverrides.Add(new CustomPositioningBoneOverride
                {
                    boneName = source.BoneName,
                    bonePath = source.BonePath,
                    hasLocalPosition = source.LocalPosition.HasValue,
                    localPosition = source.LocalPosition.GetValueOrDefault(),
                    hasLocalEulerAngles = source.LocalEulerAngles.HasValue,
                    localEulerAngles = source.LocalEulerAngles.GetValueOrDefault(),
                    hasLocalScale = source.LocalScale.HasValue,
                    localScale = source.LocalScale.GetValueOrDefault()
                });
            }

            return serializedOverrides.ToArray();
        }

        private static CustomPositioningViewTransform SerializeViewTransform(ToolViewTransformRequest viewModelTransform)
        {
            if (viewModelTransform == null)
            {
                return null;
            }

            return new CustomPositioningViewTransform
            {
                transformPath = viewModelTransform.TransformPath,
                hasLocalPosition = viewModelTransform.LocalPosition.HasValue,
                localPosition = viewModelTransform.LocalPosition.GetValueOrDefault(),
                hasLocalEulerAngles = viewModelTransform.LocalEulerAngles.HasValue,
                localEulerAngles = viewModelTransform.LocalEulerAngles.GetValueOrDefault(),
                hasLocalScale = viewModelTransform.LocalScale.HasValue,
                localScale = viewModelTransform.LocalScale.GetValueOrDefault()
            };
        }

        private static List<ArmBonePose> DeserializeBoneOverrides(CustomPositioningBoneOverride[] sourceOverrides)
        {
            List<ArmBonePose> overrides = new List<ArmBonePose>(
                sourceOverrides != null ? sourceOverrides.Length : 0);

            if (sourceOverrides == null)
            {
                return overrides;
            }

            for (int i = 0; i < sourceOverrides.Length; i++)
            {
                ArmBonePose converted = DeserializeBoneOverride(sourceOverrides[i]);
                if (converted != null)
                {
                    overrides.Add(converted);
                }
            }

            return overrides;
        }

        private static ArmBonePose DeserializeBoneOverride(CustomPositioningBoneOverride source)
        {
            if (source == null)
            {
                return null;
            }

            bool hasAnyValue = source.hasLocalPosition || source.hasLocalEulerAngles || source.hasLocalScale;
            if (!hasAnyValue &&
                string.IsNullOrWhiteSpace(source.boneName) &&
                string.IsNullOrWhiteSpace(source.bonePath))
            {
                return null;
            }

            return new ArmBonePose
            {
                BoneName = source.boneName,
                BonePath = source.bonePath,
                LocalPosition = source.hasLocalPosition ? source.localPosition : (Vector3?)null,
                LocalEulerAngles = source.hasLocalEulerAngles ? source.localEulerAngles : (Vector3?)null,
                LocalScale = source.hasLocalScale ? source.localScale : (Vector3?)null
            };
        }

        private static ToolViewTransformRequest DeserializeViewTransform(CustomPositioningViewTransform view)
        {
            if (view == null)
            {
                return null;
            }

            bool hasAnyValue = view.hasLocalPosition || view.hasLocalEulerAngles || view.hasLocalScale;
            if (!hasAnyValue && string.IsNullOrWhiteSpace(view.transformPath))
            {
                return null;
            }

            return new ToolViewTransformRequest
            {
                TransformPath = view.transformPath,
                LocalPosition = view.hasLocalPosition ? view.localPosition : (Vector3?)null,
                LocalEulerAngles = view.hasLocalEulerAngles ? view.localEulerAngles : (Vector3?)null,
                LocalScale = view.hasLocalScale ? view.localScale : (Vector3?)null
            };
        }
    }
}
