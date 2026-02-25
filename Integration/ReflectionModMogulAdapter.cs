using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Logging;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;
using MineMogul.ToolImportApi.Loaders;
using UnityEngine;

namespace MineMogul.ToolImportApi.Integration
{
    internal sealed class ReflectionModMogulAdapter : IModMogulAdapter
    {
        private readonly ManualLogSource _logger;
        private readonly string _cacheDirectory;
        private readonly Type _itemizerType;
        private readonly Type _itemSpecType;
        private readonly MethodInfo _registerItemWithIconMethod;
        private readonly MethodInfo _registerItemMethod;
        private readonly Dictionary<string, BuildingObject> _pickupPrefabs =
            new Dictionary<string, BuildingObject>(StringComparer.OrdinalIgnoreCase);

        public ReflectionModMogulAdapter(ManualLogSource logger, string cacheDirectory)
        {
            _logger = logger;
            _cacheDirectory = cacheDirectory;
            Directory.CreateDirectory(_cacheDirectory);

            _itemizerType = Type.GetType("ModMogul.Itemizer, ModMogul");
            if (_itemizerType == null)
            {
                return;
            }

            _itemSpecType = _itemizerType.GetNestedType("ItemSpec", BindingFlags.Public);
            if (_itemSpecType == null)
            {
                return;
            }

            MethodInfo[] methods = _itemizerType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!string.Equals(method.Name, "RegisterItem", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 2 &&
                    parameters[0].ParameterType == _itemSpecType &&
                    parameters[1].ParameterType == typeof(string))
                {
                    _registerItemWithIconMethod = method;
                    continue;
                }

                if (parameters.Length == 1 &&
                    parameters[0].ParameterType == _itemSpecType)
                {
                    _registerItemMethod = method;
                }
            }
        }

        public bool IsAvailable => _itemizerType != null && _itemSpecType != null &&
                                   (_registerItemWithIconMethod != null || _registerItemMethod != null);

        public bool TryRegisterTool(RegisteredTool tool, ToolRegistrationRequest request, out string message)
        {
            message = "ModMogul unavailable.";
            if (!IsAvailable)
            {
                return false;
            }

            if (tool == null || request == null)
            {
                message = "Invalid tool registration data.";
                return false;
            }

            int blockId = tool.SavableId;
            int price = 500;
            string category = "Tools";
            bool lockedByDefault = false;
            int maxStack = 1;
            string qAction = "Rotate";

            if (request.ShopMetadata != null)
            {
                if (request.ShopMetadata.ShopBlockId.HasValue)
                {
                    blockId = request.ShopMetadata.ShopBlockId.Value;
                }

                price = request.ShopMetadata.Price;
                category = string.IsNullOrWhiteSpace(request.ShopMetadata.Category) ? "Tools" : request.ShopMetadata.Category;
                lockedByDefault = request.ShopMetadata.IsLockedByDefault;
                maxStack = request.ShopMetadata.MaxStackSize > 0 ? request.ShopMetadata.MaxStackSize : 1;
                qAction = string.IsNullOrWhiteSpace(request.ShopMetadata.QAction) ? "Rotate" : request.ShopMetadata.QAction;
            }

            string internalName = request.ModId + "_" + request.ToolId;
            string description = string.IsNullOrWhiteSpace(request.Description)
                ? "Imported tool pickup."
                : request.Description;

            object spec = Activator.CreateInstance(_itemSpecType);
            SetField(spec, "BlockID", blockId);
            SetField(spec, "InternalName", internalName);
            SetField(spec, "DisplayName", request.DisplayName);
            SetField(spec, "Description", description);
            SetField(spec, "Price", price);
            SetField(spec, "ShopCategory", category);
            SetField(spec, "MaxStackSize", maxStack);
            SetField(spec, "IsLockedByDefault", lockedByDefault);
            SetField(spec, "QFunction", qAction);

            string namespacedCacheDirectory = GetNamespacedCacheDirectory(request.CacheNamespace);
            string iconPath = Path.Combine(namespacedCacheDirectory, SanitizeFileName(tool.FullToolId) + "_icon.png");
            if (!EmbeddedResourceLoader.TryExtractToFile(tool.SourceAssembly, request.IconResourceName, iconPath, out string iconError))
            {
                message = "Could not extract icon for ModMogul registration: " + iconError;
                return false;
            }

            BuildingObject pickupPrefab = InvokeRegisterItem(spec, iconPath);
            if (pickupPrefab == null)
            {
                message = "Itemizer.RegisterItem returned null.";
                return false;
            }

            ConfigurePickupPrefab(pickupPrefab, tool);
            _pickupPrefabs[tool.FullToolId] = pickupPrefab;
            message = "Registered with ModMogul Itemizer as block id " + blockId + ".";
            return true;
        }

        public bool TryAddToShop(RegisteredTool tool, ToolRegistrationRequest request, out string message)
        {
            if (!IsAvailable)
            {
                message = "ModMogul unavailable.";
                return false;
            }

            if (tool == null)
            {
                message = "Tool is null.";
                return false;
            }

            if (_pickupPrefabs.ContainsKey(tool.FullToolId))
            {
                message = "Already added by Itemizer.RegisterItem.";
                return true;
            }

            message = "Tool has not been registered with Itemizer yet.";
            return false;
        }

        public bool TrySpawnTool(RegisteredTool tool, out string message)
        {
            message = "ModMogul tool pickup prefab is not available.";
            if (tool == null)
            {
                return false;
            }

            if (!_pickupPrefabs.TryGetValue(tool.FullToolId, out BuildingObject pickupPrefab) || pickupPrefab == null)
            {
                return false;
            }

            PlayerController player = UnityEngine.Object.FindFirstObjectByType<PlayerController>();
            if (player == null || player.PlayerCamera == null)
            {
                message = "PlayerController/PlayerCamera not found.";
                return false;
            }

            Transform cameraTransform = player.PlayerCamera.transform;
            Vector3 spawnPosition = cameraTransform.position + cameraTransform.forward * 1.5f;
            Quaternion rotation = cameraTransform.rotation;
            UnityEngine.Object.Instantiate(pickupPrefab, spawnPosition, rotation);
            message = "Spawned ModMogul pickup for " + tool.FullToolId + ".";
            return true;
        }

        private BuildingObject InvokeRegisterItem(object spec, string iconPath)
        {
            try
            {
                if (_registerItemWithIconMethod != null)
                {
                    object result = _registerItemWithIconMethod.Invoke(null, new[] { spec, iconPath });
                    return result as BuildingObject;
                }

                if (_registerItemMethod != null)
                {
                    object result = _registerItemMethod.Invoke(null, new[] { spec });
                    return result as BuildingObject;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("[ToolImportApi] Itemizer.RegisterItem invocation failed: " + ex);
            }

            return null;
        }

        private static void SetField(object boxedStruct, string fieldName, object value)
        {
            if (boxedStruct == null)
            {
                return;
            }

            FieldInfo field = boxedStruct.GetType().GetField(fieldName, BindingFlags.Public | BindingFlags.Instance);
            if (field != null)
            {
                field.SetValue(boxedStruct, value);
            }
        }

        private void ConfigurePickupPrefab(BuildingObject pickupPrefab, RegisteredTool tool)
        {
            if (pickupPrefab == null || tool == null || tool.TemplateTool == null)
            {
                return;
            }

            HideDefaultTemplateCube(pickupPrefab);
            CloneWorldVisual(tool.TemplateTool.WorldModel, pickupPrefab.transform);

            Rigidbody rb = pickupPrefab.GetComponent<Rigidbody>();
            if (rb == null)
            {
                rb = pickupPrefab.gameObject.AddComponent<Rigidbody>();
            }

            rb.isKinematic = false;
            rb.useGravity = true;
            rb.mass = 2f;
            rb.linearDamping = 1f;
            rb.angularDamping = 1f;
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

            Collider[] colliders = pickupPrefab.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                UnityEngine.Object.Destroy(colliders[i]);
            }

            BoxCollider box = pickupPrefab.gameObject.AddComponent<BoxCollider>();
            if (!TryFitCollider(box, pickupPrefab.transform))
            {
                box.size = new Vector3(0.35f, 0.18f, 0.55f);
                box.center = new Vector3(0f, 0.08f, 0f);
            }

            pickupPrefab.gameObject.tag = "Grabbable";
            pickupPrefab.gameObject.layer = LayerMask.NameToLayer("Default");
            pickupPrefab.BuildModePlacementOffset = new Vector3(0f, 0f, -0.3f);

            ToolImportPickupMarker.RegisterToolMapping(pickupPrefab, tool.FullToolId);
        }

        private static void CloneWorldVisual(GameObject worldModel, Transform destination)
        {
            if (worldModel == null || destination == null)
            {
                return;
            }

            for (int i = destination.childCount - 1; i >= 0; i--)
            {
                Transform child = destination.GetChild(i);
                if (child != null && child.name == "__ToolImportWorldVisual")
                {
                    UnityEngine.Object.Destroy(child.gameObject);
                }
            }

            Transform sourceRoot = worldModel.transform;
            for (int i = 0; i < sourceRoot.childCount; i++)
            {
                Transform sourceChild = sourceRoot.GetChild(i);
                if (sourceChild == null)
                {
                    continue;
                }

                GameObject clone = UnityEngine.Object.Instantiate(sourceChild.gameObject, destination, false);
                clone.name = "__ToolImportWorldVisual";
                clone.transform.localPosition = sourceChild.localPosition;
                clone.transform.localRotation = sourceChild.localRotation;
                clone.transform.localScale = sourceChild.localScale;
                clone.SetActive(true);
            }
        }

        private static bool TryFitCollider(BoxCollider box, Transform renderRoot)
        {
            Renderer[] renderers = renderRoot.GetComponentsInChildren<Renderer>(true);
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

            box.center = box.transform.InverseTransformPoint(bounds.center);
            box.size = new Vector3(
                Mathf.Max(0.02f, bounds.size.x),
                Mathf.Max(0.02f, bounds.size.y),
                Mathf.Max(0.02f, bounds.size.z));
            return true;
        }

        private static void HideDefaultTemplateCube(BuildingObject prefab)
        {
            if (prefab == null)
            {
                return;
            }

            MeshRenderer rootRenderer = prefab.GetComponent<MeshRenderer>();
            if (rootRenderer != null)
            {
                rootRenderer.enabled = false;
            }

            MeshRenderer[] renderers = prefab.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (renderer.transform.name.IndexOf("cube", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    renderer.enabled = false;
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return "tool";
            }

            char[] chars = name.ToCharArray();
            char[] invalid = Path.GetInvalidFileNameChars();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            return new string(chars);
        }

        private string GetNamespacedCacheDirectory(string cacheNamespace)
        {
            string segment = SanitizeCacheSegment(cacheNamespace);
            string directory = Path.Combine(_cacheDirectory, segment);
            Directory.CreateDirectory(directory);
            return directory;
        }

        private static string SanitizeCacheSegment(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "_shared";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = value.Trim().ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (chars[i] == invalid[j])
                    {
                        chars[i] = '_';
                        break;
                    }
                }
            }

            string sanitized = new string(chars).Trim();
            return string.IsNullOrWhiteSpace(sanitized) ? "_shared" : sanitized;
        }
    }
}
