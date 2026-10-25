using HarmonyLib;
using MineMogul.ToolImportApi.Core;
using UnityEngine;

namespace MineMogul.ToolImportApi.Integration
{
    [HarmonyPatch(typeof(PlayerInventory), "TryAddToInventory")]
    internal static class ToolImportToolBuilderInventoryConvertPatch
    {
        [System.ThreadStatic]
        private static bool _isConvertingFromPickup;

        private static bool Prefix(BaseHeldTool tool, ref bool __result)
        {
            if (tool == null)
            {
                return true;
            }

            // Allow runtime imported tools to flow through normal inventory add.
            if (tool is MineMogul.ToolImportApi.Game.RuntimeImportedTool)
            {
                return true;
            }

            // Prevent re-entrancy while we are injecting converted runtime tools.
            if (_isConvertingFromPickup)
            {
                return true;
            }

            if (!TryResolveIncomingTool(tool, out string fullToolId, out int resolvedSavableId))
            {
                return true;
            }

            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return true;
            }

            int requestedCount = Mathf.Max(1, tool.Quantity);
            int convertedCount = 0;
            string lastMessage = string.Empty;
            _isConvertingFromPickup = true;
            try
            {
                for (int i = 0; i < requestedCount; i++)
                {
                    if (!registry.TryGiveToolFromPickup(fullToolId, out string message))
                    {
                        lastMessage = message;
                        break;
                    }

                    convertedCount++;
                }
            }
            finally
            {
                _isConvertingFromPickup = false;
            }

            if (convertedCount <= 0)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " ToolBuilder->Runtime conversion failed for '" + fullToolId +
                    "' (resolvedObjectId=" + resolvedSavableId + ", incomingType=" + tool.GetType().Name + "): " + lastMessage);
                return true;
            }

            int remaining = requestedCount - convertedCount;
            if (remaining > 0)
            {
                tool.Quantity = remaining;
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " ToolBuilder->Runtime conversion partial for '" + fullToolId +
                    "': converted=" + convertedCount + ", remainingIncomingQuantity=" + remaining +
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
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " ToolBuilder->Runtime conversion complete for '" + fullToolId +
                    "': converted=" + convertedCount + ", resolvedObjectId=" + resolvedSavableId +
                    ", incomingType=" + tool.GetType().Name + ".");
            }
            return false;
        }

        private static bool TryResolveIncomingTool(BaseHeldTool tool, out string fullToolId, out int resolvedSavableId)
        {
            fullToolId = null;
            resolvedSavableId = 0;

            if (tool == null)
            {
                return false;
            }

            if (TryResolveMappedSavableId((int)tool.SavableObjectID, out fullToolId, out resolvedSavableId))
            {
                return true;
            }

            if (!(tool is ToolBuilder toolBuilder) || toolBuilder.Definition == null)
            {
                return false;
            }

            BuildingInventoryDefinition definition = toolBuilder.Definition;
            BuildingObject mainPrefab = definition.GetMainPrefab();
            if (mainPrefab != null &&
                TryResolveMappedSavableId((int)mainPrefab.SavableObjectID, out fullToolId, out resolvedSavableId))
            {
                return true;
            }

            if (definition.PackedPrefab != null &&
                TryResolveMappedSavableId((int)definition.PackedPrefab.SavableObjectID, out fullToolId, out resolvedSavableId))
            {
                return true;
            }

            if (definition.BuildingPrefabs == null)
            {
                return false;
            }

            for (int i = 0; i < definition.BuildingPrefabs.Count; i++)
            {
                BuildingObject prefab = definition.BuildingPrefabs[i];
                if (prefab == null)
                {
                    continue;
                }

                if (TryResolveMappedSavableId((int)prefab.SavableObjectID, out fullToolId, out resolvedSavableId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveMappedSavableId(int savableObjectId, out string fullToolId, out int resolvedSavableId)
        {
            fullToolId = null;
            resolvedSavableId = 0;
            if (savableObjectId <= 0)
            {
                return false;
            }

            if (!ToolImportPickupMarker.TryResolveFullToolId(savableObjectId, out fullToolId))
            {
                return false;
            }

            resolvedSavableId = savableObjectId;
            return true;
        }
    }

    [HarmonyPatch(typeof(ComputerShopUI), "TrySpawnItem")]
    internal static class ToolImportComputerShopSpawnPatch
    {
        private static bool Prefix(ShopItemDefinition item, int quantity, ref bool __result)
        {
            if (item == null || item.BuildingInventoryDefinition == null)
            {
                return true;
            }

            if (!TryResolveMappedShopItem(item.BuildingInventoryDefinition, out string fullToolId, out int resolvedSavableId))
            {
                return true;
            }

            ToolImportRegistry registry = ToolImportRuntime.Registry;
            if (registry == null)
            {
                return true;
            }

            ShopSpawnPoint spawnPoint = ShopSpawnPoint.GetRandomItemSpawnPoint();
            if (spawnPoint == null)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Shop direct tool spawn fallback for '" + fullToolId +
                    "': random spawn point unavailable.");
                return true;
            }

            int spawnQuantity = Mathf.Max(1, quantity);
            if (!registry.TrySpawnToolFromShop(fullToolId, spawnPoint.transform.position, spawnPoint.transform.rotation, spawnQuantity, out string message))
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogWarning(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Shop direct tool spawn failed for '" + fullToolId +
                    "' (resolvedObjectId=" + resolvedSavableId + "): " + message + ". Falling back to crate spawn.");
                return true;
            }

            if (ToolImportApi.Plugin.ToolImportApiPlugin.VerboseLogEnabled)
            {
                ToolImportApi.Plugin.ToolImportApiPlugin.LogInfo(
                    MineMogul.ToolImportApi.Plugin.ModInfo.LOG_PREFIX + " Shop direct tool spawn complete for '" + fullToolId +
                    "' (resolvedObjectId=" + resolvedSavableId + ", quantity=" + spawnQuantity + ").");
            }

            __result = true;
            return false;
        }

        private static bool TryResolveMappedShopItem(BuildingInventoryDefinition definition, out string fullToolId, out int resolvedSavableId)
        {
            fullToolId = null;
            resolvedSavableId = 0;
            if (definition == null)
            {
                return false;
            }

            BuildingObject mainPrefab = definition.GetMainPrefab();
            if (mainPrefab != null &&
                TryResolveMappedSavableId((int)mainPrefab.SavableObjectID, out fullToolId, out resolvedSavableId))
            {
                return true;
            }

            if (definition.PackedPrefab != null &&
                TryResolveMappedSavableId((int)definition.PackedPrefab.SavableObjectID, out fullToolId, out resolvedSavableId))
            {
                return true;
            }

            if (definition.BuildingPrefabs == null)
            {
                return false;
            }

            for (int i = 0; i < definition.BuildingPrefabs.Count; i++)
            {
                BuildingObject prefab = definition.BuildingPrefabs[i];
                if (prefab == null)
                {
                    continue;
                }

                if (TryResolveMappedSavableId((int)prefab.SavableObjectID, out fullToolId, out resolvedSavableId))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool TryResolveMappedSavableId(int savableObjectId, out string fullToolId, out int resolvedSavableId)
        {
            fullToolId = null;
            resolvedSavableId = 0;
            if (savableObjectId <= 0)
            {
                return false;
            }

            if (!ToolImportPickupMarker.TryResolveFullToolId(savableObjectId, out fullToolId))
            {
                return false;
            }

            resolvedSavableId = savableObjectId;
            return true;
        }
    }
}
