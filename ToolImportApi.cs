using System.Reflection;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;
using MineMogul.ToolImportApi.Game;
using MineMogul.ToolImportApi.Integration;
using MineMogul.ToolImportApi.Loaders;
using ToolImportPublicApi = MineMogul.ToolImportApi.Api.ToolImportApi;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MineMogul.ToolImportApi.Plugin
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency("modmogul.core", BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class ToolImportApiPlugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.main.toolimportapi";
        public const string PluginName = "Tool Import API";
        public const string PluginVersion = "1.0.1";

        private static readonly Harmony HarmonyInstance = new Harmony(PluginGuid);
        private static ToolImportApiPlugin _instance;
        private static float _startupUnscaledTime;
        private static float _startupLoadGraceSeconds = 6f;

        private ConfigEntry<bool> _enableModMogulIntegration;
        private ConfigEntry<KeyboardShortcut> _debugSpawnKey;
        private ConfigEntry<string> _debugSpawnToolId;
        private ConfigEntry<bool> _verboseLog;
        private ConfigEntry<bool> _verboseViewModelLogs;
        private ConfigEntry<bool> _injectVanillaArmsIfMissing;
        private ConfigEntry<bool> _enableArmPoseEditorUi;
        private ConfigEntry<KeyboardShortcut> _armPoseEditorToggleKey;
        private ConfigEntry<bool> _armPoseEditorShowLeftArm;
        private ConfigEntry<float> _startupLoadGraceSecondsConfig;

        private ToolImportRegistry _registry;
        private ArmPoseEditorUi _armPoseEditorUi;
        private float _nextFinalizePollTime;

        private void Awake()
        {
            _instance = this;
            _startupUnscaledTime = Time.unscaledTime;
            BindConfigEntries();

            string cacheDirectory = System.IO.Path.Combine(
                System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath,
                ".tool-import-cache");
            string savableMapPath = System.IO.Path.Combine(Paths.ConfigPath, "com.main.toolimportapi.savable-map.txt");

            _registry = CreateRegistry(cacheDirectory, savableMapPath, out ReflectionModMogulModelLoader modelLoader, out IModMogulAdapter modMogulAdapter);
            ToolImportRuntime.Registry = _registry;
            ToolImportPublicApi.Initialize(_registry, Logger);
            SetupArmPoseEditorUi();

            HarmonyInstance.PatchAll(Assembly.GetExecutingAssembly());
            SceneManager.sceneLoaded += OnSceneLoaded;

            if (_verboseLog.Value)
            {
                Logger.LogInfo("[ToolImportApi] Awake complete. ModelLoader=" + modelLoader.LoaderName +
                               ", ModMogulAdapter=" + modMogulAdapter.GetType().Name +
                               ", InjectVanillaArmsIfMissing=" + _injectVanillaArmsIfMissing.Value +
                               ", ArmPoseEditorUiEnabled=" + _enableArmPoseEditorUi.Value +
                               ", VerboseViewModelLogs=" + _verboseViewModelLogs.Value);
            }

            _registry.TryFinalizePending();
        }

        private void Update()
        {
            PollPendingFinalizations();
            TryHandleDebugSpawn();
        }

        private void BindConfigEntries()
        {
            _enableModMogulIntegration = Config.Bind(
                "ModMogul",
                "EnableIntegration",
                true,
                "When true, register tools in ModMogul if ModMogul is available.");

            _debugSpawnKey = Config.Bind(
                "Debug",
                "SpawnAnyToolKey",
                new KeyboardShortcut(KeyCode.F7),
                "Spawns the first registered tool to your inventory for testing.");

            _debugSpawnToolId = Config.Bind(
                "Debug",
                "SpawnSpecificToolId",
                string.Empty,
                "If set, F7 spawns this full id (ModId.ToolId) instead of the first tool.");

            _verboseLog = Config.Bind(
                "Debug",
                "VerboseLog",
                false,
                "Enable extra Tool Import API logs.");

            _verboseViewModelLogs = Config.Bind(
                "Debug",
                "VerboseViewModelLogs",
                false,
                "Enable detailed logging for viewmodel load/hierarchy/arm injection.");

            _injectVanillaArmsIfMissing = Config.Bind(
                "ViewModel",
                "InjectVanillaArmsIfMissing",
                true,
                "If imported viewmodel has no arm renderers, clone arm meshes from vanilla Wrench (SavableObjectID.WrenchTool).");

            _enableArmPoseEditorUi = Config.Bind(
                "ArmPoseEditor",
                "EnableUi",
                false,
                "Enable arm pose editor UI for currently equipped tool (bottom-left).");

            _armPoseEditorToggleKey = Config.Bind(
                "ArmPoseEditor",
                "ToggleUiKey",
                new KeyboardShortcut(KeyCode.F8),
                "Toggle arm pose editor UI visibility.");

            _armPoseEditorShowLeftArm = Config.Bind(
                "ArmPoseEditor",
                "ShowLeftArm",
                false,
                "When true, the editor keeps matching left arm variant visible while previewing right arm variants.");

            _startupLoadGraceSecondsConfig = Config.Bind(
                "SaveLoad",
                "StartupLoadGraceSeconds",
                6f,
                "Startup grace window before allowing save load to proceed when Tool Import API has known mappings. Helps avoid race with external mod registrations.");

            _startupLoadGraceSeconds = Mathf.Clamp(_startupLoadGraceSecondsConfig.Value, 0f, 20f);
        }

        private void SetupArmPoseEditorUi()
        {
            if (!_enableArmPoseEditorUi.Value)
            {
                return;
            }

            string pluginDirectory = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Paths.PluginPath;
            string exportDirectory = System.IO.Path.Combine(pluginDirectory, "ArmPoseExports");

            GameObject editorRoot = new GameObject("ToolImportApi.ArmPoseEditorUi");
            DontDestroyOnLoad(editorRoot);
            _armPoseEditorUi = editorRoot.AddComponent<ArmPoseEditorUi>();
            _armPoseEditorUi.Initialize(Logger, _armPoseEditorToggleKey, _armPoseEditorShowLeftArm, exportDirectory);
            Logger.LogInfo("[ToolImportApi] Arm pose editor UI enabled. Toggle key: " + _armPoseEditorToggleKey.Value + ", exportDir=" + exportDirectory);
        }

        private ToolImportRegistry CreateRegistry(
            string cacheDirectory,
            string savableMapPath,
            out ReflectionModMogulModelLoader modelLoader,
            out IModMogulAdapter modMogulAdapter)
        {
            modelLoader = new ReflectionModMogulModelLoader(cacheDirectory, Logger);
            PngIconLoader iconLoader = new PngIconLoader();
            SavableIdMapStore savableIdStore = new SavableIdMapStore(savableMapPath, Logger);
            GameToolFactoryOptions factoryOptions = new GameToolFactoryOptions
            {
                VerboseViewModelLogs = _verboseViewModelLogs.Value,
                InjectVanillaArmsIfMissing = _injectVanillaArmsIfMissing.Value
            };
            GameToolFactory gameToolFactory = new GameToolFactory(Logger, modelLoader, factoryOptions);

            modMogulAdapter = new NullModMogulAdapter();
            if (_enableModMogulIntegration.Value)
            {
                ReflectionModMogulAdapter reflectionAdapter = new ReflectionModMogulAdapter(Logger, cacheDirectory);
                if (reflectionAdapter.IsAvailable)
                {
                    modMogulAdapter = reflectionAdapter;
                }
            }

            return new ToolImportRegistry(
                this,
                Logger,
                iconLoader,
                gameToolFactory,
                modMogulAdapter,
                savableIdStore,
                cacheDirectory,
                _verboseLog.Value || _verboseViewModelLogs.Value);
        }

        private void PollPendingFinalizations()
        {
            if (_registry == null || Time.unscaledTime < _nextFinalizePollTime)
            {
                return;
            }

            _nextFinalizePollTime = Time.unscaledTime + 1f;
            _registry.TryFinalizePending();
        }

        private void TryHandleDebugSpawn()
        {
            if (_registry == null || !_debugSpawnKey.Value.IsDown())
            {
                return;
            }

            bool success;
            string message;
            if (string.IsNullOrWhiteSpace(_debugSpawnToolId.Value))
            {
                success = ToolImportPublicApi.TrySpawnAnyRegisteredTool(out message);
            }
            else
            {
                success = ToolImportPublicApi.TrySpawnTool(_debugSpawnToolId.Value, out message);
            }

            if (success)
            {
                Logger.LogInfo("[ToolImportApi] Debug spawn success: " + message);
            }
            else
            {
                Logger.LogWarning("[ToolImportApi] Debug spawn failed: " + message);
            }
        }

        private void OnDestroy()
        {
            if (_instance == this)
            {
                _instance = null;
            }

            SceneManager.sceneLoaded -= OnSceneLoaded;
            if (_armPoseEditorUi != null)
            {
                Destroy(_armPoseEditorUi.gameObject);
                _armPoseEditorUi = null;
            }
            HarmonyInstance.UnpatchSelf();
            if (ToolImportRuntime.Registry == _registry)
            {
                ToolImportRuntime.Registry = null;
            }
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            _registry?.TryFinalizePending();
            _registry?.EnsureRegisteredTemplatesInSaveManager();
        }

        internal static ToolImportApiPlugin Instance => _instance;
        internal static float StartupUnscaledTime => _startupUnscaledTime;
        internal static float StartupLoadGraceSeconds => _startupLoadGraceSeconds;
        internal static bool VerboseLogEnabled => _instance != null && _instance._verboseLog != null && _instance._verboseLog.Value;

        internal static void LogInfo(string message)
        {
            if (_instance != null)
            {
                _instance.Logger.LogInfo("[ToolImportApi] " + message);
            }
        }

        internal static void LogWarning(string message)
        {
            if (_instance != null)
            {
                _instance.Logger.LogWarning("[ToolImportApi] " + message);
            }
        }
    }

    [HarmonyPatch(typeof(SavingLoadingManager), "Awake")]
    internal static class ToolImportSaveRegistrationAwakePatch
    {
        private static void Postfix()
        {
            ToolImportRuntime.Registry?.TryFinalizePending();
            ToolImportRuntime.Registry?.EnsureRegisteredTemplatesInSaveManager();
        }
    }

    [HarmonyPatch(typeof(SavingLoadingManager), "LoadGame")]
    internal static class ToolImportSaveRegistrationLoadPatch
    {
        private static bool _allowDirectLoadCall;
        private static bool _deferredLoadRunning;
        private static string _lastLoadDeferReason;
        private static float _nextLoadLogTime;

        private static bool Prefix(SavingLoadingManager __instance, string fullFilePath)
        {
            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null || _allowDirectLoadCall)
            {
                return true;
            }

            registry.TryFinalizePending();
            registry.EnsureRegisteredTemplatesInSaveManager();

            if (CanProceedWithLoad(registry, out string reason))
            {
                if (ToolImportApiPlugin.VerboseLogEnabled)
                {
                    ToolImportApiPlugin.LogInfo("LoadGame proceed: " + reason);
                }

                return true;
            }

            if (_deferredLoadRunning)
            {
                MaybeLogDefer("LoadGame still deferred: coroutine already running.");
                return false;
            }

            ToolImportApiPlugin plugin = ToolImportApiPlugin.Instance;
            if (plugin == null || __instance == null)
            {
                return true;
            }

            MaybeLogDefer("LoadGame deferred: " + reason);
            _deferredLoadRunning = true;
            plugin.StartCoroutine(DeferredLoad(__instance, fullFilePath, registry));
            return false;
        }

        private static System.Collections.IEnumerator DeferredLoad(
            SavingLoadingManager saveManager,
            string fullFilePath,
            ToolImportRegistry registry)
        {
            try
            {
                float timeoutAt = Time.unscaledTime + 20f;
                while (Time.unscaledTime < timeoutAt)
                {
                    registry.TryFinalizePending();
                    registry.EnsureRegisteredTemplatesInSaveManager();
                    if (CanProceedWithLoad(registry, out string reason))
                    {
                        if (ToolImportApiPlugin.VerboseLogEnabled)
                        {
                            ToolImportApiPlugin.LogInfo("LoadGame deferred wait complete: " + reason);
                        }

                        break;
                    }

                    yield return null;
                }

                if (!CanProceedWithLoad(registry, out string timeoutReason))
                {
                    ToolImportApiPlugin.LogWarning("LoadGame defer timeout reached; continuing anyway. " + timeoutReason);
                }

                _allowDirectLoadCall = true;
                try
                {
                    saveManager.LoadGame(fullFilePath);
                }
                finally
                {
                    _allowDirectLoadCall = false;
                }
            }
            finally
            {
                _allowDirectLoadCall = false;
                _deferredLoadRunning = false;
            }
        }

        private static bool CanProceedWithLoad(ToolImportRegistry registry, out string reason)
        {
            reason = "ready";
            if (registry == null)
            {
                reason = "registry-null";
                return true;
            }

            registry.GetRegistrationStatus(out int registeredCount, out int pendingCount, out int mappedCount);
            float startupDelay = Time.unscaledTime - ToolImportApiPlugin.StartupUnscaledTime;
            bool hasKnownMappings = mappedCount > 0;
            bool withinStartupGrace = hasKnownMappings && startupDelay < ToolImportApiPlugin.StartupLoadGraceSeconds;

            if (withinStartupGrace)
            {
                reason = "within-startup-grace startupDelay=" + startupDelay.ToString("F2") +
                         "s grace=" + ToolImportApiPlugin.StartupLoadGraceSeconds.ToString("F2") +
                         "s registered=" + registeredCount +
                         " pending=" + pendingCount +
                         " mapped=" + mappedCount;
                return false;
            }

            if (!registry.IsReadyForSaveLoad())
            {
                reason = "registry-not-ready startupDelay=" + startupDelay.ToString("F2") +
                         "s registered=" + registeredCount +
                         " pending=" + pendingCount +
                         " mapped=" + mappedCount;
                return false;
            }

            reason = "ready startupDelay=" + startupDelay.ToString("F2") +
                     "s registered=" + registeredCount +
                     " pending=" + pendingCount +
                     " mapped=" + mappedCount;
            return true;
        }

        private static void MaybeLogDefer(string reason)
        {
            if (!ToolImportApiPlugin.VerboseLogEnabled)
            {
                return;
            }

            if (reason == _lastLoadDeferReason && Time.unscaledTime < _nextLoadLogTime)
            {
                return;
            }

            _lastLoadDeferReason = reason;
            _nextLoadLogTime = Time.unscaledTime + 1f;
            ToolImportApiPlugin.LogInfo(reason);
        }
    }

    [HarmonyPatch(typeof(SavingLoadingManager), "SaveGame")]
    internal static class ToolImportSaveRegistrationSavePatch
    {
        private static void Prefix()
        {
            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return;
            }

            registry.TryFinalizePending();
            registry.EnsureRegisteredTemplatesInSaveManager();
            if (ToolImportApiPlugin.VerboseLogEnabled)
            {
                registry.GetRegistrationStatus(out int registeredCount, out int pendingCount, out int mappedCount);
                ToolImportApiPlugin.LogInfo(
                    "SaveGame precheck complete. registered=" + registeredCount +
                    ", pending=" + pendingCount +
                    ", mapped=" + mappedCount);
            }
        }
    }

    [HarmonyPatch(typeof(SavingLoadingManager), "GetPrefab")]
    internal static class ToolImportGetPrefabFallbackPatch
    {
        private static readonly HashSet<int> LoggedFallbackSavableIds = new HashSet<int>();

        private static void Postfix(SavableObjectID objectID, ref GameObject __result)
        {
            if (__result != null)
            {
                return;
            }

            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return;
            }

            int savableId = (int)objectID;
            if (!registry.TryGetTemplateForSavableId(savableId, out GameObject templatePrefab, out string fullToolId))
            {
                return;
            }

            __result = templatePrefab;
            if (LoggedFallbackSavableIds.Add(savableId))
            {
                ToolImportApiPlugin.LogInfo(
                    "GetPrefab fallback resolved SavableObjectID " + savableId +
                    " via registered tool '" + fullToolId + "'.");
            }
        }
    }
}
