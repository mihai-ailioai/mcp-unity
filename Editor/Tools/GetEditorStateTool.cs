using System;
using McpUnity.Tools;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for getting a snapshot of the Unity editor state.
    /// </summary>
    public class GetEditorStateTool : McpToolBase
    {
        public GetEditorStateTool()
        {
            Name = "get_editor_state";
            Description = "Gets a snapshot of the Unity editor state including play mode, compilation status, active scene, platform, and version";
        }

        public override JObject Execute(JObject parameters)
        {
            try
            {
                bool isPlaying = EditorApplication.isPlaying;
                bool isPaused = EditorApplication.isPaused;
                bool isCompiling = EditorApplication.isCompiling;

                string playModeState = !isPlaying
                    ? "Stopped"
                    : isPaused
                        ? "Paused"
                        : "Playing";

                Scene activeScene = SceneManager.GetActiveScene();
                string buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString();

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = $"Editor state: {playModeState}, Platform: {buildTarget}",
                    ["editorState"] = new JObject
                    {
                        ["isPlaying"] = isPlaying,
                        ["isPaused"] = isPaused,
                        ["isCompiling"] = isCompiling,
                        ["playModeState"] = playModeState,
                        ["activeScene"] = new JObject
                        {
                            ["name"] = activeScene.name,
                            ["path"] = activeScene.path,
                            ["isDirty"] = activeScene.isDirty,
                            ["buildIndex"] = activeScene.buildIndex
                        },
                        ["platform"] = buildTarget,
                        ["unityVersion"] = Application.unityVersion
                    }
                };
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error in get_editor_state tool: {ex.Message}\\n{ex.StackTrace}");
                return McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to get editor state: {ex.Message}",
                    "editor_state_error"
                );
            }
        }
    }
}
