using System;
using System.Collections.Generic;

namespace MineMogul.ToolImportApi.Integration
{
    internal sealed class ToolImportPickupMarker : UnityEngine.MonoBehaviour
    {
        private static readonly Dictionary<int, string> FullToolIdBySavableId =
            new Dictionary<int, string>(64);

        public static void RegisterToolMapping(BuildingObject pickupPrefab, string fullToolId)
        {
            if (pickupPrefab == null || string.IsNullOrWhiteSpace(fullToolId))
            {
                return;
            }

            RegisterSavableIdMapping((int)pickupPrefab.SavableObjectID, fullToolId);

            BuildingInventoryDefinition definition = pickupPrefab.Definition;
            if (definition != null)
            {
                BuildingObject mainPrefab = definition.GetMainPrefab();
                if (mainPrefab != null)
                {
                    RegisterSavableIdMapping((int)mainPrefab.SavableObjectID, fullToolId);
                }

                if (definition.PackedPrefab != null)
                {
                    RegisterSavableIdMapping((int)definition.PackedPrefab.SavableObjectID, fullToolId);
                }
            }
        }

        public static void RegisterSavableIdMapping(int savableObjectId, string fullToolId)
        {
            if (savableObjectId <= 0 || string.IsNullOrWhiteSpace(fullToolId))
            {
                return;
            }

            FullToolIdBySavableId[savableObjectId] = fullToolId;
        }

        public static bool TryResolveFullToolId(int savableObjectId, out string fullToolId)
        {
            fullToolId = null;
            if (savableObjectId <= 0)
            {
                return false;
            }

            return FullToolIdBySavableId.TryGetValue(savableObjectId, out fullToolId);
        }
    }
}
