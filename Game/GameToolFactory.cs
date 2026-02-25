using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Logging;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;
using MineMogul.ToolImportApi.Loaders;
using UnityEngine;

namespace MineMogul.ToolImportApi.Game
{
    internal sealed class GameToolFactory
    {
        private static readonly FieldInfo InteractionsField =
            typeof(BaseHeldTool).GetField("_interactions", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly FieldInfo SaveLookupField =
            typeof(SavingLoadingManager).GetField("_lookup", BindingFlags.Instance | BindingFlags.NonPublic);

        private static readonly string[,] PreferredArmPairs = new string[,]
        {
            { "Arm_Shirt&Gloves_l", "Arm_Shirt&Gloves_r" },
            { "Arm_Standard&Gloves_l", "Arm_Standard&Gloves_r" },
            { "Arm_Standard&Shirt.l", "Arm_Standard&Shirt.r" },
            { "Arm_Standard.l", "Arm_Standard.r" }
        };

        private readonly ManualLogSource _logger;
        private readonly ReflectionModMogulModelLoader _modelLoader;
        private readonly GameToolFactoryOptions _options;

        public GameToolFactory(ManualLogSource logger, ReflectionModMogulModelLoader modelLoader, GameToolFactoryOptions options)
        {
            _logger = logger;
            _modelLoader = modelLoader;
            _options = options ?? new GameToolFactoryOptions();
        }

        public IEnumerator CreateRegisteredTool(ToolBuildRequest request, ToolBuildResult result)
        {
            if (result == null)
            {
                yield break;
            }

            if (request == null)
            {
                result.Success = false;
                result.Error = "Build request is null.";
                yield break;
            }

            VerboseLog(
                "CreateRegisteredTool begin. fullToolId=" + request.FullToolId +
                ", worldRes=" + request.Request.WorldModelResourceName +
                ", viewRes=" + request.Request.ViewModelResourceName +
                ", iconRes=" + request.Request.IconResourceName +
                ", worldBytes=" + (request.WorldModelBytes != null ? request.WorldModelBytes.Length : 0) +
                ", viewBytes=" + (request.ViewModelBytes != null ? request.ViewModelBytes.Length : 0));

            GameObject template = null;
            BoxCollider collider = null;
            GameObject worldRoot = null;
            GameObject viewRoot = null;
            RuntimeImportedTool tool = null;

            try
            {
                template = new GameObject(request.FullToolId + ".Template");
                template.SetActive(false);
                template.tag = "Grabbable";
                template.layer = LayerMask.NameToLayer("Default");
                UnityEngine.Object.DontDestroyOnLoad(template);

                Rigidbody rb = template.AddComponent<Rigidbody>();
                rb.mass = 2f;
                rb.useGravity = true;
                rb.isKinematic = true;
                rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
                rb.interpolation = RigidbodyInterpolation.Interpolate;

                collider = template.AddComponent<BoxCollider>();
                collider.size = new Vector3(0.35f, 0.18f, 0.55f);
                collider.center = new Vector3(0f, 0.08f, 0f);

                worldRoot = new GameObject("WorldModel");
                worldRoot.transform.SetParent(template.transform, false);

                viewRoot = new GameObject("ViewModel");
                viewRoot.transform.SetParent(template.transform, false);

                tool = template.AddComponent<RuntimeImportedTool>();
                tool.RegistrationKey = request.FullToolId;
                tool.Name = request.Request.DisplayName;
                tool.Description = string.IsNullOrWhiteSpace(request.Request.Description)
                    ? "Imported mod tool."
                    : request.Request.Description;
                tool.SavableObjectID = (SavableObjectID)request.SavableId;
                tool.MaxAmount = 1;
                tool.Quantity = 1;
                tool.EquipWhenPickedUp = true;
                tool.WorldModel = worldRoot;
                tool.ViewModel = viewRoot;
                tool.InventoryIcon = request.Icon;
                tool.ProgrammerInventoryIcon = request.Icon;

                ConfigureInteractions(tool);
            }
            catch (Exception ex)
            {
                if (template != null)
                {
                    UnityEngine.Object.Destroy(template);
                }

                result.Success = false;
                result.Error = "Tool template creation failed: " + ex;
                yield break;
            }

            VerboseLog("Loading world model for " + request.FullToolId + "...");
            GameObject loadedWorld = null;
            string worldError = null;
            yield return _modelLoader.LoadModel(
                request.WorldModelBytes,
                request.CacheNamespace,
                request.FullToolId + "_world",
                worldRoot.transform,
                delegate(GameObject root, string error)
                {
                    loadedWorld = root;
                    worldError = error;
                });

            if (loadedWorld == null)
            {
                _logger.LogWarning("[ToolImportApi] World model load failed for " + request.FullToolId + ": " + worldError);
                loadedWorld = CreateFallbackVisual(worldRoot.transform, "WorldFallback");
            }

            VerboseLog(
                "World model load result for " + request.FullToolId +
                ". root=" + loadedWorld.name +
                ", meshRenderers=" + worldRoot.GetComponentsInChildren<MeshRenderer>(true).Length +
                ", skinnedRenderers=" + worldRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length);

            VerboseLog("Loading view model for " + request.FullToolId + "...");
            GameObject loadedView = null;
            string viewError = null;
            yield return _modelLoader.LoadModel(
                request.ViewModelBytes,
                request.CacheNamespace,
                request.FullToolId + "_view",
                viewRoot.transform,
                delegate(GameObject root, string error)
                {
                    loadedView = root;
                    viewError = error;
                });

            if (loadedView == null)
            {
                _logger.LogWarning("[ToolImportApi] View model load failed for " + request.FullToolId + ": " + viewError);
                loadedView = CreateFallbackVisual(viewRoot.transform, "ViewFallback");
            }

            ValidateAndFixViewModel(
                request.FullToolId,
                viewRoot.transform,
                request.Request != null ? request.Request.ArmPose : null,
                request.Request != null ? request.Request.ViewModelTransform : null);
            StripViewModelPhysics(viewRoot.transform);

            int viewModelLayer = LayerMask.NameToLayer("ViewModel");
            if (viewModelLayer >= 0)
            {
                SetLayerRecursively(viewRoot.transform, viewModelLayer);
            }

            if (!FitBoxColliderToRenderBounds(collider, worldRoot.transform))
            {
                collider.size = new Vector3(0.35f, 0.18f, 0.55f);
                collider.center = new Vector3(0f, 0.08f, 0f);
            }

            RegisteredTool registeredTool = new RegisteredTool
            {
                FullToolId = request.FullToolId,
                Request = request.Request,
                SourceAssembly = request.SourceAssembly,
                SavableId = request.SavableId,
                TemplateObject = template,
                TemplateTool = tool,
                Icon = request.Icon
            };

            EnsureSaveRegistration(registeredTool);
            VerboseLog("CreateRegisteredTool complete for " + request.FullToolId + ". SavableObjectID=" + request.SavableId);
            result.Success = true;
            result.Tool = registeredTool;
        }

        public bool TrySpawnToolToInventory(RegisteredTool tool, out string message)
        {
            message = null;
            if (tool == null || tool.TemplateObject == null)
            {
                message = "Tool template is missing.";
                return false;
            }

            EnsureSaveRegistration(tool);
            PlayerInventory inventory = UnityEngine.Object.FindFirstObjectByType<PlayerInventory>();
            if (inventory == null)
            {
                message = "PlayerInventory not found in active scene.";
                return false;
            }

            GameObject instance = UnityEngine.Object.Instantiate(tool.TemplateObject);
            instance.name = tool.Request.ToolId + ".Instance";
            instance.SetActive(false);

            RuntimeImportedTool runtimeTool = instance.GetComponent<RuntimeImportedTool>();
            if (runtimeTool == null)
            {
                UnityEngine.Object.Destroy(instance);
                message = "Spawned template is missing RuntimeImportedTool.";
                return false;
            }

            RebindModelReferences(runtimeTool, instance.transform);
            runtimeTool.SavableObjectID = (SavableObjectID)tool.SavableId;
            runtimeTool.InventoryIcon = tool.Icon;
            runtimeTool.ProgrammerInventoryIcon = tool.Icon;
            runtimeTool.Name = tool.Request.DisplayName;
            runtimeTool.Description = string.IsNullOrWhiteSpace(tool.Request.Description)
                ? "Imported mod tool."
                : tool.Request.Description;

            if (!inventory.TryAddToInventory(runtimeTool))
            {
                UnityEngine.Object.Destroy(instance);
                message = "Inventory rejected tool instance.";
                return false;
            }

            message = "Spawned and added '" + tool.FullToolId + "' to inventory.";
            return true;
        }

        public void EnsureSaveRegistration(RegisteredTool tool)
        {
            if (tool == null || tool.TemplateObject == null)
            {
                return;
            }

            SavingLoadingManager slm = UnityEngine.Object.FindFirstObjectByType<SavingLoadingManager>();
            if (slm == null)
            {
                return;
            }

            if (!slm.AllSavableObjectPrefabs.Contains(tool.TemplateObject))
            {
                slm.AllSavableObjectPrefabs.Add(tool.TemplateObject);
            }

            if (SaveLookupField == null)
            {
                return;
            }

            Dictionary<SavableObjectID, GameObject> lookup = SaveLookupField.GetValue(slm) as Dictionary<SavableObjectID, GameObject>;
            if (lookup != null)
            {
                lookup[(SavableObjectID)tool.SavableId] = tool.TemplateObject;
            }
        }

        public bool IsSavableIdInUse(int savableId)
        {
            SavingLoadingManager slm = UnityEngine.Object.FindFirstObjectByType<SavingLoadingManager>();
            if (slm == null)
            {
                return false;
            }

            if (SaveLookupField != null)
            {
                Dictionary<SavableObjectID, GameObject> lookup = SaveLookupField.GetValue(slm) as Dictionary<SavableObjectID, GameObject>;
                if (lookup != null && lookup.ContainsKey((SavableObjectID)savableId))
                {
                    return true;
                }
            }

            List<GameObject> prefabs = slm.AllSavableObjectPrefabs;
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (prefabs[i] == null)
                {
                    continue;
                }

                ISaveLoadableObject saveable = prefabs[i].GetComponent<ISaveLoadableObject>();
                if (saveable != null && (int)saveable.GetSavableObjectID() == savableId)
                {
                    return true;
                }
            }

            return false;
        }

        private void ValidateAndFixViewModel(
            string fullToolId,
            Transform viewModelRoot,
            ArmPoseRequest armPose,
            ToolViewTransformRequest viewModelTransform)
        {
            if (viewModelRoot == null)
            {
                _logger.LogWarning("[ToolImportApi] ViewModel root is null for " + fullToolId + ".");
                return;
            }

            int meshRendererCount = viewModelRoot.GetComponentsInChildren<MeshRenderer>(true).Length;
            int skinnedRendererCount = viewModelRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length;
            int armRendererCount = CountArmVariantRenderers(viewModelRoot);
            Transform fpsArmsRoot = FindTransformByName(viewModelRoot, "FP_Arms_Unarmed");

            VerboseLog(
                "ViewModel diagnostics for " + fullToolId +
                ": meshRenderers=" + meshRendererCount +
                ", skinnedRenderers=" + skinnedRendererCount +
                ", armRenderers=" + armRendererCount +
                ", hasFP_Arms_Unarmed=" + (fpsArmsRoot != null));

            DumpViewHierarchy(fullToolId, viewModelRoot);

            if (_options.InjectVanillaArmsIfMissing)
            {
                if (armRendererCount > 0)
                {
                    VerboseLog("Arm fallback not needed for " + fullToolId + " (arm renderers already present).");
                }
                else if (TryInjectVanillaWrenchArms(viewModelRoot, fullToolId, armPose, out string injectInfo))
                {
                    int armRenderersAfter = CountArmVariantRenderers(viewModelRoot);
                    VerboseLog(
                        "Arm fallback injected for " + fullToolId +
                        ". armRenderersBefore=" + armRendererCount +
                        ", armRenderersAfter=" + armRenderersAfter +
                        ". " + injectInfo);
                }
                else
                {
                    _logger.LogWarning("[ToolImportApi] Arm fallback failed for " + fullToolId + ": " + injectInfo);
                }
            }
            else
            {
                VerboseLog("Arm fallback disabled for " + fullToolId + " (InjectVanillaArmsIfMissing=false).");
            }

            ApplyRequestedArmPose(fullToolId, viewModelRoot, armPose);
            ApplyRequestedViewModelTransform(fullToolId, viewModelRoot, viewModelTransform);
        }

        private bool TryInjectVanillaWrenchArms(Transform targetViewModelRoot, string fullToolId, ArmPoseRequest armPose, out string info)
        {
            info = string.Empty;
            if (targetViewModelRoot == null)
            {
                info = "Target viewmodel root is null.";
                return false;
            }

            Transform sourceFpsArmsRoot = GetVanillaWrenchArmsSourceRoot();
            if (sourceFpsArmsRoot == null)
            {
                info = "Could not find vanilla Wrench FP_Arms_Unarmed source.";
                return false;
            }

            Transform targetFpsArmsRoot = FindTransformByName(targetViewModelRoot, "FP_Arms_Unarmed");
            Transform replacementParent = targetFpsArmsRoot != null ? targetFpsArmsRoot.parent : targetViewModelRoot;
            int replacementSiblingIndex = targetFpsArmsRoot != null ? targetFpsArmsRoot.GetSiblingIndex() : -1;

            GameObject replacement = UnityEngine.Object.Instantiate(sourceFpsArmsRoot.gameObject, replacementParent, false);
            replacement.name = "FP_Arms_Unarmed";
            replacement.transform.localPosition = sourceFpsArmsRoot.localPosition;
            replacement.transform.localRotation = sourceFpsArmsRoot.localRotation;
            replacement.transform.localScale = sourceFpsArmsRoot.localScale;
            if (replacementSiblingIndex >= 0)
            {
                replacement.transform.SetSiblingIndex(replacementSiblingIndex);
            }

            if (targetFpsArmsRoot != null)
            {
                UnityEngine.Object.Destroy(targetFpsArmsRoot.gameObject);
            }

            replacement.SetActive(true);
            RemapExternalBonesToLocalRig(replacement.transform);
            bool pairSelected = ActivatePreferredArmPairWithLog(replacement.transform, armPose != null ? armPose.PreferredArmPair : null);
            SetAllRenderersEnabled(replacement.transform, true);
            int armRenderers = CountArmVariantRenderers(targetViewModelRoot);
            int externalBoneRefs = CountExternalBoneReferences(replacement.transform);

            info =
                "replacedRoot=true" +
                ", preferredPairSelected=" + pairSelected +
                ", armRenderersNow=" + armRenderers +
                ", replacementParent=" + GetTransformPath(replacement.transform.parent) +
                ", externalBoneRefs=" + externalBoneRefs;
            return true;
        }

        private static Transform GetVanillaWrenchArmsSourceRoot()
        {
            SavingLoadingManager slm = Singleton<SavingLoadingManager>.Instance ?? UnityEngine.Object.FindFirstObjectByType<SavingLoadingManager>();
            if (slm == null)
            {
                return null;
            }

            GameObject wrenchPrefab = slm.GetPrefab(SavableObjectID.WrenchTool);
            if (wrenchPrefab == null)
            {
                List<GameObject> prefabs = slm.AllSavableObjectPrefabs;
                for (int i = 0; i < prefabs.Count; i++)
                {
                    GameObject prefab = prefabs[i];
                    if (prefab == null)
                    {
                        continue;
                    }

                    if (prefab.GetComponent("ToolSupportsWrench") != null)
                    {
                        wrenchPrefab = prefab;
                        break;
                    }
                }
            }

            if (wrenchPrefab == null)
            {
                return null;
            }

            BaseHeldTool tool = wrenchPrefab.GetComponent<BaseHeldTool>();
            if (tool == null || tool.ViewModel == null)
            {
                return null;
            }

            return FindTransformByName(tool.ViewModel.transform, "FP_Arms_Unarmed");
        }

        private bool ActivatePreferredArmPairWithLog(Transform fpsArmsRoot, string preferredPair)
        {
            if (fpsArmsRoot == null)
            {
                return false;
            }

            if (TryParseArmPair(preferredPair, out string preferredLeft, out string preferredRight))
            {
                Transform leftPreferred = fpsArmsRoot.Find(preferredLeft);
                Transform rightPreferred = fpsArmsRoot.Find(preferredRight);
                if (leftPreferred != null && rightPreferred != null)
                {
                    SetArmVariantsActive(fpsArmsRoot, false);
                    leftPreferred.gameObject.SetActive(true);
                    rightPreferred.gameObject.SetActive(true);
                    SetAllRenderersEnabled(leftPreferred, true);
                    SetAllRenderersEnabled(rightPreferred, true);
                    VerboseLog("Activated requested arm pair: " + preferredLeft + " + " + preferredRight);
                    return true;
                }

                VerboseLog(
                    "Requested arm pair was not found on rig: " +
                    preferredLeft + " + " + preferredRight + ".");
            }

            for (int i = 0; i < PreferredArmPairs.GetLength(0); i++)
            {
                string leftName = PreferredArmPairs[i, 0];
                string rightName = PreferredArmPairs[i, 1];

                Transform left = fpsArmsRoot.Find(leftName);
                Transform right = fpsArmsRoot.Find(rightName);
                if (left == null || right == null)
                {
                    continue;
                }

                SetArmVariantsActive(fpsArmsRoot, false);
                left.gameObject.SetActive(true);
                right.gameObject.SetActive(true);
                SetAllRenderersEnabled(left, true);
                SetAllRenderersEnabled(right, true);
                VerboseLog("Activated arm pair: " + leftName + " + " + rightName);
                return true;
            }

            return false;
        }

        private void ApplyRequestedArmPose(string fullToolId, Transform viewModelRoot, ArmPoseRequest armPose)
        {
            if (armPose == null)
            {
                return;
            }

            Transform fpsArmsRoot = FindTransformByName(viewModelRoot, "FP_Arms_Unarmed");
            if (fpsArmsRoot == null)
            {
                _logger.LogWarning("[ToolImportApi] Arm pose ignored for " + fullToolId + ": FP_Arms_Unarmed not found.");
                return;
            }

            bool pairSelected = ActivatePreferredArmPairWithLog(fpsArmsRoot, armPose.PreferredArmPair);
            int overrideCount = armPose.BoneOverrides != null ? armPose.BoneOverrides.Count : 0;
            if (overrideCount <= 0)
            {
                VerboseLog(
                    "Arm pose request for " + fullToolId +
                    " has no bone overrides. preferredPairApplied=" + pairSelected);
                return;
            }

            int applied = 0;
            int missing = 0;
            int skipped = 0;
            for (int i = 0; i < armPose.BoneOverrides.Count; i++)
            {
                ArmBonePose overridePose = armPose.BoneOverrides[i];
                if (overridePose == null)
                {
                    skipped++;
                    continue;
                }

                bool hasAnyChange =
                    overridePose.LocalPosition.HasValue ||
                    overridePose.LocalEulerAngles.HasValue ||
                    overridePose.LocalScale.HasValue;
                if (!hasAnyChange)
                {
                    skipped++;
                    continue;
                }

                Transform targetBone = ResolveArmBoneTransform(fpsArmsRoot, overridePose);
                if (targetBone == null)
                {
                    missing++;
                    continue;
                }

                if (overridePose.LocalPosition.HasValue)
                {
                    targetBone.localPosition = overridePose.LocalPosition.Value;
                }

                if (overridePose.LocalEulerAngles.HasValue)
                {
                    targetBone.localEulerAngles = overridePose.LocalEulerAngles.Value;
                }

                if (overridePose.LocalScale.HasValue)
                {
                    targetBone.localScale = overridePose.LocalScale.Value;
                }

                applied++;
            }

            _logger.LogInfo(
                "[ToolImportApi] Applied arm pose for " + fullToolId +
                ". overridesRequested=" + overrideCount +
                ", applied=" + applied +
                ", missing=" + missing +
                ", skipped=" + skipped +
                ", preferredPairApplied=" + pairSelected);
        }

        private void ApplyRequestedViewModelTransform(
            string fullToolId,
            Transform viewModelRoot,
            ToolViewTransformRequest transformRequest)
        {
            if (viewModelRoot == null || transformRequest == null)
            {
                return;
            }

            bool hasAnyChange =
                transformRequest.LocalPosition.HasValue ||
                transformRequest.LocalEulerAngles.HasValue ||
                transformRequest.LocalScale.HasValue;
            if (!hasAnyChange)
            {
                return;
            }

            Transform target = ResolveViewModelTransformTarget(viewModelRoot, transformRequest.TransformPath);
            if (target == null)
            {
                _logger.LogWarning(
                    "[ToolImportApi] ViewModel transform override ignored for " + fullToolId +
                    ": target transform not found. path=" +
                    (string.IsNullOrWhiteSpace(transformRequest.TransformPath) ? "<auto>" : transformRequest.TransformPath));
                return;
            }

            if (transformRequest.LocalPosition.HasValue)
            {
                target.localPosition = transformRequest.LocalPosition.Value;
            }

            if (transformRequest.LocalEulerAngles.HasValue)
            {
                target.localEulerAngles = transformRequest.LocalEulerAngles.Value;
            }

            if (transformRequest.LocalScale.HasValue)
            {
                target.localScale = transformRequest.LocalScale.Value;
            }

            _logger.LogInfo(
                "[ToolImportApi] Applied viewmodel transform for " + fullToolId +
                ". target=" + GetTransformPath(target) +
                ", path=" + (string.IsNullOrWhiteSpace(transformRequest.TransformPath) ? "<auto>" : transformRequest.TransformPath));
        }

        private static Transform ResolveViewModelTransformTarget(Transform viewModelRoot, string transformPath)
        {
            if (viewModelRoot == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(transformPath))
            {
                string path = transformPath.Trim();
                if (path == "." || path == "/" || path == "<root>" || path == "<ViewModel root>")
                {
                    return viewModelRoot;
                }

                Transform fromPath = viewModelRoot.Find(path);
                if (fromPath != null)
                {
                    return fromPath;
                }
            }

            return FindDefaultToolTransformTarget(viewModelRoot);
        }

        private static Transform FindDefaultToolTransformTarget(Transform viewModelRoot)
        {
            if (viewModelRoot == null)
            {
                return null;
            }

            Renderer[] renderers = viewModelRoot.GetComponentsInChildren<Renderer>(true);
            Transform candidate = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || IsUnderArmVariant(renderer.transform))
                {
                    continue;
                }

                candidate = candidate == null
                    ? renderer.transform
                    : FindCommonAncestorWithin(viewModelRoot, candidate, renderer.transform);
            }

            if (candidate != null)
            {
                return candidate;
            }

            return viewModelRoot.childCount > 0 ? viewModelRoot.GetChild(0) : viewModelRoot;
        }

        private static Transform FindCommonAncestorWithin(Transform root, Transform a, Transform b)
        {
            if (root == null || a == null || b == null)
            {
                return null;
            }

            HashSet<Transform> lineage = new HashSet<Transform>();
            Transform cursor = a;
            while (cursor != null && cursor.IsChildOf(root))
            {
                lineage.Add(cursor);
                if (cursor == root)
                {
                    break;
                }

                cursor = cursor.parent;
            }

            cursor = b;
            while (cursor != null && cursor.IsChildOf(root))
            {
                if (lineage.Contains(cursor))
                {
                    return cursor;
                }

                if (cursor == root)
                {
                    break;
                }

                cursor = cursor.parent;
            }

            return root;
        }

        private static Transform ResolveArmBoneTransform(Transform fpsArmsRoot, ArmBonePose overridePose)
        {
            if (fpsArmsRoot == null || overridePose == null)
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(overridePose.BonePath))
            {
                Transform fromPath = fpsArmsRoot.Find(overridePose.BonePath.Trim());
                if (fromPath != null)
                {
                    return fromPath;
                }
            }

            if (string.IsNullOrWhiteSpace(overridePose.BoneName))
            {
                return null;
            }

            return FindTransformByName(fpsArmsRoot, overridePose.BoneName.Trim());
        }

        private static bool TryParseArmPair(string pairText, out string leftName, out string rightName)
        {
            leftName = null;
            rightName = null;
            if (string.IsNullOrWhiteSpace(pairText))
            {
                return false;
            }

            string[] parts = pairText.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                return false;
            }

            leftName = parts[0].Trim();
            rightName = parts[1].Trim();
            return !string.IsNullOrWhiteSpace(leftName) && !string.IsNullOrWhiteSpace(rightName);
        }

        private static void SetArmVariantsActive(Transform fpsArmsRoot, bool isActive)
        {
            if (fpsArmsRoot == null)
            {
                return;
            }

            for (int i = 0; i < fpsArmsRoot.childCount; i++)
            {
                Transform child = fpsArmsRoot.GetChild(i);
                if (child == null || !child.name.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                child.gameObject.SetActive(isActive);
                SetAllRenderersEnabled(child, isActive);
            }
        }

        private static void SetAllRenderersEnabled(Transform root, bool enabled)
        {
            if (root == null)
            {
                return;
            }

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                if (meshRenderers[i] != null)
                {
                    meshRenderers[i].enabled = enabled;
                }
            }

            SkinnedMeshRenderer[] skinnedMeshRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedMeshRenderers.Length; i++)
            {
                SkinnedMeshRenderer skinned = skinnedMeshRenderers[i];
                if (skinned != null)
                {
                    skinned.enabled = enabled;
                    skinned.updateWhenOffscreen = true;
                }
            }
        }

        private static int CountArmVariantRenderers(Transform root)
        {
            if (root == null)
            {
                return 0;
            }

            int count = 0;

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                if (meshRenderers[i] != null && IsUnderArmVariant(meshRenderers[i].transform))
                {
                    count++;
                }
            }

            SkinnedMeshRenderer[] skinned = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i] != null && IsUnderArmVariant(skinned[i].transform))
                {
                    count++;
                }
            }

            return count;
        }

        private static bool IsUnderArmVariant(Transform t)
        {
            Transform cursor = t;
            while (cursor != null)
            {
                if (cursor.name.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                cursor = cursor.parent;
            }

            return false;
        }

        private static int CountExternalBoneReferences(Transform rigRoot)
        {
            if (rigRoot == null)
            {
                return 0;
            }

            int count = 0;
            SkinnedMeshRenderer[] skinned = rigRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer smr = skinned[i];
                if (smr == null)
                {
                    continue;
                }

                Transform[] bones = smr.bones;
                if (bones != null)
                {
                    for (int b = 0; b < bones.Length; b++)
                    {
                        Transform bone = bones[b];
                        if (bone == null)
                        {
                            continue;
                        }

                        if (!bone.IsChildOf(rigRoot))
                        {
                            count++;
                        }
                    }
                }

                if (smr.rootBone != null && !smr.rootBone.IsChildOf(rigRoot))
                {
                    count++;
                }
            }

            return count;
        }

        private static void RemapExternalBonesToLocalRig(Transform rigRoot)
        {
            if (rigRoot == null)
            {
                return;
            }

            Dictionary<string, Transform> byName = new Dictionary<string, Transform>(256);
            BuildLocalRigNameMap(rigRoot, byName);

            SkinnedMeshRenderer[] skinned = rigRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                SkinnedMeshRenderer smr = skinned[i];
                if (smr == null)
                {
                    continue;
                }

                Transform[] bones = smr.bones;
                if (bones != null && bones.Length > 0)
                {
                    bool changed = false;
                    Transform[] remapped = new Transform[bones.Length];
                    for (int b = 0; b < bones.Length; b++)
                    {
                        Transform src = bones[b];
                        Transform dst = src;
                        if (src != null && !src.IsChildOf(rigRoot) && byName.TryGetValue(src.name, out Transform localBone))
                        {
                            dst = localBone;
                            changed = true;
                        }

                        remapped[b] = dst;
                    }

                    if (changed)
                    {
                        smr.bones = remapped;
                    }
                }

                Transform rootBone = smr.rootBone;
                if (rootBone != null && !rootBone.IsChildOf(rigRoot) && byName.TryGetValue(rootBone.name, out Transform localRoot))
                {
                    smr.rootBone = localRoot;
                }
            }
        }

        private static void BuildLocalRigNameMap(Transform root, Dictionary<string, Transform> map)
        {
            if (root == null || map == null)
            {
                return;
            }

            if (!map.ContainsKey(root.name))
            {
                map.Add(root.name, root);
            }

            for (int i = 0; i < root.childCount; i++)
            {
                BuildLocalRigNameMap(root.GetChild(i), map);
            }
        }

        private static Transform FindTransformByName(Transform root, string targetName)
        {
            if (root == null || string.IsNullOrEmpty(targetName))
            {
                return null;
            }

            if (string.Equals(root.name, targetName, StringComparison.Ordinal))
            {
                return root;
            }

            for (int i = 0; i < root.childCount; i++)
            {
                Transform found = FindTransformByName(root.GetChild(i), targetName);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }

        private static string GetTransformPath(Transform t)
        {
            if (t == null)
            {
                return "<null>";
            }

            List<string> names = new List<string>(8);
            Transform cursor = t;
            while (cursor != null)
            {
                names.Add(cursor.name);
                cursor = cursor.parent;
            }

            names.Reverse();
            return string.Join("/", names.ToArray());
        }

        private void DumpViewHierarchy(string fullToolId, Transform viewModelRoot)
        {
            if (!_options.VerboseViewModelLogs || viewModelRoot == null)
            {
                return;
            }

            _logger.LogInfo("[ToolImportApi] View hierarchy dump for " + fullToolId + " (2 levels):");
            _logger.LogInfo("[ToolImportApi]   root: " + viewModelRoot.name + " (children=" + viewModelRoot.childCount + ")");

            for (int i = 0; i < viewModelRoot.childCount; i++)
            {
                Transform child = viewModelRoot.GetChild(i);
                if (child == null)
                {
                    continue;
                }

                _logger.LogInfo(
                    "[ToolImportApi]   - " + child.name +
                    " (children=" + child.childCount +
                    ", meshR=" + child.GetComponentsInChildren<MeshRenderer>(true).Length +
                    ", skinnedR=" + child.GetComponentsInChildren<SkinnedMeshRenderer>(true).Length + ")");

                int grandChildLimit = Mathf.Min(child.childCount, 20);
                for (int j = 0; j < grandChildLimit; j++)
                {
                    Transform grandChild = child.GetChild(j);
                    if (grandChild != null)
                    {
                        _logger.LogInfo("[ToolImportApi]     * " + grandChild.name + " (children=" + grandChild.childCount + ")");
                    }
                }

                if (child.childCount > grandChildLimit)
                {
                    _logger.LogInfo("[ToolImportApi]     * ... (" + (child.childCount - grandChildLimit) + " more)");
                }
            }
        }

        private void ConfigureInteractions(BaseHeldTool tool)
        {
            if (tool == null || InteractionsField == null)
            {
                return;
            }

            List<Interaction> interactions = new List<Interaction>(2)
            {
                CreateInteraction("Take", "Pick up this tool"),
                CreateInteraction("Destroy", "Destroy this tool")
            };

            InteractionsField.SetValue(tool, interactions);
        }

        private static Interaction CreateInteraction(string name, string description)
        {
            Interaction interaction = ScriptableObject.CreateInstance<Interaction>();
            interaction.Name = name;
            interaction.Description = description;
            interaction.hideFlags = HideFlags.HideAndDontSave;
            return interaction;
        }

        private static void RebindModelReferences(RuntimeImportedTool tool, Transform instanceRoot)
        {
            if (tool == null || instanceRoot == null)
            {
                return;
            }

            Transform world = instanceRoot.Find("WorldModel");
            Transform view = instanceRoot.Find("ViewModel");
            if (world != null)
            {
                tool.WorldModel = world.gameObject;
            }

            if (view != null)
            {
                tool.ViewModel = view.gameObject;
            }
        }

        private static GameObject CreateFallbackVisual(Transform parent, string name)
        {
            GameObject primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localScale = new Vector3(0.2f, 0.1f, 0.4f);

            Collider collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            return primitive;
        }

        private static bool FitBoxColliderToRenderBounds(BoxCollider collider, Transform root)
        {
            if (collider == null || root == null)
            {
                return false;
            }

            Renderer[] renderers = root.GetComponentsInChildren<Renderer>(true);
            if (renderers == null || renderers.Length == 0)
            {
                return false;
            }

            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++)
            {
                if (renderers[i] != null)
                {
                    bounds.Encapsulate(renderers[i].bounds);
                }
            }

            collider.center = collider.transform.InverseTransformPoint(bounds.center);
            collider.size = new Vector3(
                Mathf.Max(bounds.size.x, 0.02f),
                Mathf.Max(bounds.size.y, 0.02f),
                Mathf.Max(bounds.size.z, 0.02f));
            return true;
        }

        private static void SetLayerRecursively(Transform root, int layer)
        {
            if (root == null)
            {
                return;
            }

            root.gameObject.layer = layer;
            for (int i = 0; i < root.childCount; i++)
            {
                SetLayerRecursively(root.GetChild(i), layer);
            }
        }

        private void StripViewModelPhysics(Transform viewModelRoot)
        {
            if (viewModelRoot == null)
            {
                return;
            }

            Collider[] colliders = viewModelRoot.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    UnityEngine.Object.Destroy(colliders[i]);
                }
            }

            Rigidbody[] rigidbodies = viewModelRoot.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < rigidbodies.Length; i++)
            {
                if (rigidbodies[i] != null)
                {
                    UnityEngine.Object.Destroy(rigidbodies[i]);
                }
            }

            VerboseLog(
                "StripViewModelPhysics complete. collidersRemoved=" + colliders.Length +
                ", rigidbodiesRemoved=" + rigidbodies.Length);
        }

        private void VerboseLog(string message)
        {
            if (_options.VerboseViewModelLogs)
            {
                _logger.LogInfo("[ToolImportApi] " + message);
            }
        }
    }
}
