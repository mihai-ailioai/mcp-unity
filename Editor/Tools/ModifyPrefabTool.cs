using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEditor;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using Unity.EditorCoroutines.Editor;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for modifying a prefab asset headlessly by executing batched operations
    /// against its contents. Uses PrefabUtility.LoadPrefabContents for an isolated
    /// editing context — no Prefab Mode or scene instantiation needed.
    /// Operations use the same format as batch_execute and are dispatched to existing tools.
    /// </summary>
    public class ModifyPrefabTool : McpToolBase
    {
        private readonly McpUnityServer _server;

        /// <summary>
        /// Tools that are not allowed inside modify_prefab because they affect
        /// scenes, project state, or other resources outside the prefab editing context.
        /// </summary>
        private static readonly HashSet<string> BlockedTools = new HashSet<string>
        {
            "batch_execute",
            "modify_prefab",
            "create_scene",
            "load_scene",
            "save_scene",
            "delete_scene",
            "unload_scene",
            "add_asset_to_scene",
            "add_package",
            "run_tests",
            "recompile_scripts",
            "execute_menu_item",
            "save_as_prefab",
            "create_prefab",
            "move_asset",
            "rename_asset",
            "copy_asset",
            "delete_asset",
        };

        public ModifyPrefabTool(McpUnityServer server)
        {
            _server = server;
            Name = "modify_prefab";
            Description = "Modify a prefab asset headlessly by executing batched operations against its contents. " +
                          "Uses an isolated editing context — no Prefab Mode or scene instantiation needed. " +
                          "Operations use the same format as batch_execute: [{\"tool\": \"update_component\", \"params\": {...}}, ...]. " +
                          "All objectPath references in operations resolve against the prefab hierarchy. " +
                          "Changes are saved automatically if any operation succeeds. " +
                          "Set 'variantPath' to create a prefab variant from the source assetPath before applying operations. " +
                          "When creating a variant, 'operations' is optional (variant is created even with no operations). " +
                          "Allowed sub-tools: update_component, remove_component, update_gameobject, duplicate_gameobject, " +
                          "delete_gameobject, reparent_gameobject, create_primitive, set_rect_transform, get_gameobject, " +
                          "select_gameobject, create_material, assign_material, modify_material, get_material_info, " +
                          "create_tag, send_console_log, get_console_logs, create_scriptable_object, get_scriptable_object, " +
                          "update_scriptable_object, get_import_settings, update_import_settings, get_scene_info, get_prefab_info.";
            IsAsync = true;
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            EditorCoroutineUtility.StartCoroutineOwnerless(ExecuteModifyPrefabCoroutine(parameters, tcs));
        }

        private IEnumerator ExecuteModifyPrefabCoroutine(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            // Validate assetPath
            string assetPath = parameters?["assetPath"]?.ToObject<string>();
            if (string.IsNullOrEmpty(assetPath))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Missing required parameter: assetPath",
                    "validation_error"
                ));
                yield break;
            }

            if (!assetPath.StartsWith("Assets/"))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "assetPath must start with 'Assets/'",
                    "validation_error"
                ));
                yield break;
            }

            if (!assetPath.EndsWith(".prefab"))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "assetPath must end with '.prefab'",
                    "validation_error"
                ));
                yield break;
            }

            // Verify the asset exists
            var assetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            if (assetType == null)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"No asset found at path: {assetPath}",
                    "not_found_error"
                ));
                yield break;
            }

            // Check for variant creation
            string variantPath = parameters["variantPath"]?.ToObject<string>();
            bool creatingVariant = !string.IsNullOrEmpty(variantPath);

            // Validate operations — required unless creating a variant
            JArray operations = parameters["operations"] as JArray;
            if (!creatingVariant && (operations == null || operations.Count == 0))
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "The 'operations' array is required and must contain at least one operation (unless 'variantPath' is specified).",
                    "validation_error"
                ));
                yield break;
            }

            if (operations != null && operations.Count > 100)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Maximum of 100 operations allowed per modify_prefab call.",
                    "validation_error"
                ));
                yield break;
            }

            bool stopOnError = parameters["stopOnError"]?.ToObject<bool?>() ?? true;

            // Guard against concurrent headless prefab editing
            if (PrefabStageUtils.IsInHeadlessPrefabContext())
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    "Another modify_prefab operation is already in progress. Wait for it to complete before starting a new one.",
                    "concurrent_error"
                ));
                yield break;
            }

            // Create variant if requested
            string targetPath = assetPath;
            if (creatingVariant)
            {
                if (!variantPath.StartsWith("Assets/"))
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        "variantPath must start with 'Assets/'",
                        "validation_error"
                    ));
                    yield break;
                }

                if (!variantPath.EndsWith(".prefab"))
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        "variantPath must end with '.prefab'",
                        "validation_error"
                    ));
                    yield break;
                }

                // Check if variant already exists
                if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(variantPath, AssetPathToGUIDOptions.OnlyExistingAssets)))
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        $"Asset already exists at variantPath: {variantPath}. Use assetPath to modify an existing prefab.",
                        "validation_error"
                    ));
                    yield break;
                }

                // Ensure target directory exists
                string directory = System.IO.Path.GetDirectoryName(variantPath);
                if (!string.IsNullOrEmpty(directory) && !System.IO.Directory.Exists(directory))
                {
                    System.IO.Directory.CreateDirectory(directory);
                    AssetDatabase.Refresh();
                }

                // Create the variant: instantiate source prefab -> save as new asset -> destroy temp instance
                try
                {
                    var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (sourcePrefab == null)
                    {
                        tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Failed to load source prefab at: {assetPath}",
                            "prefab_load_error"
                        ));
                        yield break;
                    }

                    // InstantiatePrefab creates a prefab instance; SaveAsPrefabAsset on a prefab instance creates a variant
                    var tempInstance = (GameObject)PrefabUtility.InstantiatePrefab(sourcePrefab);
                    bool saveSuccess;
                    PrefabUtility.SaveAsPrefabAsset(tempInstance, variantPath, out saveSuccess);
                    UnityEngine.Object.DestroyImmediate(tempInstance);

                    if (!saveSuccess)
                    {
                        tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                            $"Failed to create prefab variant at: {variantPath}",
                            "prefab_save_error"
                        ));
                        yield break;
                    }

                    McpLogger.LogInfo($"Created prefab variant at {variantPath} from {assetPath}");
                }
                catch (Exception ex)
                {
                    tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to create prefab variant: {ex.Message}",
                        "prefab_save_error"
                    ));
                    yield break;
                }

                targetPath = variantPath;
            }

            // If no operations, return success (variant-only creation)
            if (operations == null || operations.Count == 0)
            {
                tcs.SetResult(new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Created prefab variant at {variantPath} from {assetPath}.",
                    ["assetPath"] = targetPath,
                    ["isVariant"] = true,
                    ["sourceAssetPath"] = assetPath,
                    ["results"] = new JArray(),
                    ["summary"] = new JObject
                    {
                        ["total"] = 0,
                        ["succeeded"] = 0,
                        ["failed"] = 0,
                        ["executed"] = 0
                    }
                });
                yield break;
            }

            // Load prefab contents into isolated editing context
            GameObject root;
            try
            {
                root = PrefabUtility.LoadPrefabContents(targetPath);
            }
            catch (Exception ex)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to load prefab contents: {ex.Message}",
                    "prefab_load_error"
                ));
                yield break;
            }

            if (root == null)
            {
                tcs.SetResult(McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to load prefab contents at path: {targetPath}",
                    "prefab_load_error"
                ));
                yield break;
            }

            JArray results = new JArray();
            int succeeded = 0;
            int failed = 0;

            try
            {
                // Set headless prefab context so existing tools resolve against this root
                PrefabStageUtils.HeadlessPrefabRoot = root;

                for (int i = 0; i < operations.Count; i++)
                {
                    JObject operation = operations[i] as JObject;
                    if (operation == null)
                    {
                        results.Add(CreateOperationResult(i, null, false, null, "Invalid operation format"));
                        failed++;

                        if (stopOnError) break;
                        continue;
                    }

                    string toolName = operation["tool"]?.ToString();
                    JObject toolParams = operation["params"] as JObject ?? new JObject();
                    string operationId = operation["id"]?.ToString() ?? i.ToString();

                    // Validate tool name
                    if (string.IsNullOrEmpty(toolName))
                    {
                        results.Add(CreateOperationResult(i, operationId, false, null, "Missing 'tool' name in operation"));
                        failed++;

                        if (stopOnError) break;
                        continue;
                    }

                    // Block tools that affect scenes/project state outside the prefab context
                    if (BlockedTools.Contains(toolName))
                    {
                        results.Add(CreateOperationResult(i, operationId, false, null,
                            $"Tool '{toolName}' is not allowed inside modify_prefab (it affects scenes or project state outside the prefab editing context)"));
                        failed++;

                        if (stopOnError) break;
                        continue;
                    }

                    // Get the tool
                    if (!_server.TryGetTool(toolName, out McpToolBase tool))
                    {
                        results.Add(CreateOperationResult(i, operationId, false, null, $"Unknown tool: {toolName}"));
                        failed++;

                        if (stopOnError) break;
                        continue;
                    }

                    // Execute the tool
                    JObject toolResult = null;
                    Exception toolException = null;

                    if (tool.IsAsync)
                    {
                        var toolTcs = new TaskCompletionSource<JObject>();

                        try
                        {
                            tool.ExecuteAsync(toolParams, toolTcs);
                        }
                        catch (Exception ex)
                        {
                            toolException = ex;
                        }

                        // Wait for async tool completion
                        if (toolException == null)
                        {
                            while (!toolTcs.Task.IsCompleted)
                            {
                                yield return null;
                            }

                            if (toolTcs.Task.IsFaulted)
                            {
                                toolException = toolTcs.Task.Exception?.InnerException ?? toolTcs.Task.Exception;
                            }
                            else
                            {
                                toolResult = toolTcs.Task.Result;
                            }
                        }
                    }
                    else
                    {
                        try
                        {
                            toolResult = tool.Execute(toolParams);
                        }
                        catch (Exception ex)
                        {
                            toolException = ex;
                        }
                    }

                    // Process result
                    if (toolException != null)
                    {
                        results.Add(CreateOperationResult(i, operationId, false, null, toolException.Message));
                        failed++;

                        if (stopOnError) break;
                    }
                    else if (toolResult != null)
                    {
                        bool isError = toolResult["error"] != null;
                        bool isSuccess = toolResult["success"]?.ToObject<bool?>() ?? !isError;

                        if (isSuccess && !isError)
                        {
                            results.Add(CreateOperationResult(i, operationId, true, toolResult, null));
                            succeeded++;
                        }
                        else
                        {
                            string errorMessage = toolResult["error"]?["message"]?.ToString()
                                ?? toolResult["message"]?.ToString()
                                ?? "Tool execution failed";
                            results.Add(CreateOperationResult(i, operationId, false, toolResult, errorMessage));
                            failed++;

                            if (stopOnError) break;
                        }
                    }
                    else
                    {
                        results.Add(CreateOperationResult(i, operationId, false, null, "Tool returned null result"));
                        failed++;

                        if (stopOnError) break;
                    }

                    // Yield to allow Unity to process
                    yield return null;
                }

                // Save if any operations succeeded
                if (succeeded > 0)
                {
                    try
                    {
                        PrefabUtility.SaveAsPrefabAsset(root, targetPath);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"Failed to save prefab at {targetPath}: {ex.Message}");
                        tcs.SetResult(new JObject
                        {
                            ["success"] = false,
                            ["type"] = "text",
                            ["message"] = $"Operations executed but failed to save prefab: {ex.Message}",
                            ["assetPath"] = targetPath,
                            ["results"] = results,
                            ["summary"] = new JObject
                            {
                                ["total"] = operations.Count,
                                ["succeeded"] = succeeded,
                                ["failed"] = failed,
                                ["executed"] = succeeded + failed
                            }
                        });
                        yield break;
                    }
                }
            }
            finally
            {
                // Always clean up: clear context and unload prefab contents
                PrefabStageUtils.HeadlessPrefabRoot = null;

                try
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
                catch (Exception ex)
                {
                    McpLogger.LogError($"Failed to unload prefab contents: {ex.Message}");
                }
            }

            // Build response
            string message;
            if (failed == 0)
            {
                message = $"Successfully modified prefab at {targetPath}. {succeeded}/{operations.Count} operations executed.";
            }
            else if (stopOnError)
            {
                message = $"Prefab modification stopped on error at {targetPath}. {succeeded}/{operations.Count} operations succeeded before failure.";
            }
            else
            {
                message = $"Prefab modification completed with errors at {targetPath}. {succeeded}/{operations.Count} operations succeeded, {failed} failed.";
            }

            var response = new JObject
            {
                ["success"] = failed == 0,
                ["type"] = "text",
                ["message"] = message,
                ["assetPath"] = targetPath,
                ["results"] = results,
                ["summary"] = new JObject
                {
                    ["total"] = operations.Count,
                    ["succeeded"] = succeeded,
                    ["failed"] = failed,
                    ["executed"] = succeeded + failed
                }
            };

            if (creatingVariant)
            {
                response["isVariant"] = true;
                response["sourceAssetPath"] = assetPath;
            }

            tcs.SetResult(response);
        }

        private JObject CreateOperationResult(int index, string id, bool success, JObject result, string error)
        {
            var operationResult = new JObject
            {
                ["index"] = index,
                ["id"] = id ?? index.ToString(),
                ["success"] = success
            };

            if (success && result != null)
            {
                operationResult["result"] = result;
            }
            else if (!success)
            {
                operationResult["error"] = error ?? "Unknown error";
            }

            return operationResult;
        }
    }
}
