using McpUnity.Unity;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for reading the current Unity Editor selection.
    /// Returns the active GameObject, all selected GameObjects, and selected assets.
    /// </summary>
    public class GetSelectionTool : McpToolBase
    {
        public GetSelectionTool()
        {
            Name = "get_selection";
            Description = "Returns the current Unity Editor selection: active GameObject, all selected GameObjects in the scene, and selected assets in the Project window. Use this to see what the user is looking at or has selected.";
        }

        public override JObject Execute(JObject parameters)
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
                ["type"] = "text",
                ["message"] = $"Selection: {selectedGameObjects.Count} GameObject(s), {selectedAssets.Count} asset(s)",
                ["activeGameObject"] = activeGameObject != null ? activeGameObject : JValue.CreateNull(),
                ["selectedGameObjects"] = selectedGameObjects,
                ["selectedAssets"] = selectedAssets
            };
        }
    }
}
