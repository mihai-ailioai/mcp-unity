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
            // Check for incremental mode: specific asset paths to process
            var assetPathsToken = parameters["assetPaths"];
            if (assetPathsToken is JArray assetPathsArray && assetPathsArray.Count > 0)
            {
                yield return ExecuteCollectSpecificAssets(assetPathsArray, tcs);
                yield break;
            }

            // Pagination: offset determines which prefab/scene index to start from.
            // Documents are accumulated until adding the next one would exceed MaxPayloadBytes.
            int offset = parameters["offset"]?.ToObject<int>() ?? 0;

            // Always use editor settings — folders and prefab/scene inclusion are configured in the Context Engine tab
            var settings = McpUnitySettings.Instance;
            bool includePrefabs = settings.ContextEngineIndexPrefabs;
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

                // Discover prefab/scene asset GUIDs to determine total count
                var assetGuids = new List<AssetGuidEntry>();
                if (includePrefabs)
                {
                    DiscoverPrefabGuids(searchFolders, assetGuids);
                }
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

                // Build package path map: AssetDatabase path -> disk folder name
                // e.g. "Packages/com.evlppy.core" -> "Packages/Core-Module"
                var packagePathMap = new JObject();
                foreach (string sf in searchFolders)
                {
                    if (sf.StartsWith("Packages/", StringComparison.Ordinal))
                    {
                        string diskPath = ResolveToDiskPath(sf);
                        if (diskPath != null && diskPath != sf)
                        {
                            packagePathMap[sf] = diskPath;
                        }
                    }
                }

                JObject response = new JObject
                {
                    ["success"] = true,
                    ["scriptPaths"] = responseScriptPaths,
                    ["documents"] = responseDocuments,
                    ["totalDocuments"] = totalDocuments,
                    ["offset"] = offset,
                    ["nextOffset"] = nextOffset,
                    ["packagePathMap"] = packagePathMap,
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

        // ── Incremental mode: process only specific asset paths ──

        private static IEnumerator ExecuteCollectSpecificAssets(JArray assetPathsArray, TaskCompletionSource<JObject> tcs)
        {
            var settings = McpUnitySettings.Instance;
            bool includePrefabs = settings.ContextEngineIndexPrefabs;
            bool includeScenes = settings.ContextEngineIndexScenes;

            yield return null;

            try
            {
                var documents = new List<CollectedDocument>();

                for (int i = 0; i < assetPathsArray.Count; i++)
                {
                    string assetPath = assetPathsArray[i]?.ToString();
                    if (string.IsNullOrEmpty(assetPath))
                        continue;

                    try
                    {
                        if (assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!includePrefabs) continue;
                            var doc = ProcessPrefab(assetPath);
                            if (doc != null) documents.Add(doc);
                        }
                        else if (assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!includeScenes) continue;
                            var doc = ProcessScene(assetPath);
                            if (doc != null) documents.Add(doc);
                        }
                        // Scripts and other file types are handled Node-side (read from disk)
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"[Context Engine] Failed to process {assetPath}: {ex.Message}");
                    }
                }

                var responseDocuments = new JArray();
                foreach (var doc in documents)
                {
                    responseDocuments.Add(doc.ToResponseJObject());
                }

                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["documents"] = responseDocuments,
                });
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[Context Engine] Failed to collect specific assets: {ex.Message}");
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to collect specific assets: {ex.Message}",
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
        /// Reads package.json in each Packages/ subdirectory to get the real package name,
        /// since the folder name (e.g. "Core-Module") often differs from the package name
        /// (e.g. "com.evlppy.core") that Unity uses in AssetDatabase paths.
        /// </summary>
        private static List<string> GetLocalPackageFolders()
        {
            var result = new List<string>();
            string packagesPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Packages");

            if (!System.IO.Directory.Exists(packagesPath))
                return result;

            string[] dirs = System.IO.Directory.GetDirectories(packagesPath);

            foreach (string dir in dirs)
            {
                string folderName = System.IO.Path.GetFileName(dir);
                string packageJsonPath = System.IO.Path.Combine(dir, "package.json");
                if (!System.IO.File.Exists(packageJsonPath))
                    continue;

                try
                {
                    string json = System.IO.File.ReadAllText(packageJsonPath);
                    var packageJson = JObject.Parse(json);
                    string packageName = packageJson["name"]?.ToString();

                    if (string.IsNullOrEmpty(packageName))
                        continue;

                    string assetDbPath = $"Packages/{packageName}";
                    if (AssetDatabase.IsValidFolder(assetDbPath))
                    {
                        result.Add(assetDbPath);
                    }
                }
                catch (System.Exception e)
                {
                    McpLogger.LogWarning($"[Context Engine] Failed to read package.json in {folderName}: {e.Message}");
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

        /// <summary>
        /// Maps an AssetDatabase package path (e.g. "Packages/com.evlppy.core") to the actual
        /// disk-relative path (e.g. "Packages/Core-Module"). Returns null if not resolvable.
        /// Uses the GetLocalPackageFolders discovery logic in reverse: scans Packages/ subdirs,
        /// reads package.json name, and matches against the assetDbPath.
        /// </summary>
        private static string ResolveToDiskPath(string assetDbPath)
        {
            // assetDbPath is like "Packages/com.evlppy.core"
            string packageName = assetDbPath.StartsWith("Packages/", StringComparison.Ordinal)
                ? assetDbPath.Substring("Packages/".Length)
                : null;

            if (string.IsNullOrEmpty(packageName))
                return null;

            string packagesPath = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Packages");
            if (!System.IO.Directory.Exists(packagesPath))
                return null;

            foreach (string dir in System.IO.Directory.GetDirectories(packagesPath))
            {
                string packageJsonPath = System.IO.Path.Combine(dir, "package.json");
                if (!System.IO.File.Exists(packageJsonPath))
                    continue;

                try
                {
                    string json = System.IO.File.ReadAllText(packageJsonPath);
                    var packageJson = JObject.Parse(json);
                    string name = packageJson["name"]?.ToString();

                    if (name == packageName)
                    {
                        string folderName = System.IO.Path.GetFileName(dir);
                        return $"Packages/{folderName}";
                    }
                }
                catch
                {
                    // Skip unreadable package.json files
                }
            }

            return null;
        }
    }
}
