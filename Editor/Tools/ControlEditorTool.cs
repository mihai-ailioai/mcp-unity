using System;
using System.Threading.Tasks;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool to control Unity editor play mode state.
    /// </summary>
    public class ControlEditorTool : McpToolBase
    {
        public ControlEditorTool()
        {
            Name = "control_editor";
            Description = "Controls Unity editor play mode state (play, pause, unpause, stop, step)";
            IsAsync = true;
        }

        public override JObject Execute(JObject parameters)
        {
            throw new NotImplementedException("Use ExecuteAsync for control_editor.");
        }

        public override void ExecuteAsync(JObject parameters, TaskCompletionSource<JObject> tcs)
        {
            try
            {
                string action = parameters?["action"]?.ToString();
                if (string.IsNullOrWhiteSpace(action))
                {
                    tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                        "Missing required parameter 'action'. Allowed values: play, pause, unpause, stop, step.",
                        "validation_error"
                    ));
                    return;
                }

                switch (action)
                {
                    case "play":
                        if (EditorApplication.isPlaying)
                        {
                            tcs.SetResult(CreateStateResponse("Editor is already in play mode.", false));
                            return;
                        }

                        EditorApplication.isPlaying = true;
                        tcs.SetResult(CreateStateResponse("Entering play mode."));
                        return;

                    case "pause":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot pause because the editor is not in play mode.",
                                "invalid_state"
                            ));
                            return;
                        }

                        if (EditorApplication.isPaused)
                        {
                            tcs.SetResult(CreateStateResponse("Editor is already paused.", false));
                            return;
                        }

                        EditorApplication.isPaused = true;
                        tcs.SetResult(CreateStateResponse("Editor paused."));
                        return;

                    case "unpause":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot unpause because the editor is not in play mode.",
                                "invalid_state"
                            ));
                            return;
                        }

                        if (!EditorApplication.isPaused)
                        {
                            tcs.SetResult(CreateStateResponse("Editor is already unpaused.", false));
                            return;
                        }

                        EditorApplication.isPaused = false;
                        tcs.SetResult(CreateStateResponse("Editor unpaused."));
                        return;

                    case "stop":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(CreateStateResponse("Editor is already stopped.", false));
                            return;
                        }

                        EditorApplication.isPlaying = false;
                        tcs.SetResult(CreateStateResponse("Stopping play mode."));
                        return;

                    case "step":
                        if (!EditorApplication.isPlaying)
                        {
                            tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                                "Cannot step because the editor is not in play mode.",
                                "invalid_state"
                            ));
                            return;
                        }

                        EditorApplication.Step();
                        tcs.SetResult(CreateStateResponse("Advanced one frame."));
                        return;

                    default:
                        tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                            $"Invalid action '{action}'. Allowed values: play, pause, unpause, stop, step.",
                            "validation_error"
                        ));
                        return;
                }
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"Error in control_editor tool: {ex.Message}\\n{ex.StackTrace}");
                tcs.SetResult(McpUnity.Unity.McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to control editor state: {ex.Message}",
                    "execution_error"
                ));
            }
        }

        private static JObject CreateStateResponse(string message, bool stateChanged = true)
        {
            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = message,
                ["stateChanged"] = stateChanged,
                ["editorState"] = new JObject
                {
                    ["isPlaying"] = EditorApplication.isPlaying,
                    ["isPaused"] = EditorApplication.isPaused,
                    ["isCompiling"] = EditorApplication.isCompiling
                }
            };
        }
    }
}
