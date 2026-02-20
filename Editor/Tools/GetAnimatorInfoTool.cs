using System;
using System.Collections.Generic;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    public class GetAnimatorInfoTool : McpToolBase
    {
        public GetAnimatorInfoTool()
        {
            Name = "get_animator_info";
            Description = "Inspects an Animator component on a GameObject and returns controller parameters, layers, states, and transitions";
        }

        public override JObject Execute(JObject parameters)
        {
            int? instanceId = parameters["instanceId"]?.ToObject<int?>();
            string objectPath = parameters["objectPath"]?.ToObject<string>();
            bool includeClipDetails = parameters["includeClipDetails"]?.ToObject<bool?>() ?? false;

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'instanceId' or 'objectPath' must be provided",
                    "validation_error"
                );
            }

            GameObject gameObject = FindGameObject(instanceId, objectPath);
            if (gameObject == null)
            {
                string identifier = instanceId.HasValue
                    ? $"instanceId '{instanceId.Value}'"
                    : $"objectPath '{objectPath}'";
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject not found for {identifier}",
                    "not_found_error"
                );
            }

            Animator animator = gameObject.GetComponent<Animator>();
            if (animator == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject '{gameObject.name}' does not have an Animator component",
                    "component_error"
                );
            }

            RuntimeAnimatorController runtimeController = animator.runtimeAnimatorController;
            AnimatorController controller = runtimeController as AnimatorController;

            string controllerPath = null;
            string controllerGuid = null;
            JArray parameterArray = new JArray();
            JArray layerArray = new JArray();

            string message;
            if (controller == null)
            {
                if (runtimeController == null)
                {
                    message = $"Animator info retrieved for '{gameObject.name}'. No runtime animator controller is assigned.";
                }
                else
                {
                    message = $"Animator info retrieved for '{gameObject.name}'. Runtime controller '{runtimeController.name}' is not an AnimatorController.";
                }
            }
            else
            {
                controllerPath = AssetDatabase.GetAssetPath(controller);
                if (string.IsNullOrEmpty(controllerPath))
                {
                    controllerPath = null;
                }

                controllerGuid = !string.IsNullOrEmpty(controllerPath)
                    ? AssetDatabase.AssetPathToGUID(controllerPath)
                    : null;

                parameterArray = SerializeParameters(controller.parameters);
                layerArray = SerializeLayers(controller, includeClipDetails);
                message = $"Retrieved animator info for '{gameObject.name}'";
            }

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = message,
                ["gameObject"] = GameObjectToolUtils.GetGameObjectPath(gameObject),
                ["instanceId"] = gameObject.GetInstanceID(),
                ["controllerPath"] = controllerPath != null ? new JValue(controllerPath) : JValue.CreateNull(),
                ["controllerGuid"] = controllerGuid != null ? new JValue(controllerGuid) : JValue.CreateNull(),
                ["parameters"] = parameterArray,
                ["layers"] = layerArray
            };
        }

        private static GameObject FindGameObject(int? instanceId, string objectPath)
        {
            if (instanceId.HasValue)
            {
                return EditorUtility.InstanceIDToObject(instanceId.Value) as GameObject;
            }

            if (string.IsNullOrEmpty(objectPath))
            {
                return null;
            }

            GameObject gameObject = PrefabStageUtils.FindGameObject(objectPath);
            if (gameObject == null && !PrefabStageUtils.IsInPrefabStage())
            {
                gameObject = SerializedFieldUtils.FindGameObjectByPath(objectPath);
            }

            return gameObject;
        }

        private static JArray SerializeParameters(AnimatorControllerParameter[] parameters)
        {
            JArray result = new JArray();
            foreach (AnimatorControllerParameter parameter in parameters)
            {
                result.Add(new JObject
                {
                    ["name"] = parameter.name,
                    ["type"] = parameter.type.ToString().ToLowerInvariant(),
                    ["defaultValue"] = SerializeDefaultValue(parameter)
                });
            }

            return result;
        }

        private static JToken SerializeDefaultValue(AnimatorControllerParameter parameter)
        {
            switch (parameter.type)
            {
                case AnimatorControllerParameterType.Float:
                    return new JValue(parameter.defaultFloat);
                case AnimatorControllerParameterType.Int:
                    return new JValue(parameter.defaultInt);
                case AnimatorControllerParameterType.Bool:
                    return new JValue(parameter.defaultBool);
                case AnimatorControllerParameterType.Trigger:
                    return JValue.CreateNull();
                default:
                    return JValue.CreateNull();
            }
        }

        private static JArray SerializeLayers(AnimatorController controller, bool includeClipDetails)
        {
            JArray layers = new JArray();
            for (int index = 0; index < controller.layers.Length; index++)
            {
                AnimatorControllerLayer layer = controller.layers[index];
                AnimatorStateMachine stateMachine = layer.stateMachine;

                layers.Add(new JObject
                {
                    ["name"] = layer.name,
                    ["index"] = index,
                    ["weight"] = layer.defaultWeight,
                    ["blendingMode"] = layer.blendingMode.ToString().ToLowerInvariant(),
                    ["states"] = SerializeStates(stateMachine, includeClipDetails),
                    ["transitions"] = SerializeTransitions(stateMachine)
                });
            }

            return layers;
        }

        private static JArray SerializeStates(AnimatorStateMachine stateMachine, bool includeClipDetails)
        {
            JArray states = new JArray();
            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                AnimatorState state = childState.state;
                JObject stateObject = new JObject
                {
                    ["name"] = state.name,
                    ["speed"] = state.speed,
                    ["isDefault"] = stateMachine.defaultState == state
                };

                if (state.motion is AnimationClip animationClip)
                {
                    string clipPath = AssetDatabase.GetAssetPath(animationClip);
                    stateObject["clipPath"] = string.IsNullOrEmpty(clipPath)
                        ? JValue.CreateNull()
                        : new JValue(clipPath);

                    if (includeClipDetails)
                    {
                        stateObject["clipDetails"] = SerializeClipDetails(animationClip);
                    }
                }
                else if (state.motion is BlendTree blendTree)
                {
                    JObject blendTreeObject = new JObject
                    {
                        ["name"] = blendTree.name,
                        ["blendType"] = blendTree.blendType.ToString().ToLowerInvariant(),
                        ["blendParameter"] = blendTree.blendParameter,
                        ["children"] = SerializeBlendTreeChildren(blendTree, includeClipDetails)
                    };

                    if (!string.IsNullOrEmpty(blendTree.blendParameterY))
                    {
                        blendTreeObject["blendParameterY"] = blendTree.blendParameterY;
                    }

                    stateObject["blendTree"] = blendTreeObject;
                }

                states.Add(stateObject);
            }

            return states;
        }

        private static JArray SerializeBlendTreeChildren(BlendTree blendTree, bool includeClipDetails)
        {
            JArray children = new JArray();
            foreach (ChildMotion child in blendTree.children)
            {
                JObject childObject = new JObject
                {
                    ["threshold"] = child.threshold
                };

                if (child.motion is AnimationClip clip)
                {
                    string clipPath = AssetDatabase.GetAssetPath(clip);
                    childObject["clipPath"] = string.IsNullOrEmpty(clipPath)
                        ? JValue.CreateNull()
                        : new JValue(clipPath);

                    if (includeClipDetails)
                    {
                        childObject["clipDetails"] = SerializeClipDetails(clip);
                    }
                }
                else
                {
                    childObject["clipPath"] = JValue.CreateNull();
                    if (child.motion != null)
                    {
                        childObject["motion"] = child.motion.name;
                    }
                }

                children.Add(childObject);
            }

            return children;
        }

        private static JObject SerializeClipDetails(AnimationClip clip)
        {
            JArray curves = new JArray();
            EditorCurveBinding[] bindings = AnimationUtility.GetCurveBindings(clip);
            foreach (EditorCurveBinding binding in bindings)
            {
                curves.Add(new JObject
                {
                    ["propertyPath"] = binding.propertyName,
                    ["type"] = binding.type != null ? binding.type.Name : null,
                    ["relativePath"] = binding.path
                });
            }

            JArray events = new JArray();
            AnimationEvent[] clipEvents = AnimationUtility.GetAnimationEvents(clip);
            foreach (AnimationEvent animationEvent in clipEvents)
            {
                events.Add(new JObject
                {
                    ["functionName"] = animationEvent.functionName,
                    ["time"] = animationEvent.time,
                    ["floatParameter"] = animationEvent.floatParameter,
                    ["intParameter"] = animationEvent.intParameter,
                    ["stringParameter"] = animationEvent.stringParameter
                });
            }

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            return new JObject
            {
                ["length"] = clip.length,
                ["frameRate"] = clip.frameRate,
                ["loop"] = settings.loopTime,
                ["curves"] = curves,
                ["events"] = events
            };
        }

        private static JArray SerializeTransitions(AnimatorStateMachine stateMachine)
        {
            JArray transitions = new JArray();

            foreach (ChildAnimatorState childState in stateMachine.states)
            {
                AnimatorState state = childState.state;
                foreach (AnimatorStateTransition transition in state.transitions)
                {
                    transitions.Add(SerializeTransition(transition, state.name));
                }
            }

            foreach (AnimatorStateTransition transition in stateMachine.anyStateTransitions)
            {
                transitions.Add(SerializeTransition(transition, "Any"));
            }

            return transitions;
        }

        private static JObject SerializeTransition(AnimatorStateTransition transition, string fromState)
        {
            JArray conditions = new JArray();
            foreach (AnimatorCondition condition in transition.conditions)
            {
                conditions.Add(new JObject
                {
                    ["parameter"] = condition.parameter,
                    ["mode"] = condition.mode.ToString().ToLowerInvariant(),
                    ["threshold"] = condition.threshold
                });
            }

            return new JObject
            {
                ["fromState"] = fromState,
                ["toState"] = transition.destinationState != null ? transition.destinationState.name : "(exit)",
                ["hasExitTime"] = transition.hasExitTime,
                ["exitTime"] = transition.exitTime,
                ["duration"] = transition.duration,
                ["conditions"] = conditions
            };
        }
    }
}
