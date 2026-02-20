using System;
using System.Collections.Generic;
using System.IO;
using McpUnity.Unity;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Tools
{
    public class SetupAnimationClipTool : McpToolBase
    {
        public SetupAnimationClipTool()
        {
            Name = "setup_animation_clip";
            Description = "Creates or updates AnimationClip assets, including curves, events, loop settings, and frame rate";
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
                assetPath = NormalizeAssetPath(assetPath);

                AnimationClip clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(assetPath);
                bool isNew = clip == null;
                if (isNew)
                {
                    clip = new AnimationClip
                    {
                        name = Path.GetFileNameWithoutExtension(assetPath)
                    };
                }

                Undo.RecordObject(clip, $"Setup Animation Clip {clip.name}");

                int removedCurveCount = RemoveCurves(clip, parameters["removeCurves"] as JArray);
                int removedEventCount = RemoveEvents(clip, parameters["removeEvents"] as JArray);

                float? frameRate = parameters["frameRate"]?.ToObject<float?>();
                if (frameRate.HasValue)
                {
                    clip.frameRate = frameRate.Value;
                }

                bool? loop = parameters["loop"]?.ToObject<bool?>();
                if (loop.HasValue)
                {
                    AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
                    settings.loopTime = loop.Value;
                    AnimationUtility.SetAnimationClipSettings(clip, settings);
                }

                int upsertedCurveCount = UpsertCurves(clip, parameters["curves"] as JArray);
                int addedEventCount = AddEvents(clip, parameters["events"] as JArray);

                if (isNew)
                {
                    string directory = Path.GetDirectoryName(assetPath)?.Replace("\\", "/");
                    if (!string.IsNullOrEmpty(directory) && !AssetDatabase.IsValidFolder(directory))
                    {
                        MoveAssetTool.CreateFolderRecursive(directory);
                    }

                    AssetDatabase.CreateAsset(clip, assetPath);
                }
                else
                {
                    EditorUtility.SetDirty(clip);
                }

                AssetDatabase.SaveAssets();

                string assetGuid = AssetDatabase.AssetPathToGUID(assetPath);

                return new JObject
                {
                    ["success"] = true,
                    ["type"] = "text",
                    ["message"] = isNew
                        ? $"Successfully created animation clip at '{assetPath}'"
                        : $"Successfully updated animation clip at '{assetPath}'",
                    ["assetPath"] = assetPath,
                    ["assetGuid"] = assetGuid,
                    ["isNew"] = isNew,
                    ["curvesProcessed"] = removedCurveCount + upsertedCurveCount,
                    ["eventsProcessed"] = removedEventCount + addedEventCount
                };
            }
            catch (Exception ex)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to setup animation clip: {ex.Message}",
                    "execution_error"
                );
            }
        }

        private static string NormalizeAssetPath(string assetPath)
        {
            string normalized = assetPath.Trim().Replace("\\", "/");

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

        private int RemoveCurves(AnimationClip clip, JArray removeCurves)
        {
            if (removeCurves == null || removeCurves.Count == 0)
            {
                return 0;
            }

            int removedCount = 0;
            EditorCurveBinding[] existingBindings = AnimationUtility.GetCurveBindings(clip);

            foreach (JToken token in removeCurves)
            {
                JObject removeCurve = token as JObject;
                if (removeCurve == null)
                {
                    continue;
                }

                string propertyPath = removeCurve["propertyPath"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(propertyPath))
                {
                    throw new ArgumentException("Each removeCurves entry must include 'propertyPath'");
                }

                string relativePath = removeCurve["relativePath"]?.ToObject<string>() ?? string.Empty;
                string typeName = removeCurve["type"]?.ToObject<string>();
                Type requestedType = ResolveComponentType(typeName, true);

                foreach (EditorCurveBinding binding in existingBindings)
                {
                    if (!string.Equals(binding.propertyName, propertyPath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!string.Equals(binding.path, relativePath, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (requestedType != null && binding.type != requestedType)
                    {
                        continue;
                    }

                    AnimationUtility.SetEditorCurve(clip, binding, null);
                    removedCount++;
                }
            }

            return removedCount;
        }

        private int UpsertCurves(AnimationClip clip, JArray curves)
        {
            if (curves == null || curves.Count == 0)
            {
                return 0;
            }

            int curveCount = 0;
            foreach (JToken token in curves)
            {
                JObject curveObject = token as JObject;
                if (curveObject == null)
                {
                    continue;
                }

                string propertyPath = curveObject["propertyPath"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(propertyPath))
                {
                    throw new ArgumentException("Each curves entry must include 'propertyPath'");
                }

                string typeName = curveObject["type"]?.ToObject<string>() ?? "Transform";
                Type curveType = ResolveComponentType(typeName, false);

                string relativePath = curveObject["relativePath"]?.ToObject<string>() ?? string.Empty;
                JArray keys = curveObject["keys"] as JArray;
                if (keys == null || keys.Count == 0)
                {
                    throw new ArgumentException($"Curve '{propertyPath}' must include a non-empty 'keys' array");
                }

                List<Keyframe> keyframes = new List<Keyframe>();
                foreach (JToken keyToken in keys)
                {
                    JObject keyObject = keyToken as JObject;
                    if (keyObject == null)
                    {
                        continue;
                    }

                    if (keyObject["time"] == null || keyObject["value"] == null)
                    {
                        throw new ArgumentException($"Each key in curve '{propertyPath}' must include 'time' and 'value'");
                    }

                    float time = keyObject["time"].ToObject<float>();
                    float value = keyObject["value"].ToObject<float>();

                    if (keyObject["inTangent"] != null || keyObject["outTangent"] != null)
                    {
                        float inTangent = keyObject["inTangent"]?.ToObject<float>() ?? 0f;
                        float outTangent = keyObject["outTangent"]?.ToObject<float>() ?? 0f;
                        keyframes.Add(new Keyframe(time, value, inTangent, outTangent));
                    }
                    else
                    {
                        keyframes.Add(new Keyframe(time, value));
                    }
                }

                EditorCurveBinding binding = EditorCurveBinding.FloatCurve(relativePath, curveType, propertyPath);
                AnimationUtility.SetEditorCurve(clip, binding, new AnimationCurve(keyframes.ToArray()));
                curveCount++;
            }

            return curveCount;
        }

        private int RemoveEvents(AnimationClip clip, JArray removeEvents)
        {
            if (removeEvents == null || removeEvents.Count == 0)
            {
                return 0;
            }

            List<AnimationEvent> events = new List<AnimationEvent>(AnimationUtility.GetAnimationEvents(clip));
            int removedCount = 0;

            foreach (JToken token in removeEvents)
            {
                JObject removeEvent = token as JObject;
                if (removeEvent == null)
                {
                    continue;
                }

                string functionName = removeEvent["functionName"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(functionName))
                {
                    throw new ArgumentException("Each removeEvents entry must include 'functionName'");
                }

                float? time = removeEvent["time"]?.ToObject<float?>();

                for (int i = events.Count - 1; i >= 0; i--)
                {
                    bool matchesFunction = string.Equals(events[i].functionName, functionName, StringComparison.Ordinal);
                    bool matchesTime = !time.HasValue || Mathf.Approximately(events[i].time, time.Value);

                    if (matchesFunction && matchesTime)
                    {
                        events.RemoveAt(i);
                        removedCount++;
                    }
                }
            }

            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            return removedCount;
        }

        private int AddEvents(AnimationClip clip, JArray eventsToAdd)
        {
            if (eventsToAdd == null || eventsToAdd.Count == 0)
            {
                return 0;
            }

            List<AnimationEvent> events = new List<AnimationEvent>(AnimationUtility.GetAnimationEvents(clip));
            int addedCount = 0;

            foreach (JToken token in eventsToAdd)
            {
                JObject eventObject = token as JObject;
                if (eventObject == null)
                {
                    continue;
                }

                string functionName = eventObject["functionName"]?.ToObject<string>();
                if (string.IsNullOrWhiteSpace(functionName))
                {
                    throw new ArgumentException("Each events entry must include 'functionName'");
                }

                AnimationEvent animationEvent = new AnimationEvent
                {
                    functionName = functionName,
                    time = eventObject["time"]?.ToObject<float>() ?? 0f
                };

                if (eventObject["stringParameter"] != null)
                {
                    animationEvent.stringParameter = eventObject["stringParameter"].ToObject<string>();
                }

                if (eventObject["floatParameter"] != null)
                {
                    animationEvent.floatParameter = eventObject["floatParameter"].ToObject<float>();
                }

                if (eventObject["intParameter"] != null)
                {
                    animationEvent.intParameter = eventObject["intParameter"].ToObject<int>();
                }

                events.Add(animationEvent);
                addedCount++;
            }

            events.Sort((left, right) => left.time.CompareTo(right.time));
            AnimationUtility.SetAnimationEvents(clip, events.ToArray());
            return addedCount;
        }

        private Type ResolveComponentType(string typeName, bool allowNull)
        {
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return allowNull ? null : typeof(Transform);
            }

            Type resolvedType = SerializedFieldUtils.FindType(typeName, typeof(Component));
            if (resolvedType == null)
            {
                resolvedType = Type.GetType($"UnityEngine.{typeName}, UnityEngine");
            }

            if (resolvedType == null)
            {
                throw new ArgumentException($"Could not resolve component type '{typeName}'");
            }

            return resolvedType;
        }
    }
}
