using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Logging;
using UnityEngine;

namespace MineMogul.ToolImportApi.Loaders
{
    internal sealed class ReflectionModMogulModelLoader
    {
        private readonly ManualLogSource _logger;
        private readonly string _cacheDirectory;
        private readonly MethodInfo _loadModelToGameObjectAsyncMethod;
        private readonly object _opaqueMaterialEnumValue;

        public ReflectionModMogulModelLoader(string cacheDirectory, ManualLogSource logger)
        {
            _logger = logger;
            _cacheDirectory = cacheDirectory;
            Directory.CreateDirectory(_cacheDirectory);

            Type modelMakerType = Type.GetType("ModMogul.ModelMaker, ModMogul");
            if (modelMakerType == null)
            {
                return;
            }

            Type materialType = modelMakerType.GetNestedType("MaterialType", BindingFlags.Public);
            if (materialType != null)
            {
                try
                {
                    _opaqueMaterialEnumValue = Enum.Parse(materialType, "Opaque");
                }
                catch
                {
                    _opaqueMaterialEnumValue = null;
                }
            }

            MethodInfo[] methods = modelMakerType.GetMethods(BindingFlags.Public | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                if (!string.Equals(methods[i].Name, "LoadModelToGameObjectAsync", StringComparison.Ordinal))
                {
                    continue;
                }

                ParameterInfo[] parameters = methods[i].GetParameters();
                if (parameters.Length != 5)
                {
                    continue;
                }

                if (parameters[0].ParameterType == typeof(string) &&
                    parameters[1].ParameterType == typeof(Transform) &&
                    parameters[3].ParameterType == typeof(string) &&
                    parameters[4].ParameterType == typeof(CancellationToken))
                {
                    _loadModelToGameObjectAsyncMethod = methods[i];
                    break;
                }
            }
        }

        public bool IsAvailable => _loadModelToGameObjectAsyncMethod != null && _opaqueMaterialEnumValue != null;
        public string LoaderName => "ModMogul.ModelMaker (reflection)";

        public IEnumerator LoadModel(byte[] glbBytes, string cacheNamespace, string cacheKey, Transform parent, Action<GameObject, string> onComplete)
        {
            if (onComplete == null)
            {
                yield break;
            }

            if (parent == null)
            {
                onComplete(null, "Model parent transform is null.");
                yield break;
            }

            if (glbBytes == null || glbBytes.Length == 0)
            {
                onComplete(null, "GLB byte array is empty.");
                yield break;
            }

            if (!IsAvailable)
            {
                onComplete(null, "No runtime GLB importer available. Install ModMogul.");
                yield break;
            }

            string namespacedCacheDirectory = GetNamespacedCacheDirectory(cacheNamespace);
            string filePath = Path.Combine(namespacedCacheDirectory, SanitizeFileName(cacheKey) + ".glb");
            try
            {
                File.WriteAllBytes(filePath, glbBytes);
            }
            catch (Exception ex)
            {
                onComplete(null, "Failed to cache GLB to disk: " + ex.Message);
                yield break;
            }

            Task loadTask;
            try
            {
                object taskObject = _loadModelToGameObjectAsyncMethod.Invoke(
                    null,
                    new object[]
                    {
                        filePath,
                        parent,
                        _opaqueMaterialEnumValue,
                        Path.GetFileNameWithoutExtension(filePath),
                        default(CancellationToken)
                    });

                loadTask = taskObject as Task;
            }
            catch (Exception ex)
            {
                onComplete(null, "ModelMaker.LoadModelToGameObjectAsync invocation failed: " + ex.Message);
                yield break;
            }

            if (loadTask == null)
            {
                onComplete(null, "ModelMaker.LoadModelToGameObjectAsync returned null task.");
                yield break;
            }

            while (!loadTask.IsCompleted)
            {
                yield return null;
            }

            if (loadTask.IsFaulted)
            {
                string error = loadTask.Exception != null ? loadTask.Exception.ToString() : "Unknown task fault.";
                onComplete(null, "GLB import task faulted: " + error);
                yield break;
            }

            GameObject loadedRoot = GetTaskResultAsGameObject(loadTask);
            if (loadedRoot == null)
            {
                onComplete(null, "GLB import completed but returned null GameObject.");
                yield break;
            }

            loadedRoot.transform.localPosition = Vector3.zero;
            loadedRoot.transform.localRotation = Quaternion.identity;
            loadedRoot.transform.localScale = Vector3.one;
            onComplete(loadedRoot, null);
        }

        private static GameObject GetTaskResultAsGameObject(Task task)
        {
            PropertyInfo resultProperty = task.GetType().GetProperty("Result", BindingFlags.Instance | BindingFlags.Public);
            if (resultProperty == null)
            {
                return null;
            }

            return resultProperty.GetValue(task, null) as GameObject;
        }

        private static string SanitizeFileName(string key)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return "model";
            }

            char[] invalid = Path.GetInvalidFileNameChars();
            char[] chars = key.ToCharArray();
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
            string segment = SanitizeFolderName(cacheNamespace);
            string path = Path.Combine(_cacheDirectory, segment);
            Directory.CreateDirectory(path);
            return path;
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
    }
}
