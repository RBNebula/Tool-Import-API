using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace MineMogul.ToolImportApi.Core
{
    internal static class CustomPositioningCsParser
    {
        private static readonly Regex BoneBlockRegex = new Regex(
            @"new\s+ArmBonePose\s*\{(?<body>.*?)\}\s*,",
            RegexOptions.Singleline | RegexOptions.Compiled);

        private static readonly Regex ViewTransformBlockRegex = new Regex(
            @"request\.ViewModelTransform\s*=\s*new\s+ToolViewTransformRequest\s*\{(?<body>.*?)\}\s*;",
            RegexOptions.Singleline | RegexOptions.Compiled);

        public static bool TryParseDocument(string source, out CustomPositioningDocument document, out string error)
        {
            document = null;
            error = null;

            if (string.IsNullOrWhiteSpace(source))
            {
                error = "C# custom positioning source is empty.";
                return false;
            }

            document = new CustomPositioningDocument
            {
                preferredArmPair = TryReadStringField(source, "PreferredArmPair", out string preferredPair)
                    ? preferredPair
                    : null,
                boneOverrides = ParseBoneOverrides(source),
                viewModelTransform = ParseViewModelTransform(source)
            };

            return true;
        }

        private static CustomPositioningBoneOverride[] ParseBoneOverrides(string source)
        {
            MatchCollection matches = BoneBlockRegex.Matches(source);
            List<CustomPositioningBoneOverride> overrides = new List<CustomPositioningBoneOverride>(matches.Count);

            for (int i = 0; i < matches.Count; i++)
            {
                string body = matches[i].Groups["body"].Value;

                TryReadStringField(body, "BoneName", out string boneName);
                TryReadStringField(body, "BonePath", out string bonePath);
                bool hasLocalPosition = TryReadVector3Field(body, "LocalPosition", out Vector3 localPosition);
                bool hasLocalEulerAngles = TryReadVector3Field(body, "LocalEulerAngles", out Vector3 localEulerAngles);
                bool hasLocalScale = TryReadVector3Field(body, "LocalScale", out Vector3 localScale);

                bool hasAnyValue =
                    !string.IsNullOrWhiteSpace(boneName) ||
                    !string.IsNullOrWhiteSpace(bonePath) ||
                    hasLocalPosition ||
                    hasLocalEulerAngles ||
                    hasLocalScale;
                if (!hasAnyValue)
                {
                    continue;
                }

                overrides.Add(new CustomPositioningBoneOverride
                {
                    boneName = boneName,
                    bonePath = bonePath,
                    hasLocalPosition = hasLocalPosition,
                    localPosition = hasLocalPosition ? localPosition : Vector3.zero,
                    hasLocalEulerAngles = hasLocalEulerAngles,
                    localEulerAngles = hasLocalEulerAngles ? localEulerAngles : Vector3.zero,
                    hasLocalScale = hasLocalScale,
                    localScale = hasLocalScale ? localScale : Vector3.zero
                });
            }

            return overrides.ToArray();
        }

        private static CustomPositioningViewTransform ParseViewModelTransform(string source)
        {
            Match match = ViewTransformBlockRegex.Match(source);
            if (!match.Success)
            {
                return null;
            }

            string body = match.Groups["body"].Value;
            TryReadStringField(body, "TransformPath", out string transformPath);
            bool hasLocalPosition = TryReadVector3Field(body, "LocalPosition", out Vector3 localPosition);
            bool hasLocalEulerAngles = TryReadVector3Field(body, "LocalEulerAngles", out Vector3 localEulerAngles);
            bool hasLocalScale = TryReadVector3Field(body, "LocalScale", out Vector3 localScale);

            bool hasAnyValue =
                !string.IsNullOrWhiteSpace(transformPath) ||
                hasLocalPosition ||
                hasLocalEulerAngles ||
                hasLocalScale;
            if (!hasAnyValue)
            {
                return null;
            }

            return new CustomPositioningViewTransform
            {
                transformPath = transformPath,
                hasLocalPosition = hasLocalPosition,
                localPosition = hasLocalPosition ? localPosition : Vector3.zero,
                hasLocalEulerAngles = hasLocalEulerAngles,
                localEulerAngles = hasLocalEulerAngles ? localEulerAngles : Vector3.zero,
                hasLocalScale = hasLocalScale,
                localScale = hasLocalScale ? localScale : Vector3.zero
            };
        }

        private static bool TryReadStringField(string source, string fieldName, out string value)
        {
            value = null;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            string pattern =
                Regex.Escape(fieldName) +
                @"\s*=\s*""(?<value>(?:\\.|[^""])*)""";
            Match match = Regex.Match(source, pattern, RegexOptions.Singleline);
            if (!match.Success)
            {
                return false;
            }

            value = UnescapeCSharpString(match.Groups["value"].Value);
            return true;
        }

        private static bool TryReadVector3Field(string source, string fieldName, out Vector3 value)
        {
            value = Vector3.zero;
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(fieldName))
            {
                return false;
            }

            string pattern =
                Regex.Escape(fieldName) +
                @"\s*=\s*new\s+Vector3\(\s*(?<x>[+-]?\d+(?:\.\d+)?)f\s*,\s*(?<y>[+-]?\d+(?:\.\d+)?)f\s*,\s*(?<z>[+-]?\d+(?:\.\d+)?)f\s*\)";
            Match match = Regex.Match(source, pattern, RegexOptions.Singleline);
            if (!match.Success)
            {
                return false;
            }

            if (!TryParseFloat(match.Groups["x"].Value, out float x) ||
                !TryParseFloat(match.Groups["y"].Value, out float y) ||
                !TryParseFloat(match.Groups["z"].Value, out float z))
            {
                return false;
            }

            value = new Vector3(x, y, z);
            return true;
        }

        private static bool TryParseFloat(string source, out float value)
        {
            return float.TryParse(source, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        private static string UnescapeCSharpString(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
