using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;

namespace MineMogul.ToolImportApi.Integration
{
    internal sealed class NullModMogulAdapter : IModMogulAdapter
    {
        public bool IsAvailable => false;

        public bool TryRegisterTool(RegisteredTool tool, ToolRegistrationRequest request, out string message)
        {
            message = "ModMogul not detected.";
            return false;
        }

        public bool TryAddToShop(RegisteredTool tool, ToolRegistrationRequest request, out string message)
        {
            message = "ModMogul not detected.";
            return false;
        }

        public bool TrySpawnTool(RegisteredTool tool, out string message)
        {
            message = "ModMogul not detected.";
            return false;
        }
    }
}
