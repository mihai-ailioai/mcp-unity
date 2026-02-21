using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;

namespace McpUnity.Resources
{
    /// <summary>
    /// Resource for getting the current editor selection state.
    /// Returns the active GameObject, all selected GameObjects, and selected assets.
    /// </summary>
    public class GetSelectionResource : McpResourceBase
    {
        public GetSelectionResource()
        {
            Name = "get_selection";
            Description = "Retrieves the current Unity Editor selection (active object, selected GameObjects, selected assets)";
            Uri = "unity://selection";
        }

        public override JObject Fetch(JObject parameters)
        {
            JObject activeGameObject = null;
            GameObject activeGo = Selection.activeGameObject;
            if (activeGo != null)
            {
                activeGameObject = new JObject
                {
                    ["name"] = activeGo.name,
                    ["instanceId"] = activeGo.GetInstanceID(),
                    ["path"] = GameObjectToolUtils.GetGameObjectPath(activeGo)
                };
            }

            // All selected GameObjects (scene objects)
            JArray selectedGameObjects = new JArray();
            foreach (GameObject go in Selection.gameObjects)
            {
                selectedGameObjects.Add(new JObject
                {
                    ["name"] = go.name,
                    ["instanceId"] = go.GetInstanceID(),
                    ["path"] = GameObjectToolUtils.GetGameObjectPath(go)
                });
            }

            // Selected assets in the Project window
            JArray selectedAssets = new JArray();
            string[] guids = Selection.assetGUIDs;
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                    continue;

                Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                selectedAssets.Add(new JObject
                {
                    ["name"] = asset != null ? asset.name : System.IO.Path.GetFileNameWithoutExtension(assetPath),
                    ["path"] = assetPath,
                    ["guid"] = guid,
                    ["type"] = asset != null ? asset.GetType().Name : "Unknown"
                });
            }

            return new JObject
            {
                ["success"] = true,
                ["message"] = $"Selection: {selectedGameObjects.Count} GameObject(s), {selectedAssets.Count} asset(s)",
                ["activeGameObject"] = activeGameObject != null ? activeGameObject : JValue.CreateNull(),
                ["selectedGameObjects"] = selectedGameObjects,
                ["selectedAssets"] = selectedAssets
            };
        }
    }
}
