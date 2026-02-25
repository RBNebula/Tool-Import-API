using MineMogul.ToolImportApi.Api;
using MineMogul.ToolImportApi.Core;

namespace MineMogul.ToolImportApi.Integration
{
    internal interface IModMogulAdapter
    {
        bool IsAvailable { get; }
        bool TryRegisterTool(RegisteredTool tool, ToolRegistrationRequest request, out string message);
        bool TryAddToShop(RegisteredTool tool, ToolRegistrationRequest request, out string message);
        bool TrySpawnTool(RegisteredTool tool, out string message);
    }
}
