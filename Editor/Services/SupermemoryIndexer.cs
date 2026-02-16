using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using McpUnity.Resources;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Services
{
    /// <summary>
    /// Indexes Unity project assets (scripts, prefabs, optionally scenes) into supermemory
    /// for semantic search by AI agents.
    /// </summary>
    public static class SupermemoryIndexer
    {
        private const string ApiBaseUrl = "https://api.supermemory.ai/v3";
        private const int BatchSize = 100; // Documents per batch request (API max is 600)
        private const float BatchDelaySeconds = 0.5f; // Delay between batches to avoid rate limits
        
        // In-memory API key (not persisted to disk)
        private static string _sessionApiKey = string.Empty;
        
        /// <summary>
        /// Returns true if an API key is available (env var or session).
        /// </summary>
        public static bool HasApiKey => !string.IsNullOrEmpty(GetApiKey());
        
        /// <summary>
        /// Gets the session API key (in-memory only, not persisted).
        /// </summary>
        public static string SessionApiKey
        {
            get => _sessionApiKey;
            set => _sessionApiKey = value ?? string.Empty;
        }
        
        /// <summary>
        /// Resolves the API key: env var first, then session value.
        /// </summary>
        public static string GetApiKey()
        {
            string envKey = Environment.GetEnvironmentVariable("SUPERMEMORY_API_KEY");
            if (!string.IsNullOrEmpty(envKey))
                return envKey;
            return _sessionApiKey;
        }
        
        /// <summary>
        /// Returns true if the API key comes from the environment variable.
        /// </summary>
        public static bool IsApiKeyFromEnvironment()
        {
            return !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SUPERMEMORY_API_KEY"));
        }
        
        /// <summary>
        /// Resolves the container tag: user override from settings, or default.
        /// </summary>
        public static string GetContainerTag()
        {
            string userTag = McpUnity.Unity.McpUnitySettings.Instance.SupermemoryContainerTag;
            if (!string.IsNullOrEmpty(userTag))
            {
                string sanitized = SanitizeContainerTag(userTag);
                if (!string.IsNullOrEmpty(sanitized))
                    return sanitized;
            }
            
            string defaultTag = SanitizeContainerTag($"unity-{Application.productName}");
            // Fallback if product name sanitizes to empty (e.g. all symbols)
            return string.IsNullOrEmpty(defaultTag) ? "unity-project" : defaultTag;
        }
        
        /// <summary>
        /// Sanitize container tag to alphanumeric + hyphens + underscores, max 100 chars.
        /// </summary>
        private static string SanitizeContainerTag(string tag)
        {
            var sb = new StringBuilder();
            foreach (char c in tag)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
                    sb.Append(c);
                else if (c == ' ')
                    sb.Append('-');
                // skip other chars
            }
            string result = sb.ToString();
            if (result.Length > 100)
                result = result.Substring(0, 100);
            return result;
        }
        
        /// <summary>
        /// Compute SHA256 hash of a string, returned as lowercase hex.
        /// </summary>
        public static string ComputeContentHash(string content)
        {
            using (var sha256 = SHA256.Create())
            {
                byte[] bytes = Encoding.UTF8.GetBytes(content);
                byte[] hash = sha256.ComputeHash(bytes);
                var sb = new StringBuilder(hash.Length * 2);
                foreach (byte b in hash)
                    sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }
        
        /// <summary>
        /// Represents a document ready to be pushed to supermemory.
        /// </summary>
        public class IndexDocument
        {
            public string CustomId; // Asset GUID
            public string Content;
            public string Type; // "script", "prefab", "scene"
            public string AssetPath;
            public string ContentHash;
        }
        
        /// <summary>
        /// Resolves the search folder for AssetDatabase.FindAssets.
        /// Empty/null → "Assets". Otherwise "Assets/{folder}" with trailing slash trimmed.
        /// </summary>
        private static string ResolveSearchFolder(string folder)
        {
            if (string.IsNullOrWhiteSpace(folder))
                return "Assets";
            
            string trimmed = folder.Trim().TrimStart('/').TrimEnd('/');
            if (string.IsNullOrEmpty(trimmed))
                return "Assets";
            
            return $"Assets/{trimmed}";
        }
        
        /// <summary>
        /// Collect all script documents (MonoScript assets under the search folder).
        /// </summary>
        public static List<IndexDocument> CollectScripts(string searchFolder)
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", new[] { searchFolder });
            
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                    
                try
                {
                    string fileContent = File.ReadAllText(assetPath);
                    if (string.IsNullOrWhiteSpace(fileContent))
                        continue;
                    
                    // Prepend file path so it's part of the searchable/returned content
                    string content = $"// File: {assetPath}\n{fileContent}";
                        
                    docs.Add(new IndexDocument
                    {
                        CustomId = guid,
                        Content = content,
                        Type = "script",
                        AssetPath = assetPath,
                        ContentHash = ComputeContentHash(content)
                    });
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Supermemory] Failed to read script {assetPath}: {ex.Message}");
                }
            }
            
            return docs;
        }
        
        /// <summary>
        /// Collect all prefab documents (under the search folder).
        /// </summary>
        public static List<IndexDocument> CollectPrefabs(string searchFolder)
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { searchFolder });
            
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                
                try
                {
                    var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (prefab == null)
                        continue;
                    
                    JObject summary = GetGameObjectResource.GameObjectToSummaryJObject(prefab);
                    if (summary == null)
                        continue;
                    
                    // Prepend asset path so it's part of the searchable/returned content
                    summary["assetPath"] = assetPath;
                    string content = summary.ToString(Newtonsoft.Json.Formatting.None);
                    
                    docs.Add(new IndexDocument
                    {
                        CustomId = guid,
                        Content = content,
                        Type = "prefab",
                        AssetPath = assetPath,
                        ContentHash = ComputeContentHash(content)
                    });
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Supermemory] Failed to process prefab {assetPath}: {ex.Message}");
                }
            }
            
            return docs;
        }
        
        /// <summary>
        /// Collect all scene documents (under the search folder). Opens each scene additively.
        /// This can be slow for large scenes.
        /// </summary>
        public static List<IndexDocument> CollectScenes(string searchFolder)
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:SceneAsset", new[] { searchFolder });
            
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                
                var scene = default(UnityEngine.SceneManagement.Scene);
                bool sceneOpened = false;
                try
                {
                    // Open scene additively to read hierarchy
                    scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        assetPath, 
                        UnityEditor.SceneManagement.OpenSceneMode.Additive
                    );
                    sceneOpened = scene.IsValid();
                    
                    // Capture scene name BEFORE closing (struct may lose data after close)
                    string sceneName = scene.name;
                    
                    JArray rootObjects = new JArray();
                    foreach (var rootGo in scene.GetRootGameObjects())
                    {
                        rootObjects.Add(GetGameObjectResource.GameObjectToSummaryJObject(rootGo));
                    }
                    
                    // Close the additively loaded scene
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    sceneOpened = false;
                    
                    JObject sceneDoc = new JObject
                    {
                        ["assetPath"] = assetPath,
                        ["sceneName"] = sceneName,
                        ["rootObjects"] = rootObjects
                    };
                    
                    string content = sceneDoc.ToString(Newtonsoft.Json.Formatting.None);
                    
                    docs.Add(new IndexDocument
                    {
                        CustomId = guid,
                        Content = content,
                        Type = "scene",
                        AssetPath = assetPath,
                        ContentHash = ComputeContentHash(content)
                    });
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"[Supermemory] Failed to process scene {assetPath}: {ex.Message}");
                }
                finally
                {
                    // Ensure additively opened scene is always closed to prevent leaks
                    if (sceneOpened && scene.IsValid() && scene.isLoaded)
                    {
                        UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    }
                }
            }
            
            return docs;
        }
        
        /// <summary>
        /// Index the project: collect documents and push to supermemory in batches.
        /// Runs as an editor coroutine to keep the editor responsive.
        /// </summary>
        /// <param name="includeScenes">Whether to include scene files (can be slow).</param>
        /// <param name="indexFolder">Subfolder under Assets/ to index. Empty means all of Assets/.</param>
        public static void IndexProject(bool includeScenes, string indexFolder)
        {
            string apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    "No API key found. Set the SUPERMEMORY_API_KEY environment variable or enter a key in the settings window.", 
                    "OK");
                return;
            }
            
            string searchFolder = ResolveSearchFolder(indexFolder);
            
            // Validate the folder exists
            if (!AssetDatabase.IsValidFolder(searchFolder))
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    $"Folder '{searchFolder}' does not exist in the project.", 
                    "OK");
                return;
            }
            
            EditorCoroutineUtility.StartCoroutineOwnerless(IndexProjectCoroutine(includeScenes, apiKey, searchFolder));
        }
        
        private static IEnumerator IndexProjectCoroutine(bool includeScenes, string apiKey, string searchFolder)
        {
            string containerTag = GetContainerTag();
            int totalSuccess = 0;
            int totalFailed = 0;
            var allDocs = new List<IndexDocument>();
            List<IndexDocument> scripts = null;
            List<IndexDocument> prefabs = null;
            
            // Wrap entire coroutine to guarantee progress bar cleanup
            bool progressBarActive = true;
            
            // Phase 1: Collect (synchronous — no yield, so progress bar won't repaint between calls)
            EditorUtility.DisplayProgressBar("Supermemory Indexing", "Collecting assets...", 0f);
            
            scripts = CollectScripts(searchFolder);
            allDocs.AddRange(scripts);
            McpLogger.LogInfo($"[Supermemory] Collected {scripts.Count} scripts from {searchFolder}");
            
            prefabs = CollectPrefabs(searchFolder);
            allDocs.AddRange(prefabs);
            McpLogger.LogInfo($"[Supermemory] Collected {prefabs.Count} prefabs from {searchFolder}");
            
            if (includeScenes)
            {
                var scenes = CollectScenes(searchFolder);
                allDocs.AddRange(scenes);
                McpLogger.LogInfo($"[Supermemory] Collected {scenes.Count} scenes from {searchFolder}");
            }
            
            // Yield once so the progress bar actually renders before pushing
            yield return null;
            
            if (allDocs.Count == 0)
            {
                EditorUtility.ClearProgressBar();
                progressBarActive = false;
                EditorUtility.DisplayDialog("Supermemory", "No assets found to index.", "OK");
                yield break;
            }
            
            // Phase 1.5: Fetch existing content hashes from supermemory to skip unchanged docs
            EditorUtility.DisplayProgressBar("Supermemory Indexing", "Checking for changes...", 0.15f);
            var existingHashes = new Dictionary<string, string>();
            int listPage = 1;
            bool hasMorePages = true;
            
            while (hasMorePages)
            {
                JObject listBody = new JObject
                {
                    ["containerTags"] = new JArray { containerTag },
                    ["limit"] = 200,
                    ["page"] = listPage
                };
                
                string listJson = listBody.ToString(Newtonsoft.Json.Formatting.None);
                byte[] listBytes = Encoding.UTF8.GetBytes(listJson);
                
                var listRequest = new UnityEngine.Networking.UnityWebRequest($"{ApiBaseUrl}/documents/list", "POST");
                listRequest.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(listBytes);
                listRequest.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                listRequest.timeout = 15;
                listRequest.SetRequestHeader("Content-Type", "application/json");
                listRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                
                var listOp = listRequest.SendWebRequest();
                while (!listOp.isDone)
                    yield return null;
                
                if (listRequest.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        JObject listResponse = JObject.Parse(listRequest.downloadHandler.text);
                        JArray memories = listResponse["memories"] as JArray;
                        if (memories != null)
                        {
                            foreach (JObject mem in memories)
                            {
                                string customId = mem["customId"]?.ToString();
                                string hash = mem["metadata"]?["contentHash"]?.ToString();
                                if (!string.IsNullOrEmpty(customId) && !string.IsNullOrEmpty(hash))
                                {
                                    existingHashes[customId] = hash;
                                }
                            }
                        }
                        
                        JObject pagination = listResponse["pagination"] as JObject;
                        int totalPages = pagination?["totalPages"]?.Value<int>() ?? 1;
                        hasMorePages = listPage < totalPages;
                        listPage++;
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[Supermemory] Failed to parse document list (page {listPage}): {ex.Message}");
                        hasMorePages = false; // proceed without filtering
                    }
                }
                else
                {
                    McpLogger.LogWarning($"[Supermemory] Failed to fetch existing documents: {listRequest.responseCode} {listRequest.error}. Will re-index all.");
                    hasMorePages = false; // proceed without filtering
                }
                
                listRequest.Dispose();
            }
            
            // Filter out unchanged documents
            int originalCount = allDocs.Count;
            if (existingHashes.Count > 0)
            {
                allDocs.RemoveAll(doc => 
                    existingHashes.TryGetValue(doc.CustomId, out string existingHash) && 
                    existingHash == doc.ContentHash);
            }
            int skippedCount = originalCount - allDocs.Count;
            if (skippedCount > 0)
            {
                McpLogger.LogInfo($"[Supermemory] Skipped {skippedCount} unchanged documents");
            }
            
            if (allDocs.Count == 0)
            {
                EditorUtility.ClearProgressBar();
                progressBarActive = false;
                EditorUtility.DisplayDialog("Supermemory", 
                    $"All {originalCount} documents are up to date. Nothing to re-index.", "OK");
                yield break;
            }
            
            // Phase 2: Push in batches
            int totalDocs = allDocs.Count;
            int batchCount = 0;
            
            for (int i = 0; i < totalDocs; i += BatchSize)
            {
                int end = Math.Min(i + BatchSize, totalDocs);
                float progress = 0.3f + 0.7f * ((float)i / totalDocs);
                EditorUtility.DisplayProgressBar("Supermemory Indexing", 
                    $"Pushing batch {batchCount + 1} ({i + 1}-{end} of {totalDocs})...", progress);
                
                // Build batch request body
                JArray batchDocs = new JArray();
                for (int j = i; j < end; j++)
                {
                    var doc = allDocs[j];
                    batchDocs.Add(new JObject
                    {
                        ["content"] = doc.Content,
                        ["customId"] = doc.CustomId,
                        ["metadata"] = new JObject
                        {
                            ["type"] = doc.Type,
                            ["assetPath"] = doc.AssetPath,
                            ["contentHash"] = doc.ContentHash
                        }
                    });
                }
                
                JObject requestBody = new JObject
                {
                    ["containerTag"] = containerTag,
                    ["documents"] = batchDocs
                };
                
                // Send batch request
                string json = requestBody.ToString(Newtonsoft.Json.Formatting.None);
                byte[] bodyBytes = Encoding.UTF8.GetBytes(json);
                
                var request = new UnityEngine.Networking.UnityWebRequest($"{ApiBaseUrl}/documents/batch", "POST");
                request.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(bodyBytes);
                request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                request.timeout = 30; // 30 second timeout per batch request
                request.SetRequestHeader("Content-Type", "application/json");
                request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                    yield return null;
                
                if (request.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    totalSuccess += (end - i);
                    McpLogger.LogInfo($"[Supermemory] Batch {batchCount + 1} succeeded ({end - i} docs)");
                }
                else
                {
                    totalFailed += (end - i);
                    McpLogger.LogError($"[Supermemory] Batch {batchCount + 1} failed: {request.responseCode} {request.error} — {request.downloadHandler?.text}");
                }
                
                request.Dispose();
                batchCount++;
                
                // Delay between batches
                if (i + BatchSize < totalDocs)
                {
                    double waitUntil = EditorApplication.timeSinceStartup + BatchDelaySeconds;
                    while (EditorApplication.timeSinceStartup < waitUntil)
                        yield return null;
                }
            }
            
            if (progressBarActive)
            {
                EditorUtility.ClearProgressBar();
                progressBarActive = false;
            }
            
            // Update timestamp
            McpUnity.Unity.McpUnitySettings.Instance.SupermemoryLastIndexedTimestamp = 
                DateTime.UtcNow.ToString("o");
            McpUnity.Unity.McpUnitySettings.Instance.SaveSettings();
            
            // Summary
            int scriptCount = scripts?.Count ?? 0;
            int prefabCount = prefabs?.Count ?? 0;
            string scenePart = includeScenes ? $", {allDocs.FindAll(d => d.Type == "scene").Count} scenes" : "";
            string skippedPart = skippedCount > 0 ? $", Skipped (unchanged): {skippedCount}" : "";
            string summary = $"Found {originalCount} assets ({scriptCount} scripts, {prefabCount} prefabs{scenePart}).\n\n" +
                             $"Pushed: {totalSuccess}, Failed: {totalFailed}{skippedPart}\n" +
                             $"Container tag: {containerTag}";
            
            if (totalFailed > 0)
            {
                summary += "\n\nCheck the Console for error details.";
            }
            
            summary += "\n\nNote: Documents may take a few minutes to finish processing " +
                       "and become searchable. Use 'Check Processing Status' to monitor progress.";
            
            EditorUtility.DisplayDialog("Supermemory Indexing Complete", summary, "OK");
        }
        
        /// <summary>
        /// Check for changes without pushing. Collects local assets, fetches existing hashes,
        /// and reports how many are new, changed, or unchanged.
        /// </summary>
        public static void CheckForChanges(bool includeScenes, string indexFolder)
        {
            string apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                EditorUtility.DisplayDialog("Supermemory", "No API key found.", "OK");
                return;
            }
            
            string searchFolder = ResolveSearchFolder(indexFolder);
            if (!AssetDatabase.IsValidFolder(searchFolder))
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    $"Folder '{searchFolder}' does not exist in the project.", "OK");
                return;
            }
            
            EditorCoroutineUtility.StartCoroutineOwnerless(CheckForChangesCoroutine(includeScenes, apiKey, searchFolder));
        }
        
        private static IEnumerator CheckForChangesCoroutine(bool includeScenes, string apiKey, string searchFolder)
        {
            string containerTag = GetContainerTag();
            
            EditorUtility.DisplayProgressBar("Supermemory", "Collecting local assets...", 0f);
            
            var allDocs = new List<IndexDocument>();
            allDocs.AddRange(CollectScripts(searchFolder));
            allDocs.AddRange(CollectPrefabs(searchFolder));
            if (includeScenes)
                allDocs.AddRange(CollectScenes(searchFolder));
            
            yield return null;
            
            // Fetch existing hashes
            EditorUtility.DisplayProgressBar("Supermemory", "Fetching remote hashes...", 0.3f);
            var existingHashes = new Dictionary<string, string>();
            int listPage = 1;
            bool hasMorePages = true;
            
            while (hasMorePages)
            {
                JObject listBody = new JObject
                {
                    ["containerTags"] = new JArray { containerTag },
                    ["limit"] = 200,
                    ["page"] = listPage
                };
                
                string listJson = listBody.ToString(Newtonsoft.Json.Formatting.None);
                byte[] listBytes = Encoding.UTF8.GetBytes(listJson);
                
                var listRequest = new UnityEngine.Networking.UnityWebRequest($"{ApiBaseUrl}/documents/list", "POST");
                listRequest.uploadHandler = new UnityEngine.Networking.UploadHandlerRaw(listBytes);
                listRequest.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
                listRequest.timeout = 15;
                listRequest.SetRequestHeader("Content-Type", "application/json");
                listRequest.SetRequestHeader("Authorization", $"Bearer {apiKey}");
                
                var listOp = listRequest.SendWebRequest();
                while (!listOp.isDone)
                    yield return null;
                
                if (listRequest.result == UnityEngine.Networking.UnityWebRequest.Result.Success)
                {
                    try
                    {
                        JObject listResponse = JObject.Parse(listRequest.downloadHandler.text);
                        JArray memories = listResponse["memories"] as JArray;
                        if (memories != null)
                        {
                            foreach (JObject mem in memories)
                            {
                                string customId = mem["customId"]?.ToString();
                                string hash = mem["metadata"]?["contentHash"]?.ToString();
                                if (!string.IsNullOrEmpty(customId) && !string.IsNullOrEmpty(hash))
                                    existingHashes[customId] = hash;
                            }
                        }
                        
                        JObject pagination = listResponse["pagination"] as JObject;
                        int totalPages = pagination?["totalPages"]?.Value<int>() ?? 1;
                        hasMorePages = listPage < totalPages;
                        listPage++;
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogWarning($"[Supermemory] Failed to parse list response: {ex.Message}");
                        hasMorePages = false;
                    }
                }
                else
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Supermemory", 
                        $"Failed to fetch remote documents: {listRequest.responseCode} {listRequest.error}", "OK");
                    listRequest.Dispose();
                    yield break;
                }
                
                listRequest.Dispose();
            }
            
            EditorUtility.ClearProgressBar();
            
            // Compare
            int unchanged = 0;
            int changed = 0;
            int newDocs = 0;
            int noHashRemote = 0;
            
            foreach (var doc in allDocs)
            {
                if (existingHashes.TryGetValue(doc.CustomId, out string remoteHash))
                {
                    if (remoteHash == doc.ContentHash)
                        unchanged++;
                    else
                        changed++;
                }
                else
                {
                    newDocs++;
                }
            }
            
            // Check if remote has docs with no contentHash metadata
            noHashRemote = existingHashes.Values.Count(v => string.IsNullOrEmpty(v));
            
            string summary = $"Local: {allDocs.Count} assets, Remote: {existingHashes.Count} documents\n\n" +
                             $"Unchanged: {unchanged}\n" +
                             $"Changed: {changed}\n" +
                             $"New (not in remote): {newDocs}";
            
            if (changed == 0 && newDocs == 0)
                summary += "\n\nEverything is up to date.";
            else
                summary += $"\n\nRe-indexing would push {changed + newDocs} document(s).";
            
            EditorUtility.DisplayDialog("Supermemory — Change Check", summary, "OK");
        }
        
        /// <summary>
        /// Check how many documents are still being processed by supermemory.
        /// Runs as an editor coroutine.
        /// </summary>
        public static void CheckProcessingStatus()
        {
            string apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    "No API key found.", "OK");
                return;
            }
            
            EditorCoroutineUtility.StartCoroutineOwnerless(CheckProcessingStatusCoroutine(apiKey));
        }
        
        private static IEnumerator CheckProcessingStatusCoroutine(string apiKey)
        {
            EditorUtility.DisplayProgressBar("Supermemory", "Checking processing status...", 0.5f);
            
            var request = new UnityEngine.Networking.UnityWebRequest($"{ApiBaseUrl}/documents/processing", "GET");
            request.downloadHandler = new UnityEngine.Networking.DownloadHandlerBuffer();
            request.timeout = 15;
            request.SetRequestHeader("Authorization", $"Bearer {apiKey}");
            
            var operation = request.SendWebRequest();
            while (!operation.isDone)
                yield return null;
            
            EditorUtility.ClearProgressBar();
            
            if (request.result != UnityEngine.Networking.UnityWebRequest.Result.Success)
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    $"Failed to check status: {request.responseCode} {request.error}", "OK");
                request.Dispose();
                yield break;
            }
            
            try
            {
                JObject response = JObject.Parse(request.downloadHandler.text);
                JArray docs = response["documents"] as JArray;
                int total = docs?.Count ?? 0;
                
                if (total == 0)
                {
                    EditorUtility.DisplayDialog("Supermemory", 
                        "All documents have been processed and are searchable.", "OK");
                }
                else
                {
                    // Count by status
                    var statusCounts = new Dictionary<string, int>();
                    foreach (JObject doc in docs)
                    {
                        string status = doc["status"]?.ToString() ?? "unknown";
                        if (!statusCounts.ContainsKey(status))
                            statusCounts[status] = 0;
                        statusCounts[status]++;
                    }
                    
                    var sb = new StringBuilder();
                    sb.AppendLine($"{total} document(s) still processing:\n");
                    foreach (var kvp in statusCounts)
                    {
                        sb.AppendLine($"  {kvp.Key}: {kvp.Value}");
                    }
                    
                    EditorUtility.DisplayDialog("Supermemory Processing Status", sb.ToString(), "OK");
                }
            }
            catch (Exception ex)
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    $"Failed to parse status response: {ex.Message}", "OK");
            }
            
            request.Dispose();
        }
    }
}
