using System.Reflection;
using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Game;
using UnityEngine;

namespace MineMogul.ToolImportApi.Core
{
    internal sealed class RegisteredTool
    {
        public string FullToolId;
        public ToolRegistrationRequest Request;
        public Assembly SourceAssembly;
        public int SavableId;
        public GameObject TemplateObject;
        public RuntimeImportedTool TemplateTool;
        public Sprite Icon;
    }

    internal sealed class ToolBuildRequest
    {
        public string FullToolId;
        public string CacheNamespace;
        public ToolRegistrationRequest Request;
        public Assembly SourceAssembly;
        public int SavableId;
        public Sprite Icon;
        public byte[] WorldModelBytes;
        public byte[] ViewModelBytes;
    }

    internal sealed class ToolBuildResult
    {
        public bool Success;
        public string Error;
        public RegisteredTool Tool;
    }

    internal sealed class PendingRegistration
    {
        public ToolRegistrationRequest Request;
        public Assembly SourceAssembly;
        public string FullToolId;
    }
}
