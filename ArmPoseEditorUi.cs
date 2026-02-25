using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;
using MineMogul.ToolImportApi.Game;
using MineMogul.ToolImportApi.Integration;
using UnityEngine;

namespace MineMogul.ToolImportApi.Plugin
{
    internal sealed class ArmPoseEditorUi : MonoBehaviour
    {
        private const string ViewModelContainerPath = "Player/Camera/ViewModelContainer";
        private const float RigRefreshIntervalSeconds = 0.4f;
        private const int WindowId = 582193;
        private const float WindowWidth = 680f;
        private const float WindowMinHeight = 360f;
        private const float BoneIndentWidth = 18f;
        private const float PositionSliderMin = -2f;
        private const float PositionSliderMax = 2f;
        private const float ScaleSliderMin = 0.05f;
        private const float ScaleSliderMax = 4f;
        private static readonly Color WindowBackdropColor = new Color(0.82f, 0.82f, 0.82f, 1f);

        private readonly List<Transform> _rightArmVariants = new List<Transform>(8);
        private readonly List<BoneEntry> _bones = new List<BoneEntry>(96);
        private readonly Dictionary<int, BoneEntry> _bonesByInstanceId = new Dictionary<int, BoneEntry>(128);
        private readonly Dictionary<int, bool> _expandedByInstanceId = new Dictionary<int, bool>(128);
        private Rect _windowRect = new Rect(12f, 12f, WindowWidth, WindowMinHeight);

        private ManualLogSource _logger;
        private ConfigEntry<KeyboardShortcut> _toggleKey;
        private ConfigEntry<bool> _showLeftArm;
        private string _exportDirectory;

        private Transform _toolRoot;
        private Transform _toolViewModelRoot;
        private Transform _fpsArmsRoot;
        private Transform _armsRoot;
        private Transform _toolTransformTarget;
        private string _toolTransformPath = string.Empty;
        private Vector3 _toolLocalPosition = Vector3.zero;
        private Vector3 _toolLocalEulerAngles = Vector3.zero;
        private Vector3 _toolLocalScale = Vector3.one;
        private bool _includeToolTransformInExport = true;
        private string _currentFullToolId;
        private int _currentToolRootInstanceId;
        private float _nextRigRefreshTime;
        private bool _isVisible;
        private int _selectedRightArmIndex;
        private Vector2 _boneScroll;
        private Vector2 _windowScroll;
        private string _status = "Waiting for equipped tool viewmodel...";
        private bool _hideToolMesh;
        private bool _cursorOverrideActive;
        private bool _previousCursorVisible;
        private CursorLockMode _previousCursorLockState;
        private bool _gameInputSuppressed;
        private MonoBehaviour _suppressedUiManagerBehaviour;
        private bool _suppressedUiManagerWasEnabled;
        private readonly List<MonoBehaviour> _disabledPlayerControls = new List<MonoBehaviour>(4);

        private sealed class BoneEntry
        {
            public Transform Bone;
            public string BonePath;
            public Vector3 Rotation;
            public Vector3 BaselineRotation;
            public bool EditEnabled;
        }

        public void Initialize(
            ManualLogSource logger,
            ConfigEntry<KeyboardShortcut> toggleKey,
            ConfigEntry<bool> showLeftArm,
            string exportDirectory)
        {
            _logger = logger;
            _toggleKey = toggleKey;
            _showLeftArm = showLeftArm;
            _exportDirectory = exportDirectory ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(_exportDirectory))
            {
                Directory.CreateDirectory(_exportDirectory);
            }
        }

        private void Update()
        {
            if (_toggleKey != null && _toggleKey.Value.IsDown())
            {
                _isVisible = !_isVisible;
                _status = _isVisible ? "UI visible. Game input blocked." : "UI hidden.";
                if (_isVisible)
                {
                    AcquireCursorForUi();
                    BeginGameInputSuppression();
                }
                else
                {
                    EndGameInputSuppression();
                    ReleaseCursorForUi();
                }
            }

            if (_isVisible)
            {
                Cursor.visible = true;
                Cursor.lockState = CursorLockMode.None;
                Input.ResetInputAxes();
            }

            if (Time.unscaledTime >= _nextRigRefreshTime || _fpsArmsRoot == null)
            {
                _nextRigRefreshTime = Time.unscaledTime + RigRefreshIntervalSeconds;
                RefreshRigContext();
            }
        }

        private void OnGUI()
        {
            if (!_isVisible)
            {
                return;
            }

            _windowRect.width = Mathf.Min(WindowWidth, Screen.width - 16f);
            _windowRect.height = Mathf.Clamp(Screen.height - 16f, WindowMinHeight, Screen.height - 8f);
            _windowRect.x = 8f;
            _windowRect.y = 8f;

            Color previousColor = GUI.color;
            GUI.color = WindowBackdropColor;
            GUI.DrawTexture(_windowRect, Texture2D.whiteTexture, ScaleMode.StretchToFill, false);
            GUI.color = previousColor;

            GUI.Window(WindowId, _windowRect, DrawWindowContents, "ToolImportApi Arm Pose Editor");
        }

        private void DrawWindowContents(int _)
        {
            _windowScroll = GUILayout.BeginScrollView(_windowScroll, false, true);
            GUILayout.Label("Status: " + _status);
            GUILayout.Space(6f);

            if (_fpsArmsRoot == null || _armsRoot == null)
            {
                GUILayout.Label("No FP_Arms_Unarmed rig found on equipped tool.");
                if (GUILayout.Button("Refresh"))
                {
                    RefreshRigContext();
                }

                GUILayout.EndScrollView();
                GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
                return;
            }

            GUILayout.Label("Current Tool: " + _currentFullToolId);
            GUILayout.Space(4f);

            DrawArmVariantSelector();
            GUILayout.Space(6f);
            DrawArmPositionEditor();
            GUILayout.Space(6f);
            DrawActionButtons();
            GUILayout.Space(8f);
            DrawToolTransformEditor();
            GUILayout.Space(8f);
            float boneHeight = Mathf.Max(140f, _windowRect.height - 500f);
            DrawBoneEditor(boneHeight);
            GUILayout.EndScrollView();
            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }

        private void DrawArmVariantSelector()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("Select Arm Type (Right Arm Variants)");

            if (_rightArmVariants.Count == 0)
            {
                GUILayout.Label("No right arm variants found under FP_Arms_Unarmed.");
                GUILayout.EndVertical();
                return;
            }

            if (_selectedRightArmIndex < 0 || _selectedRightArmIndex >= _rightArmVariants.Count)
            {
                _selectedRightArmIndex = 0;
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("<", GUILayout.Width(28f)))
            {
                _selectedRightArmIndex--;
                if (_selectedRightArmIndex < 0)
                {
                    _selectedRightArmIndex = _rightArmVariants.Count - 1;
                }
            }

            GUILayout.Label(_rightArmVariants[_selectedRightArmIndex].name, GUILayout.Width(260f));

            if (GUILayout.Button(">", GUILayout.Width(28f)))
            {
                _selectedRightArmIndex++;
                if (_selectedRightArmIndex >= _rightArmVariants.Count)
                {
                    _selectedRightArmIndex = 0;
                }
            }

            if (GUILayout.Button("Apply Variant", GUILayout.Width(110f)))
            {
                ApplySelectedArmVariant();
            }

            GUILayout.EndHorizontal();

            _showLeftArm.Value = GUILayout.Toggle(
                _showLeftArm.Value,
                "Also show matching left arm variant");
            GUILayout.EndVertical();
        }

        private void DrawArmPositionEditor()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("Upper Arm Position");

            Transform leftUpperArm = GetUpperArmTransform("L");
            Transform rightUpperArm = GetUpperArmTransform("R");
            if (leftUpperArm == null && rightUpperArm == null)
            {
                GUILayout.Label("Could not find UpperArm.L or UpperArm.R on the current rig.");
                GUILayout.EndVertical();
                return;
            }

            if (rightUpperArm != null)
            {
                DrawSingleArmPositionEditor(rightUpperArm, "Right Upper Arm");
            }
            else
            {
                GUILayout.Label("Right Upper Arm: not found");
            }

            if (leftUpperArm != null)
            {
                DrawSingleArmPositionEditor(leftUpperArm, "Left Upper Arm");
            }
            else
            {
                GUILayout.Label("Left Upper Arm: not found");
            }

            GUILayout.EndVertical();
        }

        private void DrawActionButtons()
        {
            GUILayout.BeginVertical("box");
            bool nextHide = GUILayout.Toggle(_hideToolMesh, "Hide Tool Mesh (keep arms visible)");
            if (nextHide != _hideToolMesh)
            {
                _hideToolMesh = nextHide;
                ApplyToolMeshVisibility();
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Refresh Rig", GUILayout.Width(110f)))
            {
                RefreshRigContext();
            }

            if (GUILayout.Button("Capture Current Rotations", GUILayout.Width(180f)))
            {
                CaptureCurrentRotations();
            }

            if (GUILayout.Button("Reset Edited", GUILayout.Width(110f)))
            {
                ResetEditedRotations();
            }

            if (GUILayout.Button("Export Pose", GUILayout.Width(110f)))
            {
                ExportPoseSnippet();
            }

            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        private void DrawToolTransformEditor()
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("Tool Transform");
            _includeToolTransformInExport = GUILayout.Toggle(
                _includeToolTransformInExport,
                "Include tool transform override in export");

            if (_toolTransformTarget == null)
            {
                GUILayout.Label("No tool transform target found on current viewmodel.");
                if (GUILayout.Button("Re-detect Tool Target", GUILayout.Width(180f)))
                {
                    RefreshToolTransformTarget();
                }

                GUILayout.EndVertical();
                return;
            }

            GUILayout.Label("Target: " + (string.IsNullOrWhiteSpace(_toolTransformPath) ? "<ViewModel root>" : _toolTransformPath));
            if (GUILayout.Button("Re-detect Tool Target", GUILayout.Width(180f)))
            {
                RefreshToolTransformTarget();
            }

            _toolLocalPosition = NormalizeVectorZero(ApplyVector3Sliders(
                "Local Position",
                _toolTransformTarget.localPosition,
                PositionSliderMin,
                PositionSliderMax));
            _toolTransformTarget.localPosition = _toolLocalPosition;

            _toolLocalEulerAngles = ApplyVector3Sliders(
                "Local Rotation (Euler)",
                NormalizeEulerAngles(_toolTransformTarget.localEulerAngles),
                -180f,
                180f);
            _toolTransformTarget.localEulerAngles = _toolLocalEulerAngles;

            _toolLocalScale = ClampScaleVector(ApplyVector3Sliders(
                "Local Scale",
                _toolTransformTarget.localScale,
                ScaleSliderMin,
                ScaleSliderMax));
            _toolTransformTarget.localScale = _toolLocalScale;

            GUILayout.EndVertical();
        }

        private void DrawBoneEditor(float scrollHeight)
        {
            GUILayout.BeginVertical("box");
            GUILayout.Label("Bone Rotation Sliders");
            GUILayout.Label("Expand branches as needed. Enable a row, then edit X/Y/Z local Euler angles.");
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Expand All", GUILayout.Width(100f)))
            {
                SetExpansionRecursive(_armsRoot, true);
            }

            if (GUILayout.Button("Collapse All", GUILayout.Width(100f)))
            {
                SetExpansionRecursive(_armsRoot, false);
                if (_armsRoot != null)
                {
                    _expandedByInstanceId[_armsRoot.GetInstanceID()] = true;
                }
            }
            GUILayout.EndHorizontal();

            _boneScroll = GUILayout.BeginScrollView(_boneScroll, GUILayout.Height(scrollHeight));
            if (_armsRoot == null)
            {
                GUILayout.Label("Arms_Root not found.");
            }
            else
            {
                int rootId = _armsRoot.GetInstanceID();
                bool rootExpanded = GetExpanded(rootId, true);
                GUILayout.BeginHorizontal();
                if (GUILayout.Button(rootExpanded ? "v" : ">", GUILayout.Width(24f)))
                {
                    rootExpanded = !rootExpanded;
                    _expandedByInstanceId[rootId] = rootExpanded;
                }

                GUILayout.Label(_armsRoot.name);
                GUILayout.EndHorizontal();

                if (rootExpanded)
                {
                    for (int i = 0; i < _armsRoot.childCount; i++)
                    {
                        DrawBoneNode(_armsRoot.GetChild(i), 1);
                    }
                }
            }

            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        private void DrawBoneNode(Transform boneTransform, int depth)
        {
            if (boneTransform == null || !_bonesByInstanceId.TryGetValue(boneTransform.GetInstanceID(), out BoneEntry entry))
            {
                return;
            }

            bool hasChildren = boneTransform.childCount > 0;
            int instanceId = boneTransform.GetInstanceID();
            bool expanded = !hasChildren || GetExpanded(instanceId, GetDefaultExpanded(depth));
            bool useArmRootBackground = IsPrimaryArmRootSection(boneTransform);

            if (useArmRootBackground)
            {
                GUILayout.BeginVertical("box");
            }
            else
            {
                GUILayout.BeginVertical(GUIStyle.none);
            }
            GUILayout.BeginHorizontal();
            GUILayout.Space(depth * BoneIndentWidth);
            if (hasChildren)
            {
                if (GUILayout.Button(expanded ? "v" : ">", GUILayout.Width(24f)))
                {
                    expanded = !expanded;
                    _expandedByInstanceId[instanceId] = expanded;
                }
            }
            else
            {
                GUILayout.Space(24f);
            }

            entry.EditEnabled = GUILayout.Toggle(entry.EditEnabled, entry.Bone.name);
            GUILayout.EndHorizontal();

            if (entry.EditEnabled)
            {
                entry.Rotation = NormalizeEulerAngles(entry.Bone.localEulerAngles);
                GUILayout.BeginHorizontal();
                GUILayout.Space((depth + 1) * BoneIndentWidth + 24f);
                GUILayout.BeginVertical();
                float x = DrawAxisSlider("X", entry.Rotation.x);
                float y = DrawAxisSlider("Y", entry.Rotation.y);
                float z = DrawAxisSlider("Z", entry.Rotation.z);
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();

                Vector3 updated = new Vector3(x, y, z);
                if (!Approximately(updated, entry.Rotation))
                {
                    entry.Rotation = updated;
                    entry.Bone.localEulerAngles = updated;
                }
            }

            if (hasChildren && expanded)
            {
                for (int i = 0; i < boneTransform.childCount; i++)
                {
                    Transform child = boneTransform.GetChild(i);
                    int childDepth = depth + GetChildIndentStep(boneTransform, child);
                    DrawBoneNode(child, childDepth);
                }
            }

            GUILayout.EndVertical();
        }

        private bool IsPrimaryArmRootSection(Transform boneTransform)
        {
            if (boneTransform == null || _armsRoot == null || boneTransform.parent != _armsRoot)
            {
                return false;
            }

            return string.Equals(boneTransform.name, "UpperArm.L", StringComparison.Ordinal) ||
                   string.Equals(boneTransform.name, "UpperArm.R", StringComparison.Ordinal);
        }

        private float DrawAxisSlider(string axis, float value)
        {
            return DrawAxisSlider(axis, value, -180f, 180f, true);
        }

        private float DrawAxisSlider(string axis, float value, float min, float max, bool normalizeAngle)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(axis, GUILayout.Width(14f));
            float next = GUILayout.HorizontalSlider(value, min, max, GUILayout.Width(240f));
            if (normalizeAngle)
            {
                next = NormalizeAngle(next);
            }

            GUILayout.Label(next.ToString("F1", CultureInfo.InvariantCulture), GUILayout.Width(52f));
            GUILayout.EndHorizontal();
            return next;
        }

        private Vector3 ApplyVector3Sliders(string label, Vector3 value, float min, float max)
        {
            GUILayout.Label(label);
            float x = DrawAxisSlider("X", value.x, min, max, false);
            float y = DrawAxisSlider("Y", value.y, min, max, false);
            float z = DrawAxisSlider("Z", value.z, min, max, false);
            return new Vector3(x, y, z);
        }

        private void RefreshRigContext()
        {
            Transform previousToolRoot = _toolRoot;
            if (!TryResolveCurrentRig(out Transform toolRoot, out Transform fpsRoot, out string fullToolId))
            {
                if (_hideToolMesh && previousToolRoot != null)
                {
                    SetToolRenderersVisible(previousToolRoot, true);
                }

                _toolRoot = null;
                _toolViewModelRoot = null;
                _fpsArmsRoot = null;
                _armsRoot = null;
                _toolTransformTarget = null;
                _toolTransformPath = string.Empty;
                _toolLocalPosition = Vector3.zero;
                _toolLocalEulerAngles = Vector3.zero;
                _toolLocalScale = Vector3.one;
                _currentFullToolId = string.Empty;
                _currentToolRootInstanceId = 0;
                _rightArmVariants.Clear();
                _bones.Clear();
                _bonesByInstanceId.Clear();
                _expandedByInstanceId.Clear();
                _status = "No equipped FP_Arms_Unarmed found.";
                return;
            }

            int toolRootId = toolRoot.GetInstanceID();
            bool toolChanged = toolRootId != _currentToolRootInstanceId;
            if (toolChanged && _hideToolMesh && previousToolRoot != null && previousToolRoot != toolRoot)
            {
                SetToolRenderersVisible(previousToolRoot, true);
            }

            _toolRoot = toolRoot;
            _toolViewModelRoot = toolRoot.Find("ViewModel");
            _fpsArmsRoot = fpsRoot;
            _currentFullToolId = fullToolId;
            _currentToolRootInstanceId = toolRootId;
            _armsRoot = FindTransformByName(_fpsArmsRoot, "Arms_Root");
            if (_armsRoot == null)
            {
                _toolTransformTarget = null;
                _toolTransformPath = string.Empty;
                _rightArmVariants.Clear();
                _bones.Clear();
                _bonesByInstanceId.Clear();
                _expandedByInstanceId.Clear();
                _status = "FP_Arms_Unarmed found, but Arms_Root missing on tool: " + _currentFullToolId;
                return;
            }

            if (toolChanged || _rightArmVariants.Count == 0 || _bones.Count == 0)
            {
                RebuildArmVariantList();
                RebuildBoneList();
            }

            RefreshToolTransformTarget();

            _status =
                "Rig ready. tool=" + _currentFullToolId +
                ", variants=" + _rightArmVariants.Count +
                ", bones=" + _bones.Count +
                ", toolTarget=" + (_toolTransformTarget != null ? _toolTransformTarget.name : "<none>") +
                ", selected=" + (_rightArmVariants.Count > 0 ? _rightArmVariants[_selectedRightArmIndex].name : "<none>");

            ApplyToolMeshVisibility();
        }

        private bool TryResolveCurrentRig(out Transform toolRoot, out Transform fpsRoot, out string fullToolId)
        {
            toolRoot = null;
            fpsRoot = null;
            fullToolId = string.Empty;

            GameObject container = GameObject.Find(ViewModelContainerPath);
            if (container == null)
            {
                return false;
            }

            Transform containerTransform = container.transform;
            for (int i = 0; i < containerTransform.childCount; i++)
            {
                Transform equipped = containerTransform.GetChild(i);
                if (equipped == null || !equipped.gameObject.activeInHierarchy)
                {
                    continue;
                }

                Transform viewModel = equipped.Find("ViewModel");
                if (viewModel == null)
                {
                    continue;
                }

                Transform fps = FindTransformByName(viewModel, "FP_Arms_Unarmed");
                if (fps == null)
                {
                    continue;
                }

                toolRoot = equipped;
                fpsRoot = fps;
                fullToolId = ResolveFullToolId(equipped);
                return true;
            }

            return false;
        }

        private static string ResolveFullToolId(Transform equippedToolRoot)
        {
            if (equippedToolRoot == null)
            {
                return "<unknown-tool>";
            }

            RuntimeImportedTool runtimeTool = equippedToolRoot.GetComponent<RuntimeImportedTool>();
            if (runtimeTool != null && !string.IsNullOrWhiteSpace(runtimeTool.RegistrationKey))
            {
                return runtimeTool.RegistrationKey;
            }

            BaseHeldTool heldTool = equippedToolRoot.GetComponent<BaseHeldTool>();
            if (heldTool != null)
            {
                int savableId = (int)heldTool.SavableObjectID;
                if (ToolImportPickupMarker.TryResolveFullToolId(savableId, out string mapped))
                {
                    return mapped;
                }

                return heldTool.name + "#" + savableId;
            }

            return equippedToolRoot.name;
        }

        private void RebuildArmVariantList()
        {
            _rightArmVariants.Clear();
            if (_fpsArmsRoot == null)
            {
                return;
            }

            for (int i = 0; i < _fpsArmsRoot.childCount; i++)
            {
                Transform child = _fpsArmsRoot.GetChild(i);
                if (child == null || !child.name.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsRightArmVariantName(child.name))
                {
                    _rightArmVariants.Add(child);
                }
            }

            if (_selectedRightArmIndex >= _rightArmVariants.Count)
            {
                _selectedRightArmIndex = 0;
            }
        }

        private void RebuildBoneList()
        {
            _bones.Clear();
            _bonesByInstanceId.Clear();
            _expandedByInstanceId.Clear();
            if (_armsRoot == null || _fpsArmsRoot == null)
            {
                return;
            }

            _expandedByInstanceId[_armsRoot.GetInstanceID()] = true;
            for (int i = 0; i < _armsRoot.childCount; i++)
            {
                BuildBoneEntriesRecursive(_armsRoot.GetChild(i), 0);
            }
        }

        private void BuildBoneEntriesRecursive(Transform current, int depth)
        {
            if (current == null)
            {
                return;
            }

            BoneEntry entry = new BoneEntry
            {
                Bone = current,
                BonePath = GetRelativePath(_fpsArmsRoot, current),
                Rotation = NormalizeEulerAngles(current.localEulerAngles),
                BaselineRotation = NormalizeEulerAngles(current.localEulerAngles),
                EditEnabled = false
            };

            _bones.Add(entry);
            _bonesByInstanceId[current.GetInstanceID()] = entry;
            _expandedByInstanceId[current.GetInstanceID()] = GetDefaultExpanded(depth);

            for (int i = 0; i < current.childCount; i++)
            {
                BuildBoneEntriesRecursive(current.GetChild(i), depth + 1);
            }
        }

        private void RefreshToolTransformTarget()
        {
            _toolTransformTarget = FindDefaultToolTransformTarget();
            if (_toolTransformTarget == null || _toolViewModelRoot == null)
            {
                _toolTransformPath = string.Empty;
                _toolLocalPosition = Vector3.zero;
                _toolLocalEulerAngles = Vector3.zero;
                _toolLocalScale = Vector3.one;
                return;
            }

            _toolTransformPath = GetRelativePath(_toolViewModelRoot, _toolTransformTarget);
            _toolLocalPosition = _toolTransformTarget.localPosition;
            _toolLocalEulerAngles = NormalizeEulerAngles(_toolTransformTarget.localEulerAngles);
            _toolLocalScale = _toolTransformTarget.localScale;
        }

        private Transform FindDefaultToolTransformTarget()
        {
            if (_toolViewModelRoot == null)
            {
                return null;
            }

            Renderer[] renderers = _toolViewModelRoot.GetComponentsInChildren<Renderer>(true);
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
                    : FindCommonAncestorWithin(_toolViewModelRoot, candidate, renderer.transform);
            }

            if (candidate != null)
            {
                return candidate;
            }

            return _toolViewModelRoot.childCount > 0 ? _toolViewModelRoot.GetChild(0) : _toolViewModelRoot;
        }

        private void DrawSingleArmPositionEditor(Transform armTransform, string label)
        {
            if (armTransform == null)
            {
                return;
            }

            GUILayout.Label(label + ": " + armTransform.name);
            Vector3 current = armTransform.localPosition;
            Vector3 updated = NormalizeVectorZero(ApplyVector3Sliders(
                "Local Position",
                current,
                PositionSliderMin,
                PositionSliderMax));
            if (!Approximately(updated, current))
            {
                armTransform.localPosition = updated;
            }
        }

        private Transform GetUpperArmTransform(string sideSuffix)
        {
            if (_armsRoot == null || string.IsNullOrWhiteSpace(sideSuffix))
            {
                return null;
            }

            return FindTransformByName(_armsRoot, "UpperArm." + sideSuffix.Trim());
        }

        private void ApplySelectedArmVariant()
        {
            if (_fpsArmsRoot == null || _rightArmVariants.Count == 0)
            {
                return;
            }

            string selectedRight = _rightArmVariants[_selectedRightArmIndex].name;
            string selectedLeft = InferLeftArmName(selectedRight);

            for (int i = 0; i < _fpsArmsRoot.childCount; i++)
            {
                Transform child = _fpsArmsRoot.GetChild(i);
                if (child == null || !child.name.StartsWith("Arm_", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                child.gameObject.SetActive(false);
                SetRenderersEnabled(child, false);
            }

            Transform right = _fpsArmsRoot.Find(selectedRight);
            if (right != null)
            {
                right.gameObject.SetActive(true);
                SetRenderersEnabled(right, true);
            }

            if (_showLeftArm != null && _showLeftArm.Value && !string.IsNullOrWhiteSpace(selectedLeft))
            {
                Transform left = _fpsArmsRoot.Find(selectedLeft);
                if (left != null)
                {
                    left.gameObject.SetActive(true);
                    SetRenderersEnabled(left, true);
                }
            }

            _status = "Applied arm variant for " + _currentFullToolId + ": " + selectedRight;
        }

        private void CaptureCurrentRotations()
        {
            int captured = 0;
            for (int i = 0; i < _bones.Count; i++)
            {
                BoneEntry bone = _bones[i];
                if (bone.Bone != null)
                {
                    bone.Rotation = NormalizeEulerAngles(bone.Bone.localEulerAngles);
                    bone.BaselineRotation = bone.Rotation;
                    bone.EditEnabled = true;
                    captured++;
                }
            }

            if (_toolTransformTarget != null)
            {
                _toolLocalPosition = NormalizeVectorZero(_toolTransformTarget.localPosition);
                _toolLocalEulerAngles = NormalizeEulerAngles(_toolTransformTarget.localEulerAngles);
                _toolLocalScale = ClampScaleVector(_toolTransformTarget.localScale);
                _includeToolTransformInExport = true;
            }

            _status = "Captured " + captured + " bone rotations for " + _currentFullToolId + " (all marked for export). Arm/tool transforms use current scene values.";
        }

        private void ResetEditedRotations()
        {
            int resetCount = 0;
            for (int i = 0; i < _bones.Count; i++)
            {
                BoneEntry bone = _bones[i];
                if (!bone.EditEnabled || bone.Bone == null)
                {
                    continue;
                }

                bone.Rotation = bone.BaselineRotation;
                bone.Bone.localEulerAngles = bone.BaselineRotation;
                resetCount++;
            }

            _status = "Reset edited bones to baseline for " + _currentFullToolId + ": " + resetCount;
        }

        private void ExportPoseSnippet()
        {
            if (_fpsArmsRoot == null || _rightArmVariants.Count == 0)
            {
                _status = "Cannot export: no active rig.";
                return;
            }

            if (string.IsNullOrWhiteSpace(_exportDirectory))
            {
                _status = "Cannot export: export directory not configured.";
                return;
            }

            string right = _rightArmVariants[_selectedRightArmIndex].name;
            string left = InferLeftArmName(right);
            string preferredPair = string.IsNullOrWhiteSpace(left) ? right : left + "|" + right;

            List<BoneEntry> edited = new List<BoneEntry>(32);
            for (int i = 0; i < _bones.Count; i++)
            {
                if (_bones[i].EditEnabled)
                {
                    edited.Add(_bones[i]);
                }
            }

            List<ArmBonePose> exportOverrides = new List<ArmBonePose>(edited.Count + 2);
            for (int i = 0; i < edited.Count; i++)
            {
                BoneEntry bone = edited[i];
                if (bone == null || bone.Bone == null)
                {
                    continue;
                }

                Vector3 rotation = NormalizeEulerAngles(bone.Bone.localEulerAngles);
                exportOverrides.Add(new ArmBonePose
                {
                    BonePath = bone.BonePath,
                    LocalEulerAngles = rotation
                });
            }

            Directory.CreateDirectory(_exportDirectory);
            string safeToolId = SanitizeFileName(string.IsNullOrWhiteSpace(_currentFullToolId) ? "unknown-tool" : _currentFullToolId);
            string exportPath = Path.Combine(_exportDirectory, safeToolId + ".arm-pose.cs");
            string resourceName = safeToolId + ".arm-pose.cs";

            StringBuilder sb = new StringBuilder(8192);
            sb.AppendLine("// Tool: " + _currentFullToolId);
            sb.AppendLine("// Paste into your ToolRegistrationRequest setup:");
            sb.AppendLine("request.ArmPose = new ArmPoseRequest");
            sb.AppendLine("{");
            sb.AppendLine("    PreferredArmPair = \"" + Escape(preferredPair) + "\",");
            sb.AppendLine("    BoneOverrides = new List<ArmBonePose>");
            sb.AppendLine("    {");

            for (int i = 0; i < edited.Count; i++)
            {
                BoneEntry bone = edited[i];
                if (bone == null || bone.Bone == null)
                {
                    continue;
                }

                Vector3 rotation = NormalizeEulerAngles(bone.Bone.localEulerAngles);
                sb.AppendLine("        new ArmBonePose");
                sb.AppendLine("        {");
                sb.AppendLine("            BonePath = \"" + Escape(bone.BonePath) + "\",");
                sb.AppendLine("            LocalEulerAngles = new Vector3(" +
                              FormatFloat(rotation.x) + "f, " +
                              FormatFloat(rotation.y) + "f, " +
                              FormatFloat(rotation.z) + "f)");
                sb.AppendLine("        },");
            }

            int armPositionOverrideCount = 0;
            Transform leftUpperArm = GetUpperArmTransform("L");
            Transform rightUpperArm = GetUpperArmTransform("R");
            if (AppendArmPositionOverride(sb, exportOverrides, leftUpperArm, GetRelativePath(_fpsArmsRoot, leftUpperArm)))
            {
                armPositionOverrideCount++;
            }

            if (AppendArmPositionOverride(sb, exportOverrides, rightUpperArm, GetRelativePath(_fpsArmsRoot, rightUpperArm)))
            {
                armPositionOverrideCount++;
            }

            sb.AppendLine("    }");
            sb.AppendLine("};");

            if (_includeToolTransformInExport && _toolTransformTarget != null)
            {
                Vector3 toolPosition = NormalizeVectorZero(_toolTransformTarget.localPosition);
                Vector3 toolRotation = NormalizeEulerAngles(_toolTransformTarget.localEulerAngles);
                Vector3 toolScale = ClampScaleVector(_toolTransformTarget.localScale);
                string transformPath = string.IsNullOrWhiteSpace(_toolTransformPath) ? "." : _toolTransformPath;

                sb.AppendLine();
                sb.AppendLine("request.ViewModelTransform = new ToolViewTransformRequest");
                sb.AppendLine("{");
                sb.AppendLine("    TransformPath = \"" + Escape(transformPath) + "\",");
                sb.AppendLine("    LocalPosition = new Vector3(" +
                              FormatFloat(toolPosition.x) + "f, " +
                              FormatFloat(toolPosition.y) + "f, " +
                              FormatFloat(toolPosition.z) + "f),");
                sb.AppendLine("    LocalEulerAngles = new Vector3(" +
                              FormatFloat(toolRotation.x) + "f, " +
                              FormatFloat(toolRotation.y) + "f, " +
                              FormatFloat(toolRotation.z) + "f),");
                sb.AppendLine("    LocalScale = new Vector3(" +
                              FormatFloat(toolScale.x) + "f, " +
                              FormatFloat(toolScale.y) + "f, " +
                              FormatFloat(toolScale.z) + "f)");
                sb.AppendLine("};");
            }

            sb.AppendLine();
            sb.AppendLine("request.EnableCustomPositioning = true;");
            sb.AppendLine("request.CustomPositioningResourceName = \"" + Escape(resourceName) + "\";");
            File.WriteAllText(exportPath, sb.ToString(), Encoding.UTF8);

            _status =
                "Exported " + edited.Count +
                " bone overrides + " + armPositionOverrideCount +
                " upper-arm position overrides to " + exportPath +
                ". Set request.EnableCustomPositioning=true and request.CustomPositioningResourceName=\"" +
                resourceName + "\".";
            if (_logger != null)
            {
                _logger.LogInfo("[ToolImportApi] " + _status);
            }
        }

        private bool AppendArmPositionOverride(StringBuilder sb, List<ArmBonePose> exportOverrides, Transform armTransform, string bonePath)
        {
            if (sb == null || exportOverrides == null || armTransform == null)
            {
                return false;
            }

            Vector3 position = NormalizeVectorZero(armTransform.localPosition);
            string resolvedPath = string.IsNullOrWhiteSpace(bonePath) ? armTransform.name : bonePath;
            exportOverrides.Add(new ArmBonePose
            {
                BonePath = resolvedPath,
                BoneName = armTransform.name,
                LocalPosition = position
            });

            sb.AppendLine("        new ArmBonePose");
            sb.AppendLine("        {");
            sb.AppendLine("            BonePath = \"" + Escape(resolvedPath) + "\",");
            sb.AppendLine("            BoneName = \"" + Escape(armTransform.name) + "\",");
            sb.AppendLine("            LocalPosition = new Vector3(" +
                          FormatFloat(position.x) + "f, " +
                          FormatFloat(position.y) + "f, " +
                          FormatFloat(position.z) + "f)");
            sb.AppendLine("        },");
            return true;
        }

        private void ApplyToolMeshVisibility()
        {
            if (_toolRoot == null)
            {
                return;
            }

            SetToolRenderersVisible(_toolRoot, !_hideToolMesh);
        }

        private void SetToolRenderersVisible(Transform toolRoot, bool visible)
        {
            if (toolRoot == null)
            {
                return;
            }

            Renderer[] renderers = toolRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                {
                    continue;
                }

                if (_fpsArmsRoot != null && renderer.transform.IsChildOf(_fpsArmsRoot))
                {
                    continue;
                }

                renderer.enabled = visible;
            }
        }

        private void AcquireCursorForUi()
        {
            if (_cursorOverrideActive)
            {
                return;
            }

            _previousCursorVisible = Cursor.visible;
            _previousCursorLockState = Cursor.lockState;
            Cursor.visible = true;
            Cursor.lockState = CursorLockMode.None;
            _cursorOverrideActive = true;
        }

        private void ReleaseCursorForUi()
        {
            if (!_cursorOverrideActive)
            {
                return;
            }

            Cursor.visible = _previousCursorVisible;
            Cursor.lockState = _previousCursorLockState;
            _cursorOverrideActive = false;
        }

        private void OnDestroy()
        {
            if (_hideToolMesh && _toolRoot != null)
            {
                SetToolRenderersVisible(_toolRoot, true);
            }

            EndGameInputSuppression();
            ReleaseCursorForUi();
        }

        private void BeginGameInputSuppression()
        {
            if (_gameInputSuppressed)
            {
                return;
            }

            BeginUiManagerSuppression();
            TogglePlayerControls(false);
            Input.ResetInputAxes();
            _gameInputSuppressed = true;
        }

        private void EndGameInputSuppression()
        {
            if (!_gameInputSuppressed)
            {
                return;
            }

            TogglePlayerControls(true);
            EndUiManagerSuppression();
            Input.ResetInputAxes();
            _gameInputSuppressed = false;
        }

        private void TogglePlayerControls(bool enable)
        {
            GameObject playerRoot = null;
            try
            {
                playerRoot = GameObject.FindWithTag("Player");
            }
            catch
            {
                // ignore missing tag errors
            }

            if (playerRoot == null && Camera.main != null)
            {
                playerRoot = Camera.main.transform.root.gameObject;
            }

            if (playerRoot == null)
            {
                return;
            }

            MonoBehaviour[] behaviours = playerRoot.GetComponents<MonoBehaviour>();
            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null)
                {
                    continue;
                }

                string typeName = behaviour.GetType().Name;
                if (typeName != "PlayerController" && typeName != "PlayerInventory")
                {
                    continue;
                }

                if (!enable)
                {
                    if (!behaviour.enabled)
                    {
                        continue;
                    }

                    behaviour.enabled = false;
                    _disabledPlayerControls.Add(behaviour);
                    continue;
                }

                if (_disabledPlayerControls.Contains(behaviour))
                {
                    behaviour.enabled = true;
                }
            }

            if (enable)
            {
                _disabledPlayerControls.Clear();
            }
        }

        private void BeginUiManagerSuppression()
        {
            if (_suppressedUiManagerBehaviour != null)
            {
                return;
            }

            object instance = TryGetSingletonInstance("UIManager");
            MonoBehaviour uiManager = instance as MonoBehaviour;
            if (uiManager == null)
            {
                return;
            }

            _suppressedUiManagerBehaviour = uiManager;
            _suppressedUiManagerWasEnabled = uiManager.enabled;
            uiManager.enabled = false;
        }

        private void EndUiManagerSuppression()
        {
            if (_suppressedUiManagerBehaviour == null)
            {
                return;
            }

            _suppressedUiManagerBehaviour.enabled = _suppressedUiManagerWasEnabled;
            _suppressedUiManagerBehaviour = null;
            _suppressedUiManagerWasEnabled = false;
        }

        private static object TryGetSingletonInstance(string typeName)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return null;
            }

            Type type = FindType(typeName);
            if (type == null)
            {
                return null;
            }

            return TryGetSingletonInstance(type);
        }

        private static object TryGetSingletonInstance(Type type)
        {
            if (type == null)
            {
                return null;
            }

            const BindingFlags anyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.FlattenHierarchy;
            string[] singletonNames = { "Instance", "instance", "Singleton", "Current", "current" };
            for (int i = 0; i < singletonNames.Length; i++)
            {
                string singletonName = singletonNames[i];
                PropertyInfo property = type.GetProperty(singletonName, anyStatic);
                if (property != null && property.CanRead)
                {
                    try
                    {
                        object value = property.GetValue(null, null);
                        if (value != null)
                        {
                            return value;
                        }
                    }
                    catch
                    {
                        // ignore and continue
                    }
                }

                FieldInfo field = type.GetField(singletonName, anyStatic);
                if (field != null)
                {
                    try
                    {
                        object value = field.GetValue(null);
                        if (value != null)
                        {
                            return value;
                        }
                    }
                    catch
                    {
                        // ignore and continue
                    }
                }
            }

            return UnityEngine.Object.FindObjectOfType(type);
        }

        private static Type FindType(string fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName))
            {
                return null;
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        private static string SanitizeFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return "tool";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder sb = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isInvalid = false;
                for (int j = 0; j < invalid.Length; j++)
                {
                    if (c == invalid[j])
                    {
                        isInvalid = true;
                        break;
                    }
                }

                sb.Append(isInvalid ? '_' : c);
            }

            return sb.ToString();
        }

        private static string Escape(string text)
        {
            return string.IsNullOrEmpty(text) ? string.Empty : text.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static bool IsRightArmVariantName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            return name.EndsWith(".r", StringComparison.OrdinalIgnoreCase) ||
                   name.EndsWith("_r", StringComparison.OrdinalIgnoreCase);
        }

        private static string InferLeftArmName(string rightArmName)
        {
            if (string.IsNullOrWhiteSpace(rightArmName))
            {
                return string.Empty;
            }

            if (rightArmName.EndsWith(".r", StringComparison.OrdinalIgnoreCase))
            {
                return rightArmName.Substring(0, rightArmName.Length - 2) + ".l";
            }

            if (rightArmName.EndsWith("_r", StringComparison.OrdinalIgnoreCase))
            {
                return rightArmName.Substring(0, rightArmName.Length - 2) + "_l";
            }

            return string.Empty;
        }

        private static bool IsUnderArmVariant(Transform transform)
        {
            Transform cursor = transform;
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

        private static int GetChildIndentStep(Transform parent, Transform child)
        {
            if (parent == null || child == null)
            {
                return 1;
            }

            if (IsFingerChainContinuation(parent.name, child.name))
            {
                return 0;
            }

            return 1;
        }

        private static bool IsFingerChainContinuation(string parentName, string childName)
        {
            if (!TryParseFingerBoneName(parentName, out string parentFamily, out string parentSide, out int parentIndex))
            {
                return false;
            }

            if (!TryParseFingerBoneName(childName, out string childFamily, out string childSide, out int childIndex))
            {
                return false;
            }

            if (!string.Equals(parentFamily, childFamily, StringComparison.Ordinal) ||
                !string.Equals(parentSide, childSide, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return childIndex == parentIndex + 1;
        }

        private static bool TryParseFingerBoneName(string name, out string family, out string side, out int index)
        {
            family = null;
            side = null;
            index = 0;
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            string[] parts = name.Split('.');
            if (parts.Length != 3)
            {
                return false;
            }

            family = parts[0];
            side = parts[2];
            if (!IsFingerFamily(family))
            {
                return false;
            }

            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out index))
            {
                return false;
            }

            return index > 0;
        }

        private static bool IsFingerFamily(string family)
        {
            return string.Equals(family, "Thumb", StringComparison.Ordinal) ||
                   string.Equals(family, "Index", StringComparison.Ordinal) ||
                   string.Equals(family, "Middle", StringComparison.Ordinal) ||
                   string.Equals(family, "Ring", StringComparison.Ordinal) ||
                   string.Equals(family, "Pinky", StringComparison.Ordinal);
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

        private bool GetExpanded(int instanceId, bool defaultValue)
        {
            if (_expandedByInstanceId.TryGetValue(instanceId, out bool expanded))
            {
                return expanded;
            }

            _expandedByInstanceId[instanceId] = defaultValue;
            return defaultValue;
        }

        private static bool GetDefaultExpanded(int depth)
        {
            return depth <= 0;
        }

        private void SetExpansionRecursive(Transform root, bool expanded)
        {
            if (root == null)
            {
                return;
            }

            _expandedByInstanceId[root.GetInstanceID()] = expanded;
            for (int i = 0; i < root.childCount; i++)
            {
                SetExpansionRecursive(root.GetChild(i), expanded);
            }
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return Mathf.Abs(a.x - b.x) < 0.001f &&
                   Mathf.Abs(a.y - b.y) < 0.001f &&
                   Mathf.Abs(a.z - b.z) < 0.001f;
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

        private static string GetRelativePath(Transform root, Transform target)
        {
            if (root == null || target == null)
            {
                return string.Empty;
            }

            List<string> parts = new List<string>(8);
            Transform cursor = target;
            while (cursor != null && cursor != root)
            {
                parts.Add(cursor.name);
                cursor = cursor.parent;
            }

            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        private static Vector3 NormalizeEulerAngles(Vector3 euler)
        {
            return new Vector3(NormalizeAngle(euler.x), NormalizeAngle(euler.y), NormalizeAngle(euler.z));
        }

        private static Vector3 NormalizeVectorZero(Vector3 value)
        {
            if (Mathf.Abs(value.x) < 0.0001f)
            {
                value.x = 0f;
            }

            if (Mathf.Abs(value.y) < 0.0001f)
            {
                value.y = 0f;
            }

            if (Mathf.Abs(value.z) < 0.0001f)
            {
                value.z = 0f;
            }

            return value;
        }

        private static Vector3 ClampScaleVector(Vector3 value)
        {
            value.x = Mathf.Clamp(value.x, ScaleSliderMin, ScaleSliderMax);
            value.y = Mathf.Clamp(value.y, ScaleSliderMin, ScaleSliderMax);
            value.z = Mathf.Clamp(value.z, ScaleSliderMin, ScaleSliderMax);
            return value;
        }

        private static float NormalizeAngle(float angle)
        {
            float normalized = Mathf.Repeat(angle + 180f, 360f) - 180f;
            if (normalized > 179.999f)
            {
                normalized = -180f;
            }

            return normalized;
        }

        private static void SetRenderersEnabled(Transform root, bool enabled)
        {
            if (root == null)
            {
                return;
            }

            MeshRenderer[] meshRenderers = root.GetComponentsInChildren<MeshRenderer>(true);
            for (int i = 0; i < meshRenderers.Length; i++)
            {
                MeshRenderer renderer = meshRenderers[i];
                if (renderer != null)
                {
                    renderer.enabled = enabled;
                }
            }

            SkinnedMeshRenderer[] skinnedRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinnedRenderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = skinnedRenderers[i];
                if (renderer != null)
                {
                    renderer.enabled = enabled;
                    renderer.updateWhenOffscreen = true;
                }
            }
        }
    }
}
