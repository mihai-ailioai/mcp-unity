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

        /// <summary>
        /// Max total content size per page in bytes (~4 MB).
        /// Documents are accumulated until the next one would exceed this threshold.
        /// A single oversized document is always allowed as a solo page.
        /// </summary>
        public const int MaxPayloadBytes = 4 * 1024 * 1024;

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
            // Pagination: offset determines which prefab/scene index to start from.
            // Documents are accumulated until adding the next one would exceed MaxPayloadBytes.
            int offset = parameters["offset"]?.ToObject<int>() ?? 0;

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

                // Size-based pagination: accumulate documents until the payload would exceed MaxPayloadBytes
                var documents = new List<CollectedDocument>();
                long accumulatedBytes = 0;
                int processed = 0;

                for (int i = offset; i < totalDocuments; i++)
                {
                    var entry = assetGuids[i];

                    EditorUtility.DisplayProgressBar(
                        "Context Engine — Collecting Assets",
                        $"{entry.AssetPath}  ({i + 1}/{totalDocuments})",
                        (float)(i + 1) / totalDocuments);

                    CollectedDocument doc = null;
                    try
                    {
                        if (entry.Type == PrefabDocumentType)
                        {
                            doc = ProcessPrefab(entry.AssetPath);
                        }
                        else if (entry.Type == SceneDocumentType)
                        {
                            doc = ProcessScene(entry.AssetPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"[Context Engine] Failed to process {entry.Type} {entry.AssetPath}: {ex.Message}");
                    }

                    processed++;

                    if (doc == null)
                    {
                        continue;
                    }

                    long docBytes = System.Text.Encoding.UTF8.GetByteCount(doc.Contents);

                    // If adding this document would exceed the limit AND we already have at least one,
                    // stop here — this document will be the first in the next page.
                    if (accumulatedBytes > 0 && accumulatedBytes + docBytes > MaxPayloadBytes)
                    {
                        // Put this one back — don't count it as processed
                        processed--;
                        break;
                    }

                    documents.Add(doc);
                    accumulatedBytes += docBytes;
                }

                int nextOffset = offset + processed;

                EditorUtility.DisplayProgressBar(
                    "Context Engine — Preparing response",
                    $"Page offset={offset}: {documents.Count} documents, {accumulatedBytes / 1024}KB (total: {totalDocuments})",
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
                    ["nextOffset"] = nextOffset,
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
                // Default: index both Assets and all local/embedded packages
                result.Add("Assets");
                // Add Packages/ subfolders that are valid (local/embedded packages)
                foreach (string packageFolder in GetLocalPackageFolders())
                {
                    if (!result.Contains(packageFolder))
                    {
                        result.Add(packageFolder);
                    }
                }
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

        /// <summary>
        /// Discovers local/embedded package folders that Unity recognizes.
        /// Uses PackageManager API to find packages with source "Local" or "Embedded",
        /// which are packages physically on disk (not registry/git packages in Library/PackageCache).
        /// </summary>
        private static List<string> GetLocalPackageFolders()
        {
            var result = new List<string>();

            // Use the PackageManager API to list all packages and filter to local/embedded
            var listRequest = UnityEditor.PackageManager.Client.List(offlineMode: true, includeIndirectDependencies: false);

            // Spin-wait for the request to complete (we're already in an editor coroutine context)
            while (!listRequest.IsCompleted)
            {
                System.Threading.Thread.Sleep(10);
            }

            if (listRequest.Status != UnityEditor.PackageManager.StatusCode.Success)
            {
                McpLogger.LogWarning("[Context Engine] Failed to list packages: " + (listRequest.Error?.message ?? "unknown error"));
                return result;
            }

            foreach (var packageInfo in listRequest.Result)
            {
                // Only include local (file: reference) and embedded (Packages/ subfolder) packages
                if (packageInfo.source == UnityEditor.PackageManager.PackageSource.Local ||
                    packageInfo.source == UnityEditor.PackageManager.PackageSource.Embedded)
                {
                    // Unity AssetDatabase uses Packages/<package-name> as the path
                    string assetDbPath = $"Packages/{packageInfo.name}";
                    if (AssetDatabase.IsValidFolder(assetDbPath))
                    {
                        result.Add(assetDbPath);
                        McpLogger.LogInfo($"[Context Engine] Including local package: {packageInfo.name} ({packageInfo.displayName})");
                    }
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

            // Support Packages/ paths for local/embedded packages
            if (trimmed.Equals("Packages", StringComparison.OrdinalIgnoreCase) ||
                trimmed.StartsWith("Packages/", StringComparison.OrdinalIgnoreCase))
            {
                return trimmed;
            }

            return $"Assets/{trimmed}";
        }
    }
}
