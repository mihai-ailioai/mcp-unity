using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using McpUnity.Resources;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace McpUnity.Tools
{
    /// <summary>
    /// Collects project assets into path/content documents for Node-side indexing.
    /// </summary>
    public class CollectProjectAssetsTool : McpToolBase
    {
        public const string ScriptDocumentType = "script";
        public const string PrefabDocumentType = "prefab";
        public const string SceneDocumentType = "scene";

        public class CollectedDocument
        {
            public string Type;
            public string Path;
            public string Contents;

            public JObject ToResponseJObject()
            {
                return new JObject
                {
                    ["path"] = Path,
                    ["contents"] = Contents,
                };
            }
        }

        public CollectProjectAssetsTool()
        {
            Name = "collect_project_assets";
            Description = "Collects Unity scripts, prefabs, and optional scenes as documents for context indexing.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteCollectCoroutine(parameters, tcs));
        }

        private static IEnumerator ExecuteCollectCoroutine(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            bool includeScenes = parameters?["includeScenes"]?.ToObject<bool?>() ?? false;
            List<string> folders = ParseFolders(parameters?["folders"] as JArray);

            yield return null;

            try
            {
                if (!TryCollectDocuments(includeScenes, folders, out List<CollectedDocument> documents, out List<string> invalidFolders, out string errorMessage))
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(errorMessage, "validation_error"));
                    yield break;
                }

                var responseDocuments = new JArray();
                foreach (CollectedDocument document in documents)
                {
                    responseDocuments.Add(document.ToResponseJObject());
                }

                JObject response = new JObject
                {
                    ["success"] = true,
                    ["documents"] = responseDocuments,
                };

                if (invalidFolders.Count > 0)
                {
                    response["message"] = $"Skipped invalid folders: {string.Join(", ", invalidFolders)}";
                }

                tcs.SetResult(response);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[Context Engine] Failed to collect project assets: {ex.Message}");
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to collect project assets: {ex.Message}",
                    "execution_error"
                ));
            }
        }

        public static bool TryCollectDocuments(bool includeScenes, List<string> folders, out List<CollectedDocument> documents, out List<string> invalidFolders, out string errorMessage)
        {
            documents = new List<CollectedDocument>();
            errorMessage = null;

            List<string> searchFolders = ResolveSearchFolders(folders, out invalidFolders);
            if (searchFolders.Count == 0)
            {
                errorMessage = invalidFolders.Count > 0
                    ? $"No valid folders found. Invalid: {string.Join(", ", invalidFolders)}"
                    : "No valid folders found.";
                return false;
            }

            if (invalidFolders.Count > 0)
            {
                McpLogger.LogWarning($"[Context Engine] Skipping invalid folders: {string.Join(", ", invalidFolders)}");
            }

            documents.AddRange(CollectScripts(searchFolders));
            documents.AddRange(CollectPrefabs(searchFolders));

            if (includeScenes)
            {
                documents.AddRange(CollectScenes(searchFolders));
            }

            return true;
        }

        public static List<string> ResolveSearchFolders(List<string> folders, out List<string> invalidFolders)
        {
            invalidFolders = new List<string>();
            var result = new List<string>();

            if (folders == null || folders.Count == 0)
            {
                result.Add("Assets");
                return result;
            }

            foreach (string folder in folders)
            {
                string resolved = ResolveSearchFolder(folder);
                if (AssetDatabase.IsValidFolder(resolved))
                {
                    if (!result.Contains(resolved))
                    {
                        result.Add(resolved);
                    }
                }
                else
                {
                    invalidFolders.Add(resolved);
                }
            }

            return result;
        }

        public static List<CollectedDocument> CollectScripts(List<string> searchFolders)
        {
            var documents = new List<CollectedDocument>();
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", searchFolders.ToArray());

            foreach (string guid in guids)
            {
                if (!seen.Add(guid))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    string fileContents = File.ReadAllText(assetPath);
                    if (string.IsNullOrWhiteSpace(fileContents))
                    {
                        continue;
                    }

                    documents.Add(new CollectedDocument
                    {
                        Type = ScriptDocumentType,
                        Path = assetPath,
                        Contents = $"// File: {assetPath}\n{fileContents}",
                    });
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Context Engine] Failed to read script {assetPath}: {ex.Message}");
                }
            }

            return documents;
        }

        public static List<CollectedDocument> CollectPrefabs(List<string> searchFolders)
        {
            var documents = new List<CollectedDocument>();
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", searchFolders.ToArray());

            foreach (string guid in guids)
            {
                if (!seen.Add(guid))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guid);

                try
                {
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                    {
                        continue;
                    }

                    JObject summary = GetGameObjectResource.GameObjectToSummaryJObject(prefab);
                    if (summary == null)
                    {
                        continue;
                    }

                    summary["assetPath"] = assetPath;

                    documents.Add(new CollectedDocument
                    {
                        Type = PrefabDocumentType,
                        Path = assetPath,
                        Contents = summary.ToString(Formatting.None),
                    });
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Context Engine] Failed to process prefab {assetPath}: {ex.Message}");
                }
            }

            return documents;
        }

        public static List<CollectedDocument> CollectScenes(List<string> searchFolders)
        {
            var documents = new List<CollectedDocument>();
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:SceneAsset", searchFolders.ToArray());

            foreach (string guid in guids)
            {
                if (!seen.Add(guid))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                var scene = default(UnityEngine.SceneManagement.Scene);
                bool sceneOpened = false;

                try
                {
                    scene = EditorSceneManager.OpenScene(assetPath, OpenSceneMode.Additive);
                    sceneOpened = scene.IsValid();

                    JArray rootObjects = new JArray();
                    foreach (GameObject rootObject in scene.GetRootGameObjects())
                    {
                        rootObjects.Add(GetGameObjectResource.GameObjectToSummaryJObject(rootObject));
                    }

                    JObject sceneDocument = new JObject
                    {
                        ["assetPath"] = assetPath,
                        ["sceneName"] = scene.name,
                        ["rootObjects"] = rootObjects,
                    };

                    documents.Add(new CollectedDocument
                    {
                        Type = SceneDocumentType,
                        Path = assetPath,
                        Contents = sceneDocument.ToString(Formatting.None),
                    });

                    EditorSceneManager.CloseScene(scene, true);
                    sceneOpened = false;
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Context Engine] Failed to process scene {assetPath}: {ex.Message}");
                }
                finally
                {
                    if (sceneOpened && scene.IsValid() && scene.isLoaded)
                    {
                        EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }

            return documents;
        }

        private static List<string> ParseFolders(JArray foldersArray)
        {
            var folders = new List<string>();
            if (foldersArray == null)
            {
                return folders;
            }

            foreach (JToken folderToken in foldersArray)
            {
                string folder = folderToken?.ToObject<string>();
                if (folder != null)
                {
                    folders.Add(folder);
                }
            }

            return folders;
        }

        private static string ResolveSearchFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                return "Assets";
            }

            string trimmed = folder.Trim().TrimStart('/').TrimEnd('/');
            if (string.IsNullOrEmpty(trimmed))
            {
                return "Assets";
            }

            if (trimmed.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                return "Assets";
            }

            if (trimmed.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return $"Assets/{trimmed}";
        }
    }
}
