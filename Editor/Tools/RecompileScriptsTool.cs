using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace McpUnity.Tools {
    /// <summary>
    /// Tool to recompile all scripts in the Unity project
    /// </summary>
    public class RecompileScriptsTool : McpToolBase
    {
        private class CompilationRequest 
        {
            public readonly bool ReturnWithLogs;
            public readonly int LogsLimit;
            public readonly TaskCompletionSource<JObject> CompletionSource;
            
            public CompilationRequest(bool returnWithLogs, int logsLimit, TaskCompletionSource<JObject> completionSource)
            {
                ReturnWithLogs = returnWithLogs;
                LogsLimit = logsLimit;
                CompletionSource = completionSource;
            }
        }
        
        private class CompilationResult 
        {
            public readonly List<CompilerMessage> SortedLogs;
            public readonly int WarningsCount;
            public readonly int ErrorsCount;
            
            public bool HasErrors => ErrorsCount > 0;
            
            public CompilationResult(List<CompilerMessage> sortedLogs, int warningsCount, int errorsCount) 
            {
                SortedLogs = sortedLogs;
                WarningsCount = warningsCount;
                ErrorsCount = errorsCount;
            }
        }
        
        /// <summary>
        /// Hard wall-clock timeout for the watchdog. If OnCompilationFinished hasn't
        /// fired within this time (regardless of isCompiling state), bail out.
        /// This handles Hot Reload keeping isCompiling=true indefinitely.
        /// </summary>
        private const float CompilationWatchdogTimeoutSeconds = 3f;
        
        private readonly List<CompilationRequest> _pendingRequests = new List<CompilationRequest>();
        private readonly List<CompilerMessage> _compilationLogs = new List<CompilerMessage>();
        private int _processedAssemblies = 0;
        private bool _compilationFinished = false;

        public RecompileScriptsTool()
        {
            Name = "recompile_scripts";
            Description = "Recompiles all scripts in the Unity project";
            IsAsync = true; // Compilation is asynchronous
        }

        /// <summary>
        /// Execute the Recompile tool asynchronously
        /// </summary>
        /// <param name="parameters">Tool parameters as a JObject</param>
        /// <param name="tcs">TaskCompletionSource to set the result or exception</param>
        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            // Extract and store parameters
            var returnWithLogs = GetBoolParameter(parameters, "returnWithLogs", true);
            var logsLimit = Mathf.Clamp(GetIntParameter(parameters, "logsLimit", 100), 0, 1000);
            var request = new CompilationRequest(returnWithLogs, logsLimit, tcs);
            
            bool hasActiveRequest = false;
            lock (_pendingRequests)
            {
                hasActiveRequest = _pendingRequests.Count > 0;
                _pendingRequests.Add(request);
            }

            if (hasActiveRequest)
            {
                McpLogger.LogInfo("Recompilation already in progress. Waiting for completion...");
                return;
            }
            
            // On first request, initialize compilation listeners and start compilation
            _compilationFinished = false;
            StartCompilationTracking();
                
            if (EditorApplication.isCompiling == false)
            {
                // Refresh AssetDatabase first so Unity picks up any new/modified scripts
                // written to disk externally (e.g., by a coding agent). Without this,
                // CompilationPipeline.RequestScriptCompilation() only compiles files
                // Unity already knows about.
                AssetDatabase.Refresh();
                
                McpLogger.LogInfo("Recompiling all scripts in the Unity project");
                CompilationPipeline.RequestScriptCompilation();
            }
            else
            {
                McpLogger.LogInfo("Compilation already in progress, waiting for it to complete...");
            }
            
            // Always start the watchdog — it detects if compilation never completes
            // (e.g. Hot Reload blocks the pipeline, or isCompiling is a false positive)
            EditorCoroutineUtility.StartCoroutineOwnerless(WatchForCompilationComplete());
        }
        
        /// <summary>
        /// Coroutine that watches whether compilation completes within a timeout.
        /// If OnCompilationFinished never fires (e.g. Hot Reload is blocking the
        /// compilation pipeline, or isCompiling was a false positive), this coroutine
        /// completes all pending requests gracefully instead of hanging forever.
        /// </summary>
        private IEnumerator WatchForCompilationComplete()
        {
            float elapsed = 0f;
            float pollInterval = 0.5f;
            
            while (elapsed < CompilationWatchdogTimeoutSeconds)
            {
                yield return new EditorWaitForSeconds(pollInterval);
                elapsed += pollInterval;
                
                // OnCompilationFinished already handled everything
                if (_compilationFinished)
                    yield break;
                
                // If a real compilation started (assemblyCompilationFinished fired at
                // least once), trust it and keep waiting — don't timeout mid-compile.
                // But if only isCompiling is true with no assemblies processing,
                // that's the Hot Reload false-positive: don't reset the timer.
                if (_processedAssemblies > 0)
                {
                    elapsed = 0f;
                    continue;
                }
            }
            
            // Timed out — compilation never completed via the standard pipeline
            // Check if there are still pending requests (OnCompilationFinished may have raced)
            List<CompilationRequest> requestsToComplete = new List<CompilationRequest>();
            lock (_pendingRequests)
            {
                if (_pendingRequests.Count == 0)
                    yield break; // Already handled
                
                requestsToComplete.AddRange(_pendingRequests);
                _pendingRequests.Clear();
            }
            
            McpLogger.LogWarning(
                "Recompilation did not complete within the expected timeframe. " +
                "This typically happens when a live-reload tool (e.g. Hot Reload) " +
                "intercepts file changes during Play mode. " +
                "The file edit was likely already applied via hot-reload.");
            
            StopCompilationTracking();

            foreach (var req in requestsToComplete)
            {
                var response = new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = "Recompilation was requested but did not complete. " +
                                  "This typically happens when a live-reload tool (e.g. Hot Reload) " +
                                  "is active during Play mode and has already applied the changes. " +
                                  "No standard recompilation occurred.",
                    ["logs"] = new JArray()
                };
                req.CompletionSource.SetResult(response);
            }
        }

        /// <summary>
        /// Subscribe to compilation events, reset tracked state
        /// </summary>
        private void StartCompilationTracking()
        {
            _compilationLogs.Clear();
            _processedAssemblies = 0;
            CompilationPipeline.assemblyCompilationFinished += OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished += OnCompilationFinished;
        }
        
        /// <summary>
        /// Unsubscribe from compilation events
        /// </summary>
        private void StopCompilationTracking()
        {
            CompilationPipeline.assemblyCompilationFinished -= OnAssemblyCompilationFinished;
            CompilationPipeline.compilationFinished -= OnCompilationFinished;
        }

        /// <summary>
        /// Record compilation logs for every single assembly
        /// </summary>
        private void OnAssemblyCompilationFinished(string assemblyPath, CompilerMessage[] messages)
        {
            _processedAssemblies++;
            _compilationLogs.AddRange(messages);
        }

        /// <summary>
        /// Stop tracking and complete all pending requests
        /// </summary>
        private void OnCompilationFinished(object _)
        {
            _compilationFinished = true;
            McpLogger.LogInfo($"Recompilation completed. Processed {_processedAssemblies} assemblies with {_compilationLogs.Count} compiler messages");

            // Sort logs by type: first errors, then warnings and info
            List<CompilerMessage> sortedLogs = _compilationLogs.OrderBy(x => x.type).ToList();
            int errorsCount = _compilationLogs.Count(l => l.type == CompilerMessageType.Error);
            int warningsCount = _compilationLogs.Count(l => l.type == CompilerMessageType.Warning);
            CompilationResult result = new CompilationResult(sortedLogs, warningsCount, errorsCount);
            
            // Stop tracking before completing requests
            StopCompilationTracking();
            
            // Complete all requests received before compilation end, the next received request will start a new compilation
            List<CompilationRequest> requestsToComplete = new List<CompilationRequest>();
            
            lock (_pendingRequests)
            {
                requestsToComplete.AddRange(_pendingRequests);
                _pendingRequests.Clear();
            }

            foreach (var request in requestsToComplete)
            {
                CompleteRequest(request, result);
            }
        }

        /// <summary>
        /// Process a completed compilation request
        /// </summary>
        private static void CompleteRequest(CompilationRequest request, CompilationResult result)
        {
            JArray logsArray = new JArray();
            IEnumerable<CompilerMessage> logsToReturn = request.ReturnWithLogs ? result.SortedLogs.Take(request.LogsLimit) : Enumerable.Empty<CompilerMessage>();

            foreach (var message in logsToReturn)
            {
                var logObject = new JObject 
                {
                    ["message"] = message.message,
                    ["type"] = message.type.ToString()
                };

                // Add file information if available
                if (!string.IsNullOrEmpty(message.file))
                {
                    logObject["file"] = message.file;
                    logObject["line"] = message.line;
                    logObject["column"] = message.column;
                }

                logsArray.Add(logObject);
            }

            string summaryMessage = result.HasErrors
                                        ? $"Recompilation completed with {result.ErrorsCount} error(s) and {result.WarningsCount} warning(s)"
                                        : $"Successfully recompiled all scripts with {result.WarningsCount} warning(s)";

            summaryMessage += $" (returnWithLogs: {request.ReturnWithLogs}, logsLimit: {request.LogsLimit})";

            var response = new JObject 
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = summaryMessage,
                ["logs"] = logsArray
            };

            request.CompletionSource.SetResult(response);
        }

        /// <summary>
        /// Helper method to safely extract integer parameters with default values
        /// </summary>
        /// <param name="parameters">JObject containing parameters</param>
        /// <param name="key">Parameter key to extract</param>
        /// <param name="defaultValue">Default value if parameter is missing or invalid</param>
        /// <returns>Extracted integer value or default</returns>
        private static int GetIntParameter(JObject parameters, string key, int defaultValue)
        {
            if (parameters?[key] != null && int.TryParse(parameters[key].ToString(), out int value))
                return value;
            return defaultValue;
        }

        /// <summary>
        /// Helper method to safely extract boolean parameters with default values
        /// </summary>
        /// <param name="parameters">JObject containing parameters</param>
        /// <param name="key">Parameter key to extract</param>
        /// <param name="defaultValue">Default value if parameter is missing or invalid</param>
        /// <returns>Extracted boolean value or default</returns>
        private static bool GetBoolParameter(JObject parameters, string key, bool defaultValue)
        {
            if (parameters?[key] != null && bool.TryParse(parameters[key].ToString(), out bool value))
                return value;
            return defaultValue;
        }
    }
}