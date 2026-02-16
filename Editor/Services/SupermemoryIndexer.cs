using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
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
                return SanitizeContainerTag(userTag);
            return SanitizeContainerTag($"unity-{Application.productName}");
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
        /// Collect all script documents (MonoScript assets under Assets/).
        /// </summary>
        public static List<IndexDocument> CollectScripts()
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets" });
            
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                    continue;
                    
                try
                {
                    string content = File.ReadAllText(assetPath);
                    if (string.IsNullOrWhiteSpace(content))
                        continue;
                        
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
        /// Collect all prefab documents (under Assets/).
        /// </summary>
        public static List<IndexDocument> CollectPrefabs()
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:Prefab", new[] { "Assets" });
            
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
        /// Collect all scene documents (under Assets/). Opens each scene additively.
        /// This can be slow for large scenes.
        /// </summary>
        public static List<IndexDocument> CollectScenes()
        {
            var docs = new List<IndexDocument>();
            string[] guids = AssetDatabase.FindAssets("t:SceneAsset", new[] { "Assets" });
            
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                
                try
                {
                    // Open scene additively to read hierarchy
                    var scene = UnityEditor.SceneManagement.EditorSceneManager.OpenScene(
                        assetPath, 
                        UnityEditor.SceneManagement.OpenSceneMode.Additive
                    );
                    
                    JArray rootObjects = new JArray();
                    foreach (var rootGo in scene.GetRootGameObjects())
                    {
                        rootObjects.Add(GetGameObjectResource.GameObjectToSummaryJObject(rootGo));
                    }
                    
                    // Close the additively loaded scene
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
                    
                    JObject sceneDoc = new JObject
                    {
                        ["sceneName"] = scene.name,
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
            }
            
            return docs;
        }
        
        /// <summary>
        /// Index the project: collect documents and push to supermemory in batches.
        /// Runs as an editor coroutine to keep the editor responsive.
        /// </summary>
        /// <param name="includeScenes">Whether to include scene files (can be slow).</param>
        public static void IndexProject(bool includeScenes)
        {
            string apiKey = GetApiKey();
            if (string.IsNullOrEmpty(apiKey))
            {
                EditorUtility.DisplayDialog("Supermemory", 
                    "No API key found. Set the SUPERMEMORY_API_KEY environment variable or enter a key in the settings window.", 
                    "OK");
                return;
            }
            
            EditorCoroutineUtility.StartCoroutineOwnerless(IndexProjectCoroutine(includeScenes, apiKey));
        }
        
        private static IEnumerator IndexProjectCoroutine(bool includeScenes, string apiKey)
        {
            string containerTag = GetContainerTag();
            int totalSuccess = 0;
            int totalFailed = 0;
            
            // Phase 1: Collect
            EditorUtility.DisplayProgressBar("Supermemory Indexing", "Collecting scripts...", 0f);
            var allDocs = new List<IndexDocument>();
            
            var scripts = CollectScripts();
            allDocs.AddRange(scripts);
            McpLogger.LogInfo($"[Supermemory] Collected {scripts.Count} scripts");
            
            EditorUtility.DisplayProgressBar("Supermemory Indexing", "Collecting prefabs...", 0.1f);
            var prefabs = CollectPrefabs();
            allDocs.AddRange(prefabs);
            McpLogger.LogInfo($"[Supermemory] Collected {prefabs.Count} prefabs");
            
            if (includeScenes)
            {
                EditorUtility.DisplayProgressBar("Supermemory Indexing", "Collecting scenes (this may take a while)...", 0.2f);
                var scenes = CollectScenes();
                allDocs.AddRange(scenes);
                McpLogger.LogInfo($"[Supermemory] Collected {scenes.Count} scenes");
            }
            
            if (allDocs.Count == 0)
            {
                EditorUtility.ClearProgressBar();
                EditorUtility.DisplayDialog("Supermemory", "No assets found to index.", "OK");
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
            
            EditorUtility.ClearProgressBar();
            
            // Update timestamp
            McpUnity.Unity.McpUnitySettings.Instance.SupermemoryLastIndexedTimestamp = 
                DateTime.UtcNow.ToString("o");
            McpUnity.Unity.McpUnitySettings.Instance.SaveSettings();
            
            // Summary
            string scenePart = includeScenes ? $", {allDocs.FindAll(d => d.Type == "scene").Count} scenes" : "";
            string summary = $"Indexed {scripts.Count} scripts, {prefabs.Count} prefabs{scenePart}.\n\n" +
                             $"Success: {totalSuccess}, Failed: {totalFailed}\n" +
                             $"Container tag: {containerTag}";
            
            if (totalFailed > 0)
                summary += "\n\nCheck the Console for error details.";
            
            EditorUtility.DisplayDialog("Supermemory Indexing Complete", summary, "OK");
        }
    }
}
