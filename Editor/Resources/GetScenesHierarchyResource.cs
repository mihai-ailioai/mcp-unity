using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor.SceneManagement;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;

namespace McpUnity.Resources
{
    /// <summary>
    /// Resource for retrieving all game objects in the Unity scenes hierarchy.
    /// When in Prefab Mode, returns the prefab stage contents instead of scene hierarchy.
    /// </summary>
    public class GetScenesHierarchyResource : McpResourceBase
    {
        public GetScenesHierarchyResource()
        {
            Name = "get_scenes_hierarchy";
            Description = "Retrieves all game objects in the Unity loaded scenes with their active state";
            Uri = "unity://scenes_hierarchy";
        }
        
        /// <summary>
        /// Fetch all game objects in the Unity loaded scenes, or prefab stage contents if in Prefab Mode
        /// </summary>
        /// <param name="parameters">Resource parameters as a JObject (not used)</param>
        /// <returns>A JObject containing the hierarchy of game objects</returns>
        public override JObject Fetch(JObject parameters)
        {
            // Check if we're in Prefab Mode — if so, return prefab stage contents
            var prefabStage = PrefabStageUtils.GetCurrentPrefabStage();
            if (prefabStage != null)
            {
                JArray prefabHierarchy = GetPrefabStageHierarchy(prefabStage);
                return new JObject
                {
                    ["success"] = true,
                    ["message"] = $"In Prefab Mode: editing '{prefabStage.prefabContentsRoot.name}'",
                    ["isPrefabStage"] = true,
                    ["prefabAssetPath"] = prefabStage.assetPath,
                    ["hierarchy"] = prefabHierarchy
                };
            }

            // Normal scene hierarchy
            JArray hierarchyArray = GetSceneHierarchy();
                
            return new JObject
            {
                ["success"] = true,
                ["message"] = $"Retrieved hierarchy with {hierarchyArray.Count} root objects",
                ["hierarchy"] = hierarchyArray
            };
        }

        /// <summary>
        /// Get the hierarchy of the prefab currently open in Prefab Mode
        /// </summary>
        private JArray GetPrefabStageHierarchy(PrefabStage prefabStage)
        {
            JArray rootArray = new JArray();

            GameObject prefabRoot = prefabStage.prefabContentsRoot;
            if (prefabRoot != null)
            {
                rootArray.Add(GetGameObjectResource.GameObjectToJObject(prefabRoot, false));
            }

            return rootArray;
        }
        
        /// <summary>
        /// Get all game objects in the Unity loaded scenes
        /// </summary>
        /// <returns>A JArray containing the hierarchy of game objects</returns>
        private JArray GetSceneHierarchy()
        {
            JArray rootObjectsArray = new JArray();
            
            // Get all loaded scenes
            int sceneCount = SceneManager.loadedSceneCount;
            for (int i = 0; i < sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                
                // Create a scene object
                JObject sceneObject = new JObject
                {
                    ["name"] = scene.name,
                    ["path"] = scene.path,
                    ["buildIndex"] = scene.buildIndex,
                    ["isDirty"] = scene.isDirty,
                    ["rootObjects"] = new JArray()
                };
                
                // Get root game objects in the scene
                GameObject[] rootObjects = scene.GetRootGameObjects();
                JArray rootObjectsInScene = (JArray)sceneObject["rootObjects"];
                
                foreach (GameObject rootObject in rootObjects)
                {
                    rootObjectsInScene.Add(GetGameObjectResource.GameObjectToJObject(rootObject, false));
                }
                
                rootObjectsArray.Add(sceneObject);
            }
            
            return rootObjectsArray;
        }
    }
}
