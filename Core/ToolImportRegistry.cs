using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Game;
using MineMogul.ToolImportApi.Integration;
using MineMogul.ToolImportApi.Loaders;
using UnityEngine;

namespace MineMogul.ToolImportApi.Core
{
    internal sealed class ToolImportRegistry
    {
        private const string CustomPositioningSuffix = ".custom-positioning.json";
        private const string LegacyArmPoseSuffix = ".arm-pose.json";
        private const string ArmPoseCodeSuffix = ".arm-pose.cs";

        private readonly BaseUnityPlugin _plugin;
        private readonly ManualLogSource _logger;
        private readonly PngIconLoader _iconLoader;
        private readonly GameToolFactory _gameToolFactory;
        private readonly IModMogulAdapter _modMogulAdapter;
        private readonly SavableIdMapStore _savableIdStore;
        private readonly string _cacheDirectory;
        private readonly bool _verboseDiagnostics;

        private readonly object _gate = new object();
        private readonly List<PendingRegistration> _pending = new List<PendingRegistration>(32);
        private readonly Dictionary<string, RegisteredTool> _registeredById =
            new Dictionary<string, RegisteredTool>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _savableIdByTool =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<int, RegisteredTool> _registeredBySavableId =
            new Dictionary<int, RegisteredTool>(64);

        private bool _isFinalizeRunning;

        public ToolImportRegistry(
            BaseUnityPlugin plugin,
            ManualLogSource logger,
            PngIconLoader iconLoader,
            GameToolFactory gameToolFactory,
            IModMogulAdapter modMogulAdapter,
            SavableIdMapStore savableIdStore,
            string cacheDirectory,
            bool verboseDiagnostics)
        {
            _plugin = plugin;
            _logger = logger;
            _iconLoader = iconLoader;
            _gameToolFactory = gameToolFactory;
            _modMogulAdapter = modMogulAdapter;
            _savableIdStore = savableIdStore;
            _cacheDirectory = cacheDirectory;
            _verboseDiagnostics = verboseDiagnostics;
        }

        public ToolRegistrationResult Register(ToolRegistrationRequest request, Assembly sourceAssembly)
        {
            if (!TryValidateRequest(request, out string validationError))
            {
                return ToolRegistrationResult.Fail(BuildFullToolId(request), validationError);
            }

            string fullToolId = BuildFullToolId(request);
            lock (_gate)
            {
                if (_registeredById.ContainsKey(fullToolId))
                {
                    return ToolRegistrationResult.Fail(fullToolId, "Tool id is already finalized: " + fullToolId);
                }

                for (int i = 0; i < _pending.Count; i++)
                {
                    if (string.Equals(_pending[i].FullToolId, fullToolId, StringComparison.OrdinalIgnoreCase))
                    {
                        return ToolRegistrationResult.Fail(fullToolId, "Tool id is already queued: " + fullToolId);
                    }
                }

                _pending.Add(new PendingRegistration
                {
                    Request = request,
                    SourceAssembly = sourceAssembly,
                    FullToolId = fullToolId
                });
            }

            TryFinalizePending();
            return ToolRegistrationResult.Ok(fullToolId, "Queued for finalization.");
        }

        public void TryFinalizePending()
        {
            if (!IsGameReady())
            {
                return;
            }

            EnsureRegisteredTemplatesInSaveManager();

            if (!TryBeginFinalize())
            {
                return;
            }

            try
            {
                _plugin.StartCoroutine(FinalizePendingRoutine());
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    _isFinalizeRunning = false;
                }

                _logger.LogError(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Failed to start registration finalization coroutine: " + ex);
            }
        }

        public void EnsureRegisteredTemplatesInSaveManager()
        {
            List<RegisteredTool> snapshot = CopyRegisteredTools();
            for (int i = 0; i < snapshot.Count; i++)
            {
                _gameToolFactory.EnsureSaveRegistration(snapshot[i]);
            }
        }

        public bool TrySpawnTool(string fullToolId, out string message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(fullToolId))
            {
                message = "Full tool id is empty.";
                return false;
            }

            RegisteredTool tool = GetRegisteredTool(fullToolId);
            if (tool == null)
            {
                message = "No registered tool found for id: " + fullToolId;
                return false;
            }

            if (_gameToolFactory.TrySpawnToolToInventory(tool, out message))
            {
                return true;
            }

            if (_modMogulAdapter.TrySpawnTool(tool, out message))
            {
                return true;
            }

            return false;
        }

        public bool IsReadyForSaveLoad()
        {
            lock (_gate)
            {
                return !_isFinalizeRunning && _pending.Count == 0;
            }
        }

        public void GetRegistrationStatus(out int registeredCount, out int pendingCount, out int mappedCount)
        {
            lock (_gate)
            {
                registeredCount = _registeredById.Count;
                pendingCount = _pending.Count;
            }

            mappedCount = _savableIdStore != null ? _savableIdStore.CountMappings() : 0;
        }

        public bool TryGetTemplateForSavableId(int savableId, out GameObject templatePrefab, out string fullToolId)
        {
            templatePrefab = null;
            fullToolId = null;
            if (savableId <= 0)
            {
                return false;
            }

            lock (_gate)
            {
                if (_registeredBySavableId.TryGetValue(savableId, out RegisteredTool tool) &&
                    tool != null &&
                    tool.TemplateObject != null)
                {
                    templatePrefab = tool.TemplateObject;
                    fullToolId = tool.FullToolId;
                    return true;
                }
            }

            return false;
        }

        public bool TrySpawnFirstTool(out string message)
        {
            message = "No registered tools available.";
            RegisteredTool tool = null;
            lock (_gate)
            {
                foreach (KeyValuePair<string, RegisteredTool> pair in _registeredById)
                {
                    tool = pair.Value;
                    break;
                }
            }

            if (tool == null)
            {
                return false;
            }

            return TrySpawnTool(tool.FullToolId, out message);
        }

        public bool TryGiveToolFromPickup(string fullToolId, out string message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(fullToolId))
            {
                message = "Full tool id is empty.";
                return false;
            }

            RegisteredTool tool = GetRegisteredTool(fullToolId);
            if (tool == null)
            {
                message = "No registered tool found for id: " + fullToolId;
                return false;
            }

            return _gameToolFactory.TrySpawnToolToInventory(tool, out message);
        }

        public bool TrySpawnToolFromShop(string fullToolId, Vector3 position, Quaternion rotation, int quantity, out string message)
        {
            message = null;
            if (string.IsNullOrWhiteSpace(fullToolId))
            {
                message = "Full tool id is empty.";
                return false;
            }

            RegisteredTool tool = GetRegisteredTool(fullToolId);
            if (tool == null)
            {
                message = "No registered tool found for id: " + fullToolId;
                return false;
            }

            return _gameToolFactory.TrySpawnToolToWorld(tool, position, rotation, quantity, out message);
        }

        private IEnumerator FinalizePendingRoutine()
        {
            try
            {
                while (TryDequeuePending(out PendingRegistration pending))
                {
                    yield return FinalizeSingleRegistration(pending);
                }
            }
            finally
            {
                lock (_gate)
                {
                    _isFinalizeRunning = false;
                }
            }
        }

        internal bool TryGetRegisteredTool(string fullToolId, out RegisteredTool tool)
        {
            tool = null;
            if (string.IsNullOrWhiteSpace(fullToolId))
            {
                return false;
            }

            lock (_gate)
            {
                return _registeredById.TryGetValue(fullToolId, out tool) && tool != null;
            }
        }

        internal bool TryGetRegisteredToolBySavableId(int savableId, out RegisteredTool tool)
        {
            tool = null;
            lock (_gate)
            {
                return _registeredBySavableId.TryGetValue(savableId, out tool) && tool != null;
            }
        }

        private IEnumerator FinalizeSingleRegistration(PendingRegistration pending)
        {
            if (pending == null || pending.Request == null)
            {
                yield break;
            }

            ApplyCustomPositioningIfEnabled(pending);

            if (!TryLoadRegistrationResourceBytes(
                    pending,
                    pending.Request.IconResourceName,
                    "icon",
                    out byte[] iconBytes))
            {
                yield break;
            }

            if (!TryLoadRegistrationResourceBytes(
                    pending,
                    pending.Request.WorldModelResourceName,
                    "world model",
                    out byte[] worldModelBytes))
            {
                yield break;
            }

            if (!TryLoadRegistrationResourceBytes(
                    pending,
                    pending.Request.ViewModelResourceName,
                    "view model",
                    out byte[] viewModelBytes))
            {
                yield break;
            }

            LogSuccessfulResourceLoad(pending, iconBytes, worldModelBytes, viewModelBytes);

            if (!_iconLoader.TryLoadIcon(iconBytes, pending.FullToolId + "_icon", out Sprite iconSprite, out string iconParseError))
            {
                _logger.LogError(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " " + pending.FullToolId + " icon decode failed: " + iconParseError);
                yield break;
            }

            int savableId = AllocateSavableId(pending.FullToolId, pending.Request.PreferredSavableId);
            ToolBuildRequest buildRequest = new ToolBuildRequest
            {
                FullToolId = pending.FullToolId,
                CacheNamespace = pending.Request.CacheNamespace,
                Request = pending.Request,
                SourceAssembly = pending.SourceAssembly,
                SavableId = savableId,
                Icon = iconSprite,
                WorldModelBytes = worldModelBytes,
                ViewModelBytes = viewModelBytes
            };

            ToolBuildResult buildResult = new ToolBuildResult();
            yield return _gameToolFactory.CreateRegisteredTool(buildRequest, buildResult);
            if (!buildResult.Success || buildResult.Tool == null)
            {
                _logger.LogError(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " " + pending.FullToolId + " build failed: " + buildResult.Error);
                yield break;
            }

            lock (_gate)
            {
                _registeredById[pending.FullToolId] = buildResult.Tool;
                _savableIdByTool[pending.FullToolId] = savableId;
                _registeredBySavableId[savableId] = buildResult.Tool;
            }

            // Register direct id mappings so ToolBuilder/crate conversions can resolve
            // even if external shop integration prefabs are incomplete.
            ToolImportPickupMarker.RegisterSavableIdMapping(savableId, pending.FullToolId);
            if (pending.Request != null && pending.Request.ShopMetadata != null && pending.Request.ShopMetadata.ShopBlockId.HasValue)
            {
                ToolImportPickupMarker.RegisterSavableIdMapping(pending.Request.ShopMetadata.ShopBlockId.Value, pending.FullToolId);
            }

            _gameToolFactory.EnsureSaveRegistration(buildResult.Tool);

            if (_modMogulAdapter.IsAvailable)
            {
                if (_modMogulAdapter.TryRegisterTool(buildResult.Tool, pending.Request, out string registerMessage))
                {
                    VerboseLog("ModMogul register ok for " + pending.FullToolId + ": " + registerMessage);
                }
                else
                {
                    _logger.LogWarning(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " ModMogul register skipped for " + pending.FullToolId + ": " + registerMessage);
                }

                if (_modMogulAdapter.TryAddToShop(buildResult.Tool, pending.Request, out string shopMessage))
                {
                    VerboseLog("ModMogul shop add ok for " + pending.FullToolId + ": " + shopMessage);
                }
                else
                {
                    _logger.LogWarning(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " ModMogul shop add skipped for " + pending.FullToolId + ": " + shopMessage);
                }
            }

            VerboseLog("Finalized tool '" + pending.FullToolId + "' with SavableObjectID " + savableId + ".");
        }

        private bool TryLoadRegistrationResourceBytes(
            PendingRegistration pending,
            string resourceName,
            string resourceLabel,
            out byte[] bytes)
        {
            bytes = null;
            if (!EmbeddedResourceLoader.TryReadAllBytes(
                    pending.SourceAssembly,
                    resourceName,
                    out bytes,
                    out string readError))
            {
                _logger.LogError(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " " + pending.FullToolId + " " + resourceLabel + " load failed: " + readError);
                return false;
            }

            return true;
        }

        private void LogSuccessfulResourceLoad(
            PendingRegistration pending,
            byte[] iconBytes,
            byte[] worldModelBytes,
            byte[] viewModelBytes)
        {
            if (!_verboseDiagnostics)
            {
                return;
            }

            _logger.LogInfo(
                MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Resource load ok for " + pending.FullToolId +
                ". iconRes=" + pending.Request.IconResourceName + " (" + iconBytes.Length + " bytes)" +
                ", worldRes=" + pending.Request.WorldModelResourceName + " (" + worldModelBytes.Length + " bytes)" +
                ", viewRes=" + pending.Request.ViewModelResourceName + " (" + viewModelBytes.Length + " bytes)");
        }

        private int AllocateSavableId(string fullToolId, int? preferredId)
        {
            lock (_gate)
            {
                if (_savableIdByTool.TryGetValue(fullToolId, out int existing))
                {
                    return existing;
                }
            }

            if (_savableIdStore != null && _savableIdStore.TryGet(fullToolId, out int persistedId))
            {
                if (!IsSavableIdTakenByOtherTool(persistedId, fullToolId))
                {
                    if (_verboseDiagnostics)
                    {
                        _logger.LogInfo(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Reusing persisted SavableObjectID " + persistedId + " for " + fullToolId + ".");
                    }

                    return persistedId;
                }

                _logger.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Persisted SavableObjectID " + persistedId +
                    " for " + fullToolId + " is currently taken. Allocating a new id.");
            }

            int candidate;
            if (preferredId.HasValue && preferredId.Value > 0)
            {
                candidate = preferredId.Value;
            }
            else
            {
                candidate = 20000 + (StableHash(fullToolId) % 30000);
            }

            int attempts = 0;
            while (attempts < 50000)
            {
                if (!IsSavableIdTakenByOtherTool(candidate, fullToolId))
                {
                    _savableIdStore?.Set(fullToolId, candidate);
                    if (_verboseDiagnostics)
                    {
                        _logger.LogInfo(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Assigned SavableObjectID " + candidate + " to " + fullToolId + ".");
                    }
                    return candidate;
                }

                candidate++;
                if (candidate > 69999)
                {
                    candidate = 20000;
                }

                attempts++;
            }

            throw new InvalidOperationException("Could not allocate a free SavableObjectID for " + fullToolId + ".");
        }

        private bool IsSavableIdTakenByOtherTool(int savableId, string fullToolId)
        {
            lock (_gate)
            {
                if (_registeredBySavableId.TryGetValue(savableId, out RegisteredTool existingTool) &&
                    existingTool != null &&
                    !string.Equals(existingTool.FullToolId, fullToolId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (_savableIdByTool.TryGetValue(fullToolId, out int assignedId) && assignedId == savableId)
                {
                    return false;
                }
            }

            if (_savableIdStore != null && _savableIdStore.IsMappedToAnotherTool(savableId, fullToolId))
            {
                return true;
            }

            return _gameToolFactory.IsSavableIdInUse(savableId);
        }

        private RegisteredTool GetRegisteredTool(string fullToolId)
        {
            lock (_gate)
            {
                _registeredById.TryGetValue(fullToolId, out RegisteredTool tool);
                return tool;
            }
        }

        private bool TryBeginFinalize()
        {
            lock (_gate)
            {
                if (_isFinalizeRunning || _pending.Count == 0)
                {
                    return false;
                }

                _isFinalizeRunning = true;
                return true;
            }
        }

        private bool TryDequeuePending(out PendingRegistration pending)
        {
            pending = null;
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    return false;
                }

                pending = _pending[0];
                _pending.RemoveAt(0);
                return pending != null;
            }
        }

        private List<RegisteredTool> CopyRegisteredTools()
        {
            List<RegisteredTool> snapshot = new List<RegisteredTool>(8);
            lock (_gate)
            {
                foreach (KeyValuePair<string, RegisteredTool> pair in _registeredById)
                {
                    snapshot.Add(pair.Value);
                }
            }

            return snapshot;
        }

        private static bool IsGameReady()
        {
            return UnityEngine.Object.FindFirstObjectByType<SavingLoadingManager>() != null;
        }

        private static bool TryValidateRequest(ToolRegistrationRequest request, out string error)
        {
            error = null;
            if (request == null)
            {
                error = "Registration request is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.ModId))
            {
                error = "ModId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.ToolId))
            {
                error = "ToolId is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.DisplayName))
            {
                error = "DisplayName is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.IconResourceName))
            {
                error = "IconResourceName is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.WorldModelResourceName))
            {
                error = "WorldModelResourceName is required.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(request.ViewModelResourceName))
            {
                error = "ViewModelResourceName is required.";
                return false;
            }

            if (request.AssetAssembly == null)
            {
                error = "AssetAssembly is missing. Pass request.AssetAssembly or call RegisterTool directly from the owner plugin assembly.";
                return false;
            }

            if (request.ShopMetadata == null)
            {
                request.ShopMetadata = new ToolShopMetadata();
            }

            if (request.ShopMetadata.Price < 0)
            {
                request.ShopMetadata.Price = 0;
            }

            if (request.ShopMetadata.MaxStackSize <= 0)
            {
                request.ShopMetadata.MaxStackSize = 1;
            }

            request.CacheNamespace = ResolveCacheNamespace(request.CacheNamespace, request.ModId);

            return true;
        }

        private static string ResolveCacheNamespace(string requestedNamespace, string modId)
        {
            string value = requestedNamespace;
            if (string.IsNullOrWhiteSpace(value))
            {
                value = modId;
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                return "_shared";
            }

            return value.Trim();
        }

        private void ApplyCustomPositioningIfEnabled(PendingRegistration pending)
        {
            if (pending == null || pending.Request == null || !pending.Request.EnableCustomPositioning)
            {
                return;
            }

            if (!TryReadCustomPositioningDocument(
                    pending.SourceAssembly,
                    pending.Request,
                    pending.FullToolId,
                    out string resourceName,
                    out CustomPositioningDocument document,
                    out string readError))
            {
                VerboseLog(
                    "Custom positioning fallback for " + pending.FullToolId +
                    ": " + readError);
                return;
            }

            if (!TryParseCustomPositioningDocument(
                    pending.FullToolId,
                    resourceName,
                    document,
                    out ArmPoseRequest loadedArmPose,
                    out ToolViewTransformRequest loadedViewTransform))
            {
                return;
            }

            ApplyCustomPositioningOverrides(
                pending,
                resourceName,
                loadedArmPose,
                loadedViewTransform);
        }

        private bool TryParseCustomPositioningDocument(
            string fullToolId,
            string resourceName,
            CustomPositioningDocument document,
            out ArmPoseRequest loadedArmPose,
            out ToolViewTransformRequest loadedViewTransform)
        {
            loadedArmPose = null;
            loadedViewTransform = null;

            if (CustomPositioningConverter.TryToRequests(
                    document,
                    out loadedArmPose,
                    out loadedViewTransform,
                    out string parseError))
            {
                return true;
            }

            _logger.LogWarning(
                MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Custom positioning parse failed for " + fullToolId +
                " (" + resourceName + "): " + parseError);
            return false;
        }

        private void ApplyCustomPositioningOverrides(
            PendingRegistration pending,
            string resourceName,
            ArmPoseRequest loadedArmPose,
            ToolViewTransformRequest loadedViewTransform)
        {
            if (pending == null || pending.Request == null)
            {
                return;
            }

            int overrideCount = 0;
            if (loadedArmPose != null)
            {
                pending.Request.ArmPose = loadedArmPose;
                overrideCount = loadedArmPose.BoneOverrides != null ? loadedArmPose.BoneOverrides.Count : 0;
            }

            if (loadedViewTransform != null)
            {
                pending.Request.ViewModelTransform = loadedViewTransform;
            }

            _logger.LogInfo(
                MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Custom positioning loaded for " + pending.FullToolId +
                ". resource=" + resourceName +
                ", boneOverrides=" + overrideCount +
                ", hasViewModelTransform=" + (loadedViewTransform != null));
        }

        private bool TryReadCustomPositioningDocument(
            Assembly sourceAssembly,
            ToolRegistrationRequest request,
            string fullToolId,
            out string resourceName,
            out CustomPositioningDocument document,
            out string error)
        {
            resourceName = null;
            document = null;
            error = null;

            if (TryReadCustomPositioningDocumentFromCache(
                    request,
                    fullToolId,
                    out string fileSource,
                    out document,
                    out string fileError))
            {
                resourceName = fileSource;
                return true;
            }

            string requestedResource = ResolveCustomPositioningResource(
                sourceAssembly,
                request,
                fullToolId);

            if (string.IsNullOrWhiteSpace(requestedResource))
            {
                error =
                    "custom positioning not found. cacheLookup=" + fileError +
                    "; embedded expected suffix '" +
                    fullToolId + CustomPositioningSuffix +
                    "' or '" + fullToolId + LegacyArmPoseSuffix + "' or '" + fullToolId + ArmPoseCodeSuffix + "').";
                return false;
            }

            if (!EmbeddedResourceLoader.TryReadAllBytes(sourceAssembly, requestedResource, out byte[] bytes, out string readError))
            {
                error = "failed reading resource '" + requestedResource + "': " + readError;
                return false;
            }

            string text = Encoding.UTF8.GetString(bytes);
            if (!TryParseCustomPositioningDocumentText(text, requestedResource, out document, out string parseError))
            {
                error = parseError;
                return false;
            }

            TryWriteCustomPositioningCacheFile(request, fullToolId, text, requestedResource);

            resourceName = requestedResource;
            return true;
        }

        private void TryWriteCustomPositioningCacheFile(
            ToolRegistrationRequest request,
            string fullToolId,
            string content,
            string sourceName)
        {
            if (string.IsNullOrWhiteSpace(_cacheDirectory) ||
                string.IsNullOrWhiteSpace(fullToolId) ||
                string.IsNullOrWhiteSpace(content))
            {
                return;
            }

            string cacheNamespace = ResolveCacheNamespace(
                request != null ? request.CacheNamespace : null,
                request != null ? request.ModId : null);
            string folderPath = Path.Combine(_cacheDirectory, SanitizeFolderName(cacheNamespace));
            string outputFileName = BuildCacheOutputFileName(fullToolId, sourceName);
            string outputPath = Path.Combine(folderPath, outputFileName);

            try
            {
                Directory.CreateDirectory(folderPath);
                File.WriteAllText(outputPath, content, Encoding.UTF8);
                VerboseLog("Wrote custom positioning cache file: " + outputPath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Failed to cache custom positioning data for " +
                    fullToolId + " at '" + outputPath + "': " + ex.Message);
            }
        }

        private static string BuildCacheOutputFileName(string fullToolId, string sourceName)
        {
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                string requested = sourceName.Trim();
                if (requested.EndsWith(ArmPoseCodeSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    return fullToolId + ArmPoseCodeSuffix;
                }
            }

            return fullToolId + CustomPositioningSuffix;
        }

        private bool TryReadCustomPositioningDocumentFromCache(
            ToolRegistrationRequest request,
            string fullToolId,
            out string sourcePath,
            out CustomPositioningDocument document,
            out string error)
        {
            sourcePath = null;
            document = null;
            error = null;

            if (string.IsNullOrWhiteSpace(_cacheDirectory))
            {
                error = "cache directory is not configured.";
                return false;
            }

            string cacheNamespace = ResolveCacheNamespace(
                request != null ? request.CacheNamespace : null,
                request != null ? request.ModId : null);
            string folderPath = Path.Combine(_cacheDirectory, SanitizeFolderName(cacheNamespace));

            if (!Directory.Exists(folderPath))
            {
                error = "cache folder does not exist: " + folderPath;
                return false;
            }

            List<string> candidateFiles = BuildCustomPositioningFileCandidates(
                fullToolId,
                request != null ? request.CustomPositioningResourceName : null);

            for (int i = 0; i < candidateFiles.Count; i++)
            {
                string candidateFile = candidateFiles[i];
                string candidatePath = Path.Combine(folderPath, candidateFile);
                if (!File.Exists(candidatePath))
                {
                    continue;
                }

                string text;
                try
                {
                    text = File.ReadAllText(candidatePath, Encoding.UTF8);
                }
                catch (Exception ex)
                {
                    error = "failed reading cache file '" + candidatePath + "': " + ex.Message;
                    return false;
                }

                if (!TryParseCustomPositioningDocumentText(text, candidatePath, out document, out error))
                {
                    return false;
                }

                sourcePath = candidatePath;
                return true;
            }

            error =
                "no custom positioning file in '" + folderPath +
                "'. checked: " + string.Join(", ", candidateFiles.ToArray());
            return false;
        }

        private static List<string> BuildCustomPositioningFileCandidates(string fullToolId, string requestedName)
        {
            HashSet<string> unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> ordered = new List<string>(4);

            AddCandidateFileName(ordered, unique, fullToolId + CustomPositioningSuffix);
            AddCandidateFileName(ordered, unique, fullToolId + LegacyArmPoseSuffix);
            AddCandidateFileName(ordered, unique, fullToolId + ArmPoseCodeSuffix);

            if (!string.IsNullOrWhiteSpace(requestedName))
            {
                string requested = requestedName.Trim();
                AddCandidateFileName(ordered, unique, requested);

                string fileName = Path.GetFileName(requested);
                AddCandidateFileName(ordered, unique, fileName);

                int fullToolIndex = requested.LastIndexOf(fullToolId + CustomPositioningSuffix, StringComparison.OrdinalIgnoreCase);
                if (fullToolIndex >= 0)
                {
                    AddCandidateFileName(ordered, unique, requested.Substring(fullToolIndex));
                }

                int legacyIndex = requested.LastIndexOf(fullToolId + LegacyArmPoseSuffix, StringComparison.OrdinalIgnoreCase);
                if (legacyIndex >= 0)
                {
                    AddCandidateFileName(ordered, unique, requested.Substring(legacyIndex));
                }

                int codeIndex = requested.LastIndexOf(fullToolId + ArmPoseCodeSuffix, StringComparison.OrdinalIgnoreCase);
                if (codeIndex >= 0)
                {
                    AddCandidateFileName(ordered, unique, requested.Substring(codeIndex));
                }
            }

            return ordered;
        }

        private static void AddCandidateFileName(List<string> ordered, HashSet<string> unique, string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return;
            }

            string trimmed = candidate.Trim();
            bool isJson = trimmed.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            bool isCs = trimmed.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (!isJson && !isCs)
            {
                return;
            }

            if (!unique.Add(trimmed))
            {
                return;
            }

            ordered.Add(trimmed);
        }

        private static string SanitizeFolderName(string value)
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

        private static bool TryParseCustomPositioningDocumentJson(
            string json,
            string sourceName,
            out CustomPositioningDocument document,
            out string error)
        {
            document = null;
            error = null;

            try
            {
                document = JsonUtility.FromJson<CustomPositioningDocument>(json);
            }
            catch (Exception ex)
            {
                error = "invalid JSON in '" + sourceName + "': " + ex.Message;
                return false;
            }

            if (document != null)
            {
                return true;
            }

            error = "source '" + sourceName + "' parsed as null document.";
            return false;
        }

        private static bool TryParseCustomPositioningDocumentText(
            string text,
            string sourceName,
            out CustomPositioningDocument document,
            out string error)
        {
            document = null;
            error = null;

            if (!string.IsNullOrWhiteSpace(sourceName) &&
                sourceName.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CustomPositioningCsParser.TryParseDocument(text, out document, out error);
            }

            return TryParseCustomPositioningDocumentJson(text, sourceName, out document, out error);
        }

        private static string ResolveCustomPositioningResource(
            Assembly sourceAssembly,
            ToolRegistrationRequest request,
            string fullToolId)
        {
            string requestedResource = request != null ? request.CustomPositioningResourceName : null;
            if (!string.IsNullOrWhiteSpace(requestedResource))
            {
                requestedResource = ResolveCustomPositioningResourceName(sourceAssembly, requestedResource);
            }

            if (!string.IsNullOrWhiteSpace(requestedResource))
            {
                return requestedResource;
            }

            return FindCustomPositioningResourceByConvention(sourceAssembly, fullToolId);
        }

        private static string ResolveCustomPositioningResourceName(Assembly sourceAssembly, string requestedName)
        {
            if (sourceAssembly == null || string.IsNullOrWhiteSpace(requestedName))
            {
                return null;
            }

            string[] resourceNames = sourceAssembly.GetManifestResourceNames();
            for (int i = 0; i < resourceNames.Length; i++)
            {
                if (string.Equals(resourceNames[i], requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    return resourceNames[i];
                }
            }

            for (int i = 0; i < resourceNames.Length; i++)
            {
                if (resourceNames[i].EndsWith(requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    return resourceNames[i];
                }
            }

            return null;
        }

        private static string FindCustomPositioningResourceByConvention(Assembly sourceAssembly, string fullToolId)
        {
            if (sourceAssembly == null)
            {
                return null;
            }

            string[] resourceNames = sourceAssembly.GetManifestResourceNames();
            if (resourceNames == null || resourceNames.Length == 0)
            {
                return null;
            }

            string[] suffixes = new string[]
            {
                fullToolId + CustomPositioningSuffix,
                fullToolId + LegacyArmPoseSuffix,
                fullToolId + ArmPoseCodeSuffix
            };

            for (int s = 0; s < suffixes.Length; s++)
            {
                string suffix = suffixes[s];
                for (int i = 0; i < resourceNames.Length; i++)
                {
                    string name = resourceNames[i];
                    if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
            }

            return null;
        }

        private static int StableHash(string text)
        {
            unchecked
            {
                const int offset = unchecked((int)2166136261);
                const int prime = 16777619;
                int hash = offset;
                for (int i = 0; i < text.Length; i++)
                {
                    hash ^= text[i];
                    hash *= prime;
                }

                if (hash == int.MinValue)
                {
                    return 0;
                }

                return Math.Abs(hash);
            }
        }

        private static string BuildFullToolId(ToolRegistrationRequest request)
        {
            if (request == null)
            {
                return "<null-request>";
            }

            return BuildFullToolId(request.ModId, request.ToolId);
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

        private void VerboseLog(string message)
        {
            if (_verboseDiagnostics)
            {
                _logger.LogInfo(MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " " + message);
            }
        }
    }
}
