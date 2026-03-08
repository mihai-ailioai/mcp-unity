using System;
using System.Collections;
using System.Collections.Generic;
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
    /// Supports paginated collection of prefab/scene documents via offset/limit params
    /// to avoid exceeding WebSocket payload limits on large projects.
    /// </summary>
    public class CollectProjectAssetsTool : McpToolBase
    {
        public const string PrefabDocumentType = "prefab";
        public const string SceneDocumentType = "scene";
        public const int DefaultPageSize = 100;

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
            Description = "Collects Unity scripts, prefabs, and optional scenes as documents for context indexing. Supports pagination via offset/limit for large projects.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteCollectCoroutine(parameters, tcs));
        }

        private static IEnumerator ExecuteCollectCoroutine(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            // Pagination params for prefab/scene documents
            int offset = parameters["offset"]?.ToObject<int>() ?? 0;
            int limit = parameters["limit"]?.ToObject<int>() ?? DefaultPageSize;

            // Always use editor settings — folders and scene inclusion are configured in the Context Engine tab
            var settings = McpUnitySettings.Instance;
            bool includeScenes = settings.ContextEngineIndexScenes;
            List<string> folders = new List<string>(settings.ContextEngineIndexFolders);

            yield return null;

            try
            {
                List<string> searchFolders = ResolveSearchFolders(folders, out List<string> invalidFolders);
                if (searchFolders.Count == 0)
                {
                    string errorMessage = invalidFolders.Count > 0
                        ? $"No valid folders found. Invalid: {string.Join(", ", invalidFolders)}"
                        : "No valid folders found.";
                    EditorUtility.ClearProgressBar();
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(errorMessage, "validation_error"));
                    yield break;
                }

                if (invalidFolders.Count > 0)
                {
                    McpLogger.LogWarning($"[Context Engine] Skipping invalid folders: {string.Join(", ", invalidFolders)}");
                }

                // Scripts: only collect paths (lightweight strings) — always returned in full on first page
                List<string> scriptPaths = offset == 0
                    ? CollectScriptPaths(searchFolders)
                    : new List<string>();

                // Discover all prefab/scene asset GUIDs to determine total count
                var assetGuids = new List<AssetGuidEntry>();
                DiscoverPrefabGuids(searchFolders, assetGuids);
                if (includeScenes)
                {
                    DiscoverSceneGuids(searchFolders, assetGuids);
                }

                int totalDocuments = assetGuids.Count;

                // Paginate: only process the requested slice
                int endIndex = Math.Min(offset + limit, totalDocuments);
                var documents = new List<CollectedDocument>();

                for (int i = offset; i < endIndex; i++)
                {
                    var entry = assetGuids[i];

                    EditorUtility.DisplayProgressBar(
                        "Context Engine — Collecting Assets",
                        $"{entry.AssetPath}  ({i + 1}/{totalDocuments})",
                        (float)(i + 1) / totalDocuments);

                    try
                    {
                        if (entry.Type == PrefabDocumentType)
                        {
                            var doc = ProcessPrefab(entry.AssetPath);
                            if (doc != null) documents.Add(doc);
                        }
                        else if (entry.Type == SceneDocumentType)
                        {
                            var doc = ProcessScene(entry.AssetPath);
                            if (doc != null) documents.Add(doc);
                        }
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"[Context Engine] Failed to process {entry.Type} {entry.AssetPath}: {ex.Message}");
                    }
                }

                EditorUtility.DisplayProgressBar(
                    "Context Engine — Preparing response",
                    $"Page offset={offset} limit={limit}: {documents.Count} documents (total: {totalDocuments})",
                    1f);

                var responseScriptPaths = new JArray();
                foreach (string path in scriptPaths)
                {
                    responseScriptPaths.Add(path);
                }

                var responseDocuments = new JArray();
                foreach (CollectedDocument document in documents)
                {
                    responseDocuments.Add(document.ToResponseJObject());
                }

                JObject response = new JObject
                {
                    ["success"] = true,
                    ["scriptPaths"] = responseScriptPaths,
                    ["documents"] = responseDocuments,
                    ["totalDocuments"] = totalDocuments,
                    ["offset"] = offset,
                    ["limit"] = limit,
                };

                if (invalidFolders.Count > 0)
                {
                    response["message"] = $"Skipped invalid folders: {string.Join(", ", invalidFolders)}";
                }

                EditorUtility.ClearProgressBar();
                tcs.SetResult(response);
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                McpLogger.LogError($"[Context Engine] Failed to collect project assets: {ex.Message}");
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to collect project assets: {ex.Message}",
                    "execution_error"
                ));
            }
        }

        // ── Asset GUID discovery (lightweight — no content loading) ──

        private struct AssetGuidEntry
        {
            public string Guid;
            public string AssetPath;
            public string Type;
        }

        private static void DiscoverPrefabGuids(List<string> searchFolders, List<AssetGuidEntry> entries)
        {
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", searchFolders.ToArray());
            foreach (string guid in guids)
            {
                if (!seen.Add(guid)) continue;
                entries.Add(new AssetGuidEntry
                {
                    Guid = guid,
                    AssetPath = AssetDatabase.GUIDToAssetPath(guid),
                    Type = PrefabDocumentType,
                });
            }
        }

        private static void DiscoverSceneGuids(List<string> searchFolders, List<AssetGuidEntry> entries)
        {
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:SceneAsset", searchFolders.ToArray());
            foreach (string guid in guids)
            {
                if (!seen.Add(guid)) continue;
                entries.Add(new AssetGuidEntry
                {
                    Guid = guid,
                    AssetPath = AssetDatabase.GUIDToAssetPath(guid),
                    Type = SceneDocumentType,
                });
            }
        }

        // ── Individual asset processing ──

        private static CollectedDocument ProcessPrefab(string assetPath)
        {
            GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            if (prefab == null) return null;

            JObject summary = GetGameObjectResource.GameObjectToSummaryJObject(prefab);
            if (summary == null) return null;

            summary["assetPath"] = assetPath;

            return new CollectedDocument
            {
                Type = PrefabDocumentType,
                Path = assetPath,
                Contents = summary.ToString(Formatting.None),
            };
        }

        private static CollectedDocument ProcessScene(string assetPath)
        {
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

                EditorSceneManager.CloseScene(scene, true);
                sceneOpened = false;

                return new CollectedDocument
                {
                    Type = SceneDocumentType,
                    Path = assetPath,
                    Contents = sceneDocument.ToString(Formatting.None),
                };
            }
            finally
            {
                if (sceneOpened && scene.IsValid() && scene.isLoaded)
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        // ── Shared helpers ──

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

        public static List<string> CollectScriptPaths(List<string> searchFolders)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>();
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", searchFolders.ToArray());

            for (int i = 0; i < guids.Length; i++)
            {
                string guid = guids[i];
                if (!seen.Add(guid))
                {
                    continue;
                }

                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (i % 50 == 0)
                {
                    EditorUtility.DisplayProgressBar(
                        "Context Engine — Collecting Scripts",
                        $"{assetPath}  ({i + 1}/{guids.Length})",
                        (float)(i + 1) / guids.Length);
                }

                paths.Add(assetPath);
            }

            return paths;
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
