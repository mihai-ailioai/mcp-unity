using System;
using System.Collections.Generic;
using System.Linq;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace McpUnity.Tools
{
    public class SetupAnimatorControllerTool : McpToolBase
    {
        public SetupAnimatorControllerTool()
        {
            Name = "setup_animator_controller";
            Description = "Creates or updates AnimatorController assets, including parameters, layers, states, and transitions";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>();
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'assetPath' not provided",
                    "validation_error"
                );
            }

            try
            {
                assetPath = NormalizeControllerPath(assetPath);
                AnimatorController controller = AssetDatabase.LoadAssetAtPath<AnimatorController>(assetPath);
                bool isNew = controller == null;

                if (isNew)
                {
                    string directory = System.IO.Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
                    if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                    {
                        MoveAssetTool.CreateFolderRecursive(directory);
                    }

                    controller = AnimatorController.CreateAnimatorControllerAtPath(assetPath);
                    if (controller == null)
                    {
                        throw new InvalidOperationException($"Failed to create AnimatorController at '{assetPath}'");
                    }
                }

                Undo.RecordObject(controller, $"Setup Animator Controller {controller.name}");

                int removedParameters = RemoveParameters(controller, parameters["removeParameters"] as JArray);
                int removedLayers = RemoveLayers(controller, parameters["removeLayers"] as JArray);
                int removedTransitions = RemoveTransitions(controller, parameters["removeTransitions"] as JArray);
                int removedStates = RemoveStates(controller, parameters["removeStates"] as JArray);

                int upsertedParameters = UpsertParameters(controller, parameters["parameters"] as JArray);
                int upsertedLayers = UpsertLayers(controller, parameters["layers"] as JArray);
                int upsertedStates = UpsertStates(controller, parameters["states"] as JArray);
                int addedTransitions = AddTransitions(controller, parameters["transitions"] as JArray);

                EditorUtility.SetDirty(controller);
                AssetDatabase.SaveAssets();

                string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = isNew
                        ? $"Successfully created animator controller at '{assetPath}'"
                        : $"Successfully updated animator controller at '{assetPath}'",
                    ["assetPath"] = assetPath,
                    ["assetGuid"] = assetGuid,
                    ["isNew"] = isNew,
                    ["parametersProcessed"] = removedParameters + upsertedParameters,
                    ["layersProcessed"] = removedLayers + upsertedLayers,
                    ["statesProcessed"] = removedStates + upsertedStates,
                    ["transitionsProcessed"] = removedTransitions + addedTransitions
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to setup animator controller: {ex.Message}",
                    "execution_error"
                );
            }
        }

        private static string NormalizeControllerPath(string assetPath)
        {
            string normalized = assetPath.Trim().Replace("\\", "/");
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimStart('/');
                normalized = "Assets/" + normalized;
            }

            if (!normalized.EndsWith(".controller", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".controller";
            }

            return normalized;
        }

        private static string NormalizeClipPath(string clipPath)
        {
            string normalized = clipPath.Trim().Replace("\\", "/");
            if (!normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                normalized = normalized.TrimStart('/');
                normalized = "Assets/" + normalized;
            }

            if (!normalized.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
            {
                normalized += ".anim";
            }

            return normalized;
        }

        private int RemoveParameters(AnimatorController controller, JArray removeParameters)
        {
            if (removeParameters == null || removeParameters.Count == 0)
            {
                return 0;
            }

            int removedCount = 0;
            foreach (JToken token in removeParameters)
            {
                JObject removeObject = token as JObject;
                if (removeObject == null)
                {
                    continue;
                }

                string name = removeObject["name"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Each removeParameters entry must include 'name'");
                }

                AnimatorControllerParameter parameter = controller.parameters.FirstOrDefault(p => p.name == name);
                if (parameter != null)
                {
                    controller.RemoveParameter(parameter);
                    removedCount++;
                }
            }

            return removedCount;
        }

        private int RemoveLayers(AnimatorController controller, JArray removeLayers)
        {
            if (removeLayers == null || removeLayers.Count == 0)
            {
                return 0;
            }

            HashSet<string> namesToRemove = new HashSet<string>(StringComparer.Ordinal);
            foreach (JToken token in removeLayers)
            {
                JObject removeObject = token as JObject;
                if (removeObject == null)
                {
                    continue;
                }

                string name = removeObject["name"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Each removeLayers entry must include 'name'");
                }

                namesToRemove.Add(name);
            }

            int removedCount = 0;
            for (int index = controller.layers.Length - 1; index >= 0; index--)
            {
                AnimatorControllerLayer layer = controller.layers[index];
                if (!namesToRemove.Contains(layer.name))
                {
                    continue;
                }

                if (index == 0)
                {
                    throw new ArgumentException("Cannot remove Base Layer (index 0)");
                }

                controller.RemoveLayer(index);
                removedCount++;
            }

            return removedCount;
        }

        private int RemoveTransitions(AnimatorController controller, JArray removeTransitions)
        {
            if (removeTransitions == null || removeTransitions.Count == 0)
            {
                return 0;
            }

            int removedCount = 0;
            foreach (JToken token in removeTransitions)
            {
                JObject removeObject = token as JObject;
                if (removeObject == null)
                {
                    continue;
                }

                string fromState = removeObject["fromState"]?.ToObject<string>();
                string toState = removeObject["toState"]?.ToObject<string>();
                int layerIndex = removeObject["layerIndex"]?.ToObject<int?>() ?? 0;

                if (string.IsNullOrWhiteSpace(fromState) || string.IsNullOrWhiteSpace(toState))
                {
                    throw new ArgumentException("Each removeTransitions entry must include 'fromState' and 'toState'");
                }

                AnimatorStateMachine stateMachine = GetStateMachine(controller, layerIndex);
                if (IsAnyStateName(fromState))
                {
                    List<AnimatorStateTransition> toRemove = new List<AnimatorStateTransition>();
                    foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
                    {
                        if (transition.destinationState != null && transition.destinationState.name == toState)
                        {
                            toRemove.Add(transition);
                        }
                    }

                    foreach (AnimatorStateTransition transition in toRemove)
                    {
                        stateMachine.RemoveAnyStateTransition(transition);
                        removedCount++;
                    }

                    continue;
                }

                AnimatorState sourceState = FindState(stateMachine, fromState);
                if (sourceState == null)
                {
                    continue;
                }

                List<AnimatorStateTransition> removeList = new List<AnimatorStateTransition>();
                foreach (AnimatorStateTransition transition in sourceState.transitions)
                {
                    if (transition.destinationState != null && transition.destinationState.name == toState)
                    {
                        removeList.Add(transition);
                    }
                }

                foreach (AnimatorStateTransition transition in removeList)
                {
                    sourceState.RemoveTransition(transition);
                    removedCount++;
                }
            }

            return removedCount;
        }

        private int RemoveStates(AnimatorController controller, JArray removeStates)
        {
            if (removeStates == null || removeStates.Count == 0)
            {
                return 0;
            }

            int removedCount = 0;
            foreach (JToken token in removeStates)
            {
                JObject removeObject = token as JObject;
                if (removeObject == null)
                {
                    continue;
                }

                string name = removeObject["name"]?.ToObject<string>();
                int layerIndex = removeObject["layerIndex"]?.ToObject<int?>() ?? 0;
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Each removeStates entry must include 'name'");
                }

                AnimatorStateMachine stateMachine = GetStateMachine(controller, layerIndex);
                AnimatorState state = FindState(stateMachine, name);
                if (state != null)
                {
                    stateMachine.RemoveState(state);
                    removedCount++;
                }
            }

            return removedCount;
        }

        private int UpsertParameters(AnimatorController controller, JArray parameterArray)
        {
            if (parameterArray == null || parameterArray.Count == 0)
            {
                return 0;
            }

            int processedCount = 0;
            foreach (JToken token in parameterArray)
            {
                JObject parameterObject = token as JObject;
                if (parameterObject == null)
                {
                    continue;
                }

                string name = parameterObject["name"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Each parameters entry must include 'name'");
                }

                AnimatorControllerParameterType type = ParseParameterType(parameterObject["type"]?.ToObject<string>() ?? "float");
                AnimatorControllerParameter existing = controller.parameters.FirstOrDefault(p => p.name == name);
                JToken defaultToken = parameterObject["defaultValue"];

                AnimatorControllerParameter replacement = BuildParameter(name, type, defaultToken, existing);
                if (existing != null)
                {
                    controller.RemoveParameter(existing);
                }

                controller.AddParameter(replacement);
                processedCount++;
            }

            return processedCount;
        }

        private int UpsertLayers(AnimatorController controller, JArray layerArray)
        {
            if (layerArray == null || layerArray.Count == 0)
            {
                return 0;
            }

            int processedCount = 0;
            foreach (JToken token in layerArray)
            {
                JObject layerObject = token as JObject;
                if (layerObject == null)
                {
                    continue;
                }

                string layerName = layerObject["name"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(layerName))
                {
                    throw new ArgumentException("Each layers entry must include 'name'");
                }

                float weight = layerObject["weight"]?.ToObject<float?>() ?? 1f;
                AnimatorLayerBlendingMode blendingMode = ParseLayerBlendingMode(layerObject["blendingMode"]?.ToObject<string>() ?? "override");

                int index = FindLayerIndex(controller, layerName);
                if (index >= 0)
                {
                    AnimatorControllerLayer layer = controller.layers[index];
                    layer.defaultWeight = weight;
                    layer.blendingMode = blendingMode;
                    controller.SetLayer(index, layer);
                    processedCount++;
                    continue;
                }

                AnimatorStateMachine stateMachine = new AnimatorStateMachine
                {
                    name = $"{layerName} StateMachine",
                    hideFlags = HideFlags.HideInHierarchy
                };
                AssetDatabase.AddObjectToAsset(stateMachine, controller);

                AnimatorControllerLayer newLayer = new AnimatorControllerLayer
                {
                    name = layerName,
                    defaultWeight = weight,
                    blendingMode = blendingMode,
                    stateMachine = stateMachine
                };

                controller.AddLayer(newLayer);
                processedCount++;
            }

            return processedCount;
        }

        private int UpsertStates(AnimatorController controller, JArray stateArray)
        {
            if (stateArray == null || stateArray.Count == 0)
            {
                return 0;
            }

            int processedCount = 0;
            foreach (JToken token in stateArray)
            {
                JObject stateObject = token as JObject;
                if (stateObject == null)
                {
                    continue;
                }

                string name = stateObject["name"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(name))
                {
                    throw new ArgumentException("Each states entry must include 'name'");
                }

                int layerIndex = stateObject["layerIndex"]?.ToObject<int?>() ?? 0;
                float speed = stateObject["speed"]?.ToObject<float?>() ?? 1f;
                bool isDefault = stateObject["isDefault"]?.ToObject<bool?>() ?? false;

                AnimatorStateMachine stateMachine = GetStateMachine(controller, layerIndex);
                AnimatorState state = FindState(stateMachine, name);
                if (state == null)
                {
                    state = stateMachine.AddState(name);
                }

                state.speed = speed;

                if (stateObject.TryGetValue("clipPath", out JToken clipPathToken))
                {
                    string clipPath = clipPathToken?.ToObject<string>();
                    if (string.IsNullOrWhiteSpace(clipPath))
                    {
                        state.motion = null;
                    }
                    else
                    {
                        string normalizedClipPath = NormalizeClipPath(clipPath);
                        AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(normalizedClipPath);
                        if (clip == null)
                        {
                            throw new ArgumentException($"AnimationClip not found at '{normalizedClipPath}' for state '{name}'");
                        }

                        state.motion = clip;
                    }
                }

                if (isDefault)
                {
                    stateMachine.defaultState = state;
                }

                processedCount++;
            }

            return processedCount;
        }

        private int AddTransitions(AnimatorController controller, JArray transitionArray)
        {
            if (transitionArray == null || transitionArray.Count == 0)
            {
                return 0;
            }

            int processedCount = 0;
            foreach (JToken token in transitionArray)
            {
                JObject transitionObject = token as JObject;
                if (transitionObject == null)
                {
                    continue;
                }

                string fromStateName = transitionObject["fromState"]?.ToObject<string>();
                string toStateName = transitionObject["toState"]?.ToObject<string>();
                int layerIndex = transitionObject["layerIndex"]?.ToObject<int?>() ?? 0;

                if (string.IsNullOrWhiteSpace(fromStateName) || string.IsNullOrWhiteSpace(toStateName))
                {
                    throw new ArgumentException("Each transitions entry must include 'fromState' and 'toState'");
                }

                AnimatorStateMachine stateMachine = GetStateMachine(controller, layerIndex);
                AnimatorState destinationState = FindState(stateMachine, toStateName);
                if (destinationState == null)
                {
                    throw new ArgumentException($"Could not find destination state '{toStateName}' on layer index {layerIndex}");
                }

                AnimatorStateTransition transition;
                if (IsAnyStateName(fromStateName))
                {
                    transition = stateMachine.AddAnyStateTransition(destinationState);
                }
                else
                {
                    AnimatorState sourceState = FindState(stateMachine, fromStateName);
                    if (sourceState == null)
                    {
                        throw new ArgumentException($"Could not find source state '{fromStateName}' on layer index {layerIndex}");
                    }

                    transition = sourceState.AddTransition(destinationState);
                }

                if (transitionObject["hasExitTime"] != null)
                {
                    transition.hasExitTime = transitionObject["hasExitTime"].ToObject<bool>();
                }

                if (transitionObject["exitTime"] != null)
                {
                    transition.exitTime = transitionObject["exitTime"].ToObject<float>();
                }

                if (transitionObject["duration"] != null)
                {
                    transition.duration = transitionObject["duration"].ToObject<float>();
                }

                JArray conditions = transitionObject["conditions"] as JArray;
                if (conditions != null)
                {
                    foreach (JToken conditionToken in conditions)
                    {
                        JObject conditionObject = conditionToken as JObject;
                        if (conditionObject == null)
                        {
                            continue;
                        }

                        string parameterName = conditionObject["parameter"]?.ToObject<string>();
                        string modeName = conditionObject["mode"]?.ToObject<string>();
                        if (string.IsNullOrWhiteSpace(parameterName) || string.IsNullOrWhiteSpace(modeName))
                        {
                            throw new ArgumentException("Each transition condition must include 'parameter' and 'mode'");
                        }

                        AnimatorConditionMode mode = ParseConditionMode(modeName);
                        float threshold = conditionObject["threshold"]?.ToObject<float?>() ?? 0f;
                        transition.AddCondition(mode, threshold, parameterName);
                    }
                }

                processedCount++;
            }

            return processedCount;
        }

        private static AnimatorControllerParameter BuildParameter(
            string name,
            AnimatorControllerParameterType type,
            JToken defaultToken,
            AnimatorControllerParameter existing)
        {
            AnimatorControllerParameter parameter = new AnimatorControllerParameter
            {
                name = name,
                type = type
            };

            switch (type)
            {
                case AnimatorControllerParameterType.Float:
                    parameter.defaultFloat = defaultToken != null
                        ? defaultToken.ToObject<float>()
                        : (existing != null ? existing.defaultFloat : 0f);
                    break;
                case AnimatorControllerParameterType.Int:
                    parameter.defaultInt = defaultToken != null
                        ? defaultToken.ToObject<int>()
                        : (existing != null ? existing.defaultInt : 0);
                    break;
                case AnimatorControllerParameterType.Bool:
                    parameter.defaultBool = defaultToken != null
                        ? defaultToken.ToObject<bool>()
                        : existing != null && existing.defaultBool;
                    break;
            }

            return parameter;
        }

        private static bool IsAnyStateName(string stateName)
        {
            return string.Equals(stateName, "Any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(stateName, "AnyState", StringComparison.OrdinalIgnoreCase);
        }

        private static int FindLayerIndex(AnimatorController controller, string layerName)
        {
            for (int index = 0; index < controller.layers.Length; index++)
            {
                if (controller.layers[index].name == layerName)
                {
                    return index;
                }
            }

            return -1;
        }

        private static AnimatorStateMachine GetStateMachine(AnimatorController controller, int layerIndex)
        {
            if (layerIndex < 0 || layerIndex >= controller.layers.Length)
            {
                throw new ArgumentException($"Layer index {layerIndex} is out of range");
            }

            AnimatorStateMachine stateMachine = controller.layers[layerIndex].stateMachine;
            if (stateMachine == null)
            {
                throw new InvalidOperationException($"Layer index {layerIndex} does not have a valid state machine");
            }

            return stateMachine;
        }

        private static AnimatorState FindState(AnimatorStateMachine stateMachine, string stateName)
        {
            ChildAnimatorState match = stateMachine.states.FirstOrDefault(s => s.state != null && s.state.name == stateName);
            return match.state;
        }

        private static AnimatorControllerParameterType ParseParameterType(string type)
        {
            switch (type?.Trim().ToLowerInvariant())
            {
                case "float":
                    return AnimatorControllerParameterType.Float;
                case "int":
                    return AnimatorControllerParameterType.Int;
                case "bool":
                    return AnimatorControllerParameterType.Bool;
                case "trigger":
                    return AnimatorControllerParameterType.Trigger;
                default:
                    throw new ArgumentException($"Unsupported parameter type '{type}'. Expected one of: float, int, bool, trigger");
            }
        }

        private static AnimatorLayerBlendingMode ParseLayerBlendingMode(string blendingMode)
        {
            switch (blendingMode?.Trim().ToLowerInvariant())
            {
                case "override":
                    return AnimatorLayerBlendingMode.Override;
                case "additive":
                    return AnimatorLayerBlendingMode.Additive;
                default:
                    throw new ArgumentException($"Unsupported blendingMode '{blendingMode}'. Expected 'override' or 'additive'");
            }
        }

        private static AnimatorConditionMode ParseConditionMode(string mode)
        {
            switch (mode?.Trim().ToLowerInvariant())
            {
                case "greater":
                    return AnimatorConditionMode.Greater;
                case "less":
                    return AnimatorConditionMode.Less;
                case "equals":
                    return AnimatorConditionMode.Equals;
                case "notequals":
                    return AnimatorConditionMode.NotEqual;
                case "if":
                    return AnimatorConditionMode.If;
                case "ifnot":
                    return AnimatorConditionMode.IfNot;
                default:
                    throw new ArgumentException($"Unsupported condition mode '{mode}'");
            }
        }
    }
}
