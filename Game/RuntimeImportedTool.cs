using UnityEngine;

namespace MineMogul.ToolImportApi.Game
{
    public sealed class RuntimeImportedTool : BaseHeldTool
    {
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
                rb.isKinematic = true;
                rb.linearVelocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
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
                    _toolRootRigidbody.isKinematic = true;
                    _toolRootRigidbody.linearVelocity = Vector3.zero;
                    _toolRootRigidbody.angularVelocity = Vector3.zero;
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
                _toolRootRigidbody.isKinematic = true;
                _toolRootRigidbody.useGravity = true;
                _toolRootRigidbody.linearVelocity = Vector3.zero;
                _toolRootRigidbody.angularVelocity = Vector3.zero;
                _toolRootRigidbody.interpolation = RigidbodyInterpolation.None;
            }
        }
    }
}
