using UnityEngine;
using MineMogul.ToolImportApi.Core;

namespace MineMogul.ToolImportApi.Game
{
    public sealed class RuntimeImportedTool : BaseHeldTool
    {
        [System.Serializable]
        private sealed class RuntimeImportedToolSaveData
        {
            public bool IsInPlayerInventory;
            public int InventorySlotIndex = -1;
            public string ToolImportCustomData;
        }

        public string RegistrationKey;
        private Collider[] _toolColliders;
        private Rigidbody _toolRootRigidbody;
        private bool _physicsCacheReady;
        private bool _lastHeldState;

        public override string GetControlsText()
        {
            if (Singleton<KeybindManager>.Instance == null)
            {
                return "Drop - [Unbound]";
            }

            return "Drop - " + Singleton<KeybindManager>.Instance.GetBindingText(KeybindAction.DropTool);
        }

        public override void LoadFromSave(string json)
        {
            // Imported tools are instantiated from inactive templates; force active so
            // vanilla delayed inventory restore coroutine can run.
            if (!gameObject.activeSelf)
            {
                gameObject.SetActive(true);
            }

            base.LoadFromSave(json);
            ApplyCustomSaveData(json);
        }

        public override string GetCustomSaveData()
        {
            RuntimeImportedToolSaveData saveData = new RuntimeImportedToolSaveData
            {
                IsInPlayerInventory = Owner != null,
                ToolImportCustomData = CaptureCustomSaveData()
            };

            if (saveData.IsInPlayerInventory)
            {
                PlayerInventory inventory = Object.FindFirstObjectByType<PlayerInventory>();
                if (inventory != null)
                {
                    saveData.InventorySlotIndex = inventory.GetInventoryIndexForTool(this);
                }
            }

            return JsonUtility.ToJson(saveData);
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            _lastHeldState = Owner != null;
            SetHeldCollisionState(_lastHeldState);
            SyncHeldPhysicsState();
            MaintainViewModelAnchor();
        }

        protected override void OnDisable()
        {
            base.OnDisable();
            SetHeldCollisionState(false);
            _lastHeldState = false;
        }

        private void LateUpdate()
        {
            bool held = Owner != null;
            if (held != _lastHeldState)
            {
                SetHeldCollisionState(held);
                _lastHeldState = held;
            }

            if (held)
            {
                SyncHeldPhysicsState();
            }

            MaintainViewModelAnchor();
        }

        private void MaintainViewModelAnchor()
        {
            if (Owner == null || Owner.ViewModelContainer == null)
            {
                return;
            }

            Transform anchor = Owner.ViewModelContainer;
            if (transform.parent != anchor)
            {
                transform.SetParent(anchor, worldPositionStays: false);
            }

            transform.localPosition = Vector3.zero;
            transform.localRotation = Quaternion.identity;
            transform.localScale = Vector3.one;

            EnsurePhysicsCache();
            Rigidbody rb = _toolRootRigidbody;
            if (rb != null)
            {
                ZeroVelocityIfDynamic(rb);
                rb.isKinematic = true;
                rb.position = anchor.position;
                rb.rotation = anchor.rotation;
            }
        }

        private void EnsurePhysicsCache()
        {
            if (!_physicsCacheReady)
            {
                _toolColliders = GetComponentsInChildren<Collider>(true);
                _physicsCacheReady = true;
            }

            if (_toolRootRigidbody == null)
            {
                _toolRootRigidbody = GetComponent<Rigidbody>();
            }
        }

        private void SetHeldCollisionState(bool held)
        {
            EnsurePhysicsCache();
            if (_toolColliders != null)
            {
                for (int i = 0; i < _toolColliders.Length; i++)
                {
                    Collider collider = _toolColliders[i];
                    if (collider != null)
                    {
                        collider.enabled = !held;
                    }
                }
            }

            if (_toolRootRigidbody != null)
            {
                _toolRootRigidbody.detectCollisions = !held;
                if (held)
                {
                    ZeroVelocityIfDynamic(_toolRootRigidbody);
                    _toolRootRigidbody.isKinematic = true;
                }
            }
        }

        private void SyncHeldPhysicsState()
        {
            EnsurePhysicsCache();
            if (_toolRootRigidbody == null)
            {
                return;
            }

            if (Owner != null)
            {
                ZeroVelocityIfDynamic(_toolRootRigidbody);
                _toolRootRigidbody.isKinematic = true;
                _toolRootRigidbody.useGravity = true;
                _toolRootRigidbody.interpolation = RigidbodyInterpolation.None;
            }
        }

        private static void ZeroVelocityIfDynamic(Rigidbody rb)
        {
            if (rb.isKinematic)
            {
                return;
            }

            rb.linearVelocity = Vector3.zero;
            rb.angularVelocity = Vector3.zero;
        }

        private string CaptureCustomSaveData()
        {
            if (!TryResolveRegisteredTool(out RegisteredTool registeredTool))
            {
                return null;
            }

            Api.ToolCustomSaveDataDelegate callback = registeredTool.Request != null
                ? registeredTool.Request.CustomSaveData
                : null;
            if (callback == null)
            {
                return null;
            }

            try
            {
                return callback(this);
            }
            catch (System.Exception ex)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    "CustomSaveData callback failed for '" + GetToolIdForLog(registeredTool) + "': " + ex);
                return null;
            }
        }

        private void ApplyCustomSaveData(string json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            if (!TryResolveRegisteredTool(out RegisteredTool registeredTool))
            {
                return;
            }

            Api.ToolCustomLoadDataDelegate callback = registeredTool.Request != null
                ? registeredTool.Request.CustomLoadData
                : null;
            if (callback == null)
            {
                return;
            }

            RuntimeImportedToolSaveData saveData = null;
            try
            {
                saveData = JsonUtility.FromJson<RuntimeImportedToolSaveData>(json);
            }
            catch (System.Exception ex)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    "Custom load data parse failed for '" + GetToolIdForLog(registeredTool) + "': " + ex);
                return;
            }

            if (saveData == null || string.IsNullOrEmpty(saveData.ToolImportCustomData))
            {
                return;
            }

            try
            {
                callback(this, saveData.ToolImportCustomData);
            }
            catch (System.Exception ex)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    "CustomLoadData callback failed for '" + GetToolIdForLog(registeredTool) + "': " + ex);
            }
        }

        private bool TryResolveRegisteredTool(out RegisteredTool registeredTool)
        {
            registeredTool = null;
            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return false;
            }

            if (!string.IsNullOrWhiteSpace(RegistrationKey) &&
                registry.TryGetRegisteredTool(RegistrationKey, out registeredTool))
            {
                return true;
            }

            if (registry.TryGetRegisteredToolBySavableId((int)SavableObjectID, out registeredTool))
            {
                if (registeredTool != null && !string.IsNullOrWhiteSpace(registeredTool.FullToolId))
                {
                    RegistrationKey = registeredTool.FullToolId;
                }

                return true;
            }

            return false;
        }

        private string GetToolIdForLog(RegisteredTool registeredTool)
        {
            if (registeredTool != null && !string.IsNullOrWhiteSpace(registeredTool.FullToolId))
            {
                return registeredTool.FullToolId;
            }

            if (!string.IsNullOrWhiteSpace(RegistrationKey))
            {
                return RegistrationKey;
            }

            return "<unknown-runtime-tool>";
        }
    }
}
