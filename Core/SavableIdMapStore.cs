using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Logging;

namespace MineMogul.ToolImportApi.Core
{
    internal sealed class SavableIdMapStore
    {
        private readonly string _path;
        private readonly ManualLogSource _logger;
        private readonly Dictionary<string, int> _idByTool =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public SavableIdMapStore(string path, ManualLogSource logger)
        {
            _path = path;
            _logger = logger;
            Load();
        }

        public bool TryGet(string fullToolId, out int savableId)
        {
            savableId = 0;
            if (string.IsNullOrWhiteSpace(fullToolId))
            {
                return false;
            }

            lock (_gate)
            {
                return _idByTool.TryGetValue(fullToolId, out savableId);
            }
        }

        public int CountMappings()
        {
            lock (_gate)
            {
                return _idByTool.Count;
            }
        }

        public bool IsMappedToAnotherTool(int savableId, string fullToolId)
        {
            lock (_gate)
            {
                foreach (KeyValuePair<string, int> pair in _idByTool)
                {
                    if (pair.Value != savableId)
                    {
                        continue;
                    }

                    if (!string.Equals(pair.Key, fullToolId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public void Set(string fullToolId, int savableId)
        {
            if (string.IsNullOrWhiteSpace(fullToolId) || savableId <= 0)
            {
                return;
            }

            lock (_gate)
            {
                _idByTool[fullToolId] = savableId;
            }

            Save();
        }

        private void Load()
        {
            if (string.IsNullOrWhiteSpace(_path) || !File.Exists(_path))
            {
                return;
            }

            try
            {
                string[] lines = File.ReadAllLines(_path);
                lock (_gate)
                {
                    _idByTool.Clear();
                    for (int i = 0; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (string.IsNullOrWhiteSpace(line))
                        {
                            continue;
                        }

                        int split = line.IndexOf('=');
                        if (split <= 0 || split >= line.Length - 1)
                        {
                            continue;
                        }

                        string fullToolId = line.Substring(0, split).Trim();
                        string savableText = line.Substring(split + 1).Trim();
                        if (string.IsNullOrWhiteSpace(fullToolId))
                        {
                            continue;
                        }

                        if (!int.TryParse(savableText, out int savableId) || savableId <= 0)
                        {
                            continue;
                        }

                        _idByTool[fullToolId] = savableId;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[ToolImportApi] Failed to load savable id map: " + ex.Message);
            }
        }

        private void Save()
        {
            if (string.IsNullOrWhiteSpace(_path))
            {
                return;
            }

            try
            {
                string dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                List<string> lines = new List<string>(64);
                lock (_gate)
                {
                    foreach (KeyValuePair<string, int> pair in _idByTool)
                    {
                        lines.Add(pair.Key + "=" + pair.Value);
                    }
                }

                string temp = _path + ".tmp";
                File.WriteAllLines(temp, lines.ToArray());
                if (File.Exists(_path))
                {
                    File.Delete(_path);
                }

                File.Move(temp, _path);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("[ToolImportApi] Failed to save savable id map: " + ex.Message);
            }
        }
    }
}
