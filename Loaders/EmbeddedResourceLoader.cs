using System;
using System.IO;
using System.Reflection;
using System.Text;

namespace MineMogul.ToolImportApi.Loaders
{
    internal static class EmbeddedResourceLoader
    {
        public static bool TryReadAllBytes(Assembly assembly, string resourceName, out byte[] bytes, out string error)
        {
            bytes = null;
            error = null;

            if (assembly == null)
            {
                error = "Source assembly is null.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(resourceName))
            {
                error = "Resource name is empty.";
                return false;
            }

            string resolvedName = ResolveManifestName(assembly, resourceName);
            if (resolvedName == null)
            {
                error = BuildMissingResourceMessage(assembly, resourceName);
                return false;
            }

            Stream stream = assembly.GetManifestResourceStream(resolvedName);
            if (stream == null)
            {
                error = "Manifest resource stream could not be opened: " + resolvedName;
                return false;
            }

            using (stream)
            {
                using (MemoryStream ms = new MemoryStream())
                {
                    stream.CopyTo(ms);
                    bytes = ms.ToArray();
                }
            }

            return true;
        }

        public static bool TryExtractToFile(Assembly assembly, string resourceName, string outputPath, out string error)
        {
            error = null;
            if (!TryReadAllBytes(assembly, resourceName, out byte[] bytes, out error))
            {
                return false;
            }

            try
            {
                string dir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                File.WriteAllBytes(outputPath, bytes);
                return true;
            }
            catch (Exception ex)
            {
                error = "Failed to write resource to '" + outputPath + "': " + ex.Message;
                return false;
            }
        }

        private static string ResolveManifestName(Assembly assembly, string requestedName)
        {
            string[] names = assembly.GetManifestResourceNames();
            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], requestedName, StringComparison.Ordinal))
                {
                    return names[i];
                }
            }

            for (int i = 0; i < names.Length; i++)
            {
                if (string.Equals(names[i], requestedName, StringComparison.OrdinalIgnoreCase))
                {
                    return names[i];
                }
            }

            return null;
        }

        private static string BuildMissingResourceMessage(Assembly assembly, string requestedName)
        {
            string[] names = assembly.GetManifestResourceNames();
            StringBuilder sb = new StringBuilder(256);
            sb.Append("Embedded resource not found: '");
            sb.Append(requestedName);
            sb.Append("'.");

            string token = requestedName;
            int split = requestedName.LastIndexOf('.');
            if (split > 0 && split < requestedName.Length - 1)
            {
                token = requestedName.Substring(split + 1);
            }

            int appended = 0;
            for (int i = 0; i < names.Length && appended < 8; i++)
            {
                if (names[i].IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (appended == 0)
                    {
                        sb.Append(" Similar resources: ");
                    }
                    else
                    {
                        sb.Append(", ");
                    }

                    sb.Append(names[i]);
                    appended++;
                }
            }

            if (appended == 0 && names.Length > 0)
            {
                sb.Append(" Assembly has ");
                sb.Append(names.Length);
                sb.Append(" embedded resources.");
            }

            return sb.ToString();
        }
    }
}
