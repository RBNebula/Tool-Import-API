using HarmonyLib;
using MineMogul.ToolImportApi.Core;
using UnityEngine;

namespace MineMogul.ToolImportApi.Integration
{
    [HarmonyPatch(typeof(PlayerInventory), "TryAddToInventory")]
    internal static class ToolImportToolBuilderInventoryConvertPatch
    {
        private static bool Prefix(BaseHeldTool tool, ref bool __result)
        {
            if (!(tool is ToolBuilder toolBuilder) || toolBuilder.Definition == null)
            {
                return true;
            }

            BuildingObject mainPrefab = toolBuilder.Definition.GetMainPrefab();
            if (mainPrefab == null)
            {
                return true;
            }

            if (!ToolImportPickupMarker.TryResolveFullToolId((int)mainPrefab.SavableObjectID, out string fullToolId))
            {
                return true;
            }

            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return true;
            }

            int requestedCount = Mathf.Max(1, toolBuilder.Quantity);
            int convertedCount = 0;
            string lastMessage = string.Empty;
            for (int i = 0; i < requestedCount; i++)
            {
                if (!registry.TryGiveToolFromPickup(fullToolId, out string message))
                {
                    lastMessage = message;
                    break;
                }

                convertedCount++;
            }

            if (convertedCount <= 0)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    "[ToolImportApi] ToolBuilder->Runtime conversion failed for '" + fullToolId +
                    "' (buildObjectId=" + (int)mainPrefab.SavableObjectID + "): " + lastMessage);
                return true;
            }

            int remaining = requestedCount - convertedCount;
            if (remaining > 0)
            {
                toolBuilder.Quantity = remaining;
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    "[ToolImportApi] ToolBuilder->Runtime conversion partial for '" + fullToolId +
                    "': converted=" + convertedCount + ", remainingBuilderQuantity=" + remaining +
                    ". LastError=" + lastMessage);
                return true;
            }

            __result = true;
            if (tool != null && tool.gameObject != null)
            {
                Object.Destroy(tool.gameObject);
            }

            if (ToolImportApi.Plugin.ToolImportApiPlugin.VerboseLogEnabled)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogInfo(
                    "[ToolImportApi] ToolBuilder->Runtime conversion complete for '" + fullToolId +
                    "': converted=" + convertedCount + ", buildObjectId=" + (int)mainPrefab.SavableObjectID + ".");
            }
            return false;
        }
    }
}
