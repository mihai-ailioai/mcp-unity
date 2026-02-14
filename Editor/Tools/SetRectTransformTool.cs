using System;
using System.Collections.Generic;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEngine;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for setting RectTransform layout properties on UI GameObjects.
    /// Supports named presets and raw property overrides.
    /// </summary>
    public class SetRectTransformTool : McpToolBase
    {
        private struct RectTransformPreset
        {
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;

            public RectTransformPreset(Vector2 anchorMin, Vector2 anchorMax, Vector2 pivot, Vector2 anchoredPosition, Vector2 sizeDelta)
            {
                AnchorMin = anchorMin;
                AnchorMax = anchorMax;
                Pivot = pivot;
                AnchoredPosition = anchoredPosition;
                SizeDelta = sizeDelta;
            }
        }

        private static readonly Dictionary<string, RectTransformPreset> Presets = new Dictionary<string, RectTransformPreset>
        {
            ["stretch"] = new RectTransformPreset(new Vector2(0f, 0f), new Vector2(1f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(0f, 0f)),
            ["center"] = new RectTransformPreset(new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["top-left"] = new RectTransformPreset(new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["top-center"] = new RectTransformPreset(new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["top-right"] = new RectTransformPreset(new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(1f, 1f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["middle-left"] = new RectTransformPreset(new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0.5f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["middle-right"] = new RectTransformPreset(new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["bottom-left"] = new RectTransformPreset(new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["bottom-center"] = new RectTransformPreset(new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["bottom-right"] = new RectTransformPreset(new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(1f, 0f), new Vector2(0f, 0f), new Vector2(100f, 100f)),
            ["stretch-horizontal"] = new RectTransformPreset(new Vector2(0f, 0.5f), new Vector2(1f, 0.5f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(0f, 100f)),
            ["stretch-vertical"] = new RectTransformPreset(new Vector2(0.5f, 0f), new Vector2(0.5f, 1f), new Vector2(0.5f, 0.5f), new Vector2(0f, 0f), new Vector2(100f, 0f))
        };

        public SetRectTransformTool()
        {
            Name = "set_rect_transform";
            Description = "Sets RectTransform layout properties on a UI GameObject using presets and/or raw property overrides.";
            IsAsync = false;
        }

        public override JObject Execute(JObject parameters)
        {
            var findResult = TransformToolUtils.FindGameObject(parameters);
            if (findResult.Error != null)
            {
                return findResult.Error;
            }

            GameObject gameObject = findResult.GameObject;
            RectTransform rectTransform = gameObject.GetComponent<RectTransform>();
            if (rectTransform == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"GameObject '{gameObject.name}' does not have a RectTransform component",
                    "validation_error"
                );
            }

            string presetName = parameters["preset"]?.ToObject<string>();
            JObject anchoredPositionObj = parameters["anchoredPosition"] as JObject;
            JObject sizeDeltaObj = parameters["sizeDelta"] as JObject;
            JObject anchorMinObj = parameters["anchorMin"] as JObject;
            JObject anchorMaxObj = parameters["anchorMax"] as JObject;
            JObject pivotObj = parameters["pivot"] as JObject;
            JObject offsetMinObj = parameters["offsetMin"] as JObject;
            JObject offsetMaxObj = parameters["offsetMax"] as JObject;

            bool hasPreset = !string.IsNullOrWhiteSpace(presetName);
            bool hasRawOverrides = anchoredPositionObj != null
                                   || sizeDeltaObj != null
                                   || anchorMinObj != null
                                   || anchorMaxObj != null
                                   || pivotObj != null
                                   || offsetMinObj != null
                                   || offsetMaxObj != null;

            if (!hasPreset && !hasRawOverrides)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "At least one of 'preset', 'anchoredPosition', 'sizeDelta', 'anchorMin', 'anchorMax', 'pivot', 'offsetMin', or 'offsetMax' must be provided",
                    "validation_error"
                );
            }

            // Validate preset name before recording undo (avoid undo noise on validation failures)
            RectTransformPreset? validatedPreset = null;
            if (hasPreset)
            {
                string normalizedPreset = NormalizePresetName(presetName);
                if (!Presets.TryGetValue(normalizedPreset, out RectTransformPreset preset))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Invalid preset '{presetName}'. Valid presets: {string.Join(", ", Presets.Keys)}",
                        "validation_error"
                    );
                }
                validatedPreset = preset;
            }

            Undo.RecordObject(rectTransform, "Set RectTransform");

            if (validatedPreset.HasValue)
            {
                var preset = validatedPreset.Value;
                rectTransform.anchorMin = preset.AnchorMin;
                rectTransform.anchorMax = preset.AnchorMax;
                rectTransform.pivot = preset.Pivot;
                rectTransform.anchoredPosition = preset.AnchoredPosition;
                rectTransform.sizeDelta = preset.SizeDelta;
            }

            if (anchoredPositionObj != null)
            {
                rectTransform.anchoredPosition = ApplyVector2Override(rectTransform.anchoredPosition, anchoredPositionObj);
            }

            if (sizeDeltaObj != null)
            {
                rectTransform.sizeDelta = ApplyVector2Override(rectTransform.sizeDelta, sizeDeltaObj);
            }

            if (anchorMinObj != null)
            {
                rectTransform.anchorMin = ApplyVector2Override(rectTransform.anchorMin, anchorMinObj);
            }

            if (anchorMaxObj != null)
            {
                rectTransform.anchorMax = ApplyVector2Override(rectTransform.anchorMax, anchorMaxObj);
            }

            if (pivotObj != null)
            {
                rectTransform.pivot = ApplyVector2Override(rectTransform.pivot, pivotObj);
            }

            if (offsetMinObj != null)
            {
                rectTransform.offsetMin = ApplyVector2Override(rectTransform.offsetMin, offsetMinObj);
            }

            if (offsetMaxObj != null)
            {
                rectTransform.offsetMax = ApplyVector2Override(rectTransform.offsetMax, offsetMaxObj);
            }

            EditorUtility.SetDirty(gameObject);

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"RectTransform updated successfully for GameObject '{gameObject.name}'.",
                ["data"] = new JObject
                {
                    ["instanceId"] = gameObject.GetInstanceID(),
                    ["name"] = gameObject.name,
                    ["path"] = TransformToolUtils.GetGameObjectPath(gameObject),
                    ["anchorMin"] = Vector2ToJObject(rectTransform.anchorMin),
                    ["anchorMax"] = Vector2ToJObject(rectTransform.anchorMax),
                    ["pivot"] = Vector2ToJObject(rectTransform.pivot),
                    ["anchoredPosition"] = Vector2ToJObject(rectTransform.anchoredPosition),
                    ["sizeDelta"] = Vector2ToJObject(rectTransform.sizeDelta),
                    ["offsetMin"] = Vector2ToJObject(rectTransform.offsetMin),
                    ["offsetMax"] = Vector2ToJObject(rectTransform.offsetMax)
                }
            };
        }

        private static string NormalizePresetName(string presetName)
        {
            string[] parts = presetName.Trim().ToLowerInvariant().Split(new[] { '-', '_', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join("-", parts);
        }

        private static Vector2 ApplyVector2Override(Vector2 current, JObject value)
        {
            return new Vector2(
                value["x"]?.ToObject<float>() ?? current.x,
                value["y"]?.ToObject<float>() ?? current.y
            );
        }

        private static JObject Vector2ToJObject(Vector2 vector)
        {
            return new JObject
            {
                ["x"] = vector.x,
                ["y"] = vector.y
            };
        }
    }
}
