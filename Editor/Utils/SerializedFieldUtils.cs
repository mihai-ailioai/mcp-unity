using System;
using System.Reflection;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    public static class SerializedFieldUtils
    {
        public static Type FindType(string typeName, Type baseConstraint)
        {
            Type type = Type.GetType(typeName);
            if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
            {
                return type;
            }

            // Try Assembly-CSharp (user scripts)
            type = Type.GetType($"{typeName}, Assembly-CSharp");
            if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
            {
                return type;
            }

            string[] commonNamespaces = new string[]
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.EventSystems",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "TMPro"
            };

            foreach (string ns in commonNamespaces)
            {
                type = Type.GetType($"{ns}.{typeName}, UnityEngine");
                if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
                {
                    return type;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if ((t.Name == typeName || t.FullName == typeName) && (baseConstraint == null || baseConstraint.IsAssignableFrom(t)))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            return null;
        }

        /// <summary>
        /// Find a GameObject by its hierarchy path
        /// </summary>
        /// <param name="path">The path to the GameObject (e.g. "Canvas/Panel/Button")</param>
        /// <returns>The GameObject if found, null otherwise</returns>
        public static GameObject FindGameObjectByPath(string path)
        {
            string[] pathParts = path.Split('/');
            GameObject[] rootGameObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();

            if (pathParts.Length == 0)
            {
                return null;
            }

            foreach (GameObject rootObj in rootGameObjects)
            {
                if (rootObj.name == pathParts[0])
                {
                    GameObject current = rootObj;

                    for (int i = 1; i < pathParts.Length; i++)
                    {
                        Transform child = current.transform.Find(pathParts[i]);
                        if (child == null)
                        {
                            return null;
                        }

                        current = child.gameObject;
                    }

                    return current;
                }
            }

            return null;
        }

        /// <summary>
        /// Update fields/properties on a Unity object based on the provided JObject
        /// </summary>
        /// <param name="target">The Unity object to update</param>
        /// <param name="fieldData">The data to apply</param>
        /// <param name="errorMessage">Any error message when update fails</param>
        /// <returns>True if update succeeds for all provided fields/properties</returns>
        public static bool UpdateFieldsFromJson(UnityEngine.Object target, JObject fieldData, out string errorMessage)
        {
            errorMessage = "";

            if (target == null || fieldData == null)
            {
                errorMessage = "Target object or field data is null";
                return false;
            }

            Type targetType = target.GetType();
            bool fullSuccess = true;

            Undo.RecordObject(target, $"Update {target.GetType().Name} fields");

            foreach (var property in fieldData.Properties())
            {
                string fieldName = property.Name;
                JToken fieldValue = property.Value;

                if (string.IsNullOrEmpty(fieldName))
                {
                    continue;
                }

                FieldInfo fieldInfo = targetType.GetField(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (fieldInfo != null)
                {
                    try
                    {
                        // Allow setting null for reference types (e.g., clearing UnityEngine.Object references)
                        if (fieldValue.Type == JTokenType.Null)
                        {
                            if (!fieldInfo.FieldType.IsValueType || Nullable.GetUnderlyingType(fieldInfo.FieldType) != null)
                            {
                                fieldInfo.SetValue(target, null);
                            }
                            // Skip silently for value types — null is not meaningful
                        }
                        else
                        {
                            object value = ConvertJTokenToValue(fieldValue, fieldInfo.FieldType);
                            fieldInfo.SetValue(target, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        fullSuccess = false;
                        errorMessage = $"Error setting field '{fieldName}' on {targetType.Name}: {ex.Message}";
                        McpLogger.LogError($"[MCP Unity] {errorMessage}");
                    }
                    continue;
                }

                PropertyInfo propertyInfo = targetType.GetProperty(fieldName,
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (propertyInfo != null)
                {
                    if (!propertyInfo.CanWrite)
                    {
                        fullSuccess = false;
                        errorMessage = $"Property '{fieldName}' on '{targetType.Name}' is read-only";
                        McpLogger.LogError($"[MCP Unity] {errorMessage}");
                        continue;
                    }
                    try
                    {
                        // Allow setting null for reference types (e.g., clearing UnityEngine.Object references)
                        if (fieldValue.Type == JTokenType.Null)
                        {
                            if (!propertyInfo.PropertyType.IsValueType || Nullable.GetUnderlyingType(propertyInfo.PropertyType) != null)
                            {
                                propertyInfo.SetValue(target, null);
                            }
                        }
                        else
                        {
                            object value = ConvertJTokenToValue(fieldValue, propertyInfo.PropertyType);
                            propertyInfo.SetValue(target, value);
                        }
                    }
                    catch (Exception ex)
                    {
                        fullSuccess = false;
                        errorMessage = $"Error setting property '{fieldName}' on {targetType.Name}: {ex.Message}";
                        McpLogger.LogError($"[MCP Unity] {errorMessage}");
                    }
                    continue;
                }

                fullSuccess = false;
                errorMessage = $"Field or property '{fieldName}' not found on '{targetType.Name}'";
                McpLogger.LogError($"[MCP Unity] {errorMessage}");
            }

            return fullSuccess;
        }

        /// <summary>
        /// Read all user-defined serialized fields from a Unity object into a JObject.
        /// Only includes fields declared on the concrete type (not inherited from base Unity classes).
        /// Follows Unity serialization rules: public fields (unless [NonSerialized]) and [SerializeField] private fields.
        /// UnityEngine.Object references are serialized as $ref descriptors for round-trippability.
        /// </summary>
        /// <param name="target">The Unity object to read fields from</param>
        /// <returns>A JObject containing all readable user-defined fields</returns>
        public static JObject ReadFieldsToJson(UnityEngine.Object target)
        {
            if (target == null)
            {
                return new JObject();
            }

            JObject result = new JObject();
            Type targetType = target.GetType();

            // Get fields declared on the concrete type and its user-defined base classes
            // Stop at ScriptableObject/MonoBehaviour/Component/Object boundaries
            Type[] stopTypes = new Type[]
            {
                typeof(ScriptableObject),
                typeof(MonoBehaviour),
                typeof(Component),
                typeof(UnityEngine.Object)
            };

            Type currentType = targetType;
            while (currentType != null && Array.IndexOf(stopTypes, currentType) < 0)
            {
                FieldInfo[] fields = currentType.GetFields(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly);

                foreach (FieldInfo field in fields)
                {
                    // Skip backing fields for auto-properties
                    if (field.Name.StartsWith("<"))
                    {
                        continue;
                    }

                    // Match Unity serialization rules:
                    // Include: public fields (unless [NonSerialized]), private fields with [SerializeField]
                    // Exclude: [NonSerialized] fields, [HideInInspector] fields
                    bool isPublic = field.IsPublic;
                    bool hasSerializeField = field.GetCustomAttribute<SerializeField>() != null;
                    bool hasNonSerialized = field.GetCustomAttribute<NonSerializedAttribute>() != null;

                    if (hasNonSerialized)
                    {
                        continue;
                    }

                    if (!isPublic && !hasSerializeField)
                    {
                        continue;
                    }

                    // Skip if already added by a derived class
                    if (result.ContainsKey(field.Name))
                    {
                        continue;
                    }

                    try
                    {
                        object value = field.GetValue(target);
                        result[field.Name] = ConvertValueToJToken(value, field.FieldType);
                    }
                    catch (Exception ex)
                    {
                        McpLogger.LogError($"[MCP Unity] Error reading field '{field.Name}' on {targetType.Name}: {ex.Message}");
                        result[field.Name] = null;
                    }
                }

                currentType = currentType.BaseType;
            }

            return result;
        }

        /// <summary>
        /// Convert a C# value to a JToken for JSON serialization.
        /// UnityEngine.Object references are serialized as $ref descriptors.
        /// </summary>
        private static JToken ConvertValueToJToken(object value, Type fieldType)
        {
            if (value == null)
            {
                return JValue.CreateNull();
            }

            // Handle UnityEngine.Object references as $ref descriptors
            if (value is UnityEngine.Object unityObj)
            {
                // Check if it's a null Unity object (destroyed or missing reference)
                if (unityObj == null)
                {
                    return JValue.CreateNull();
                }

                string assetPath = AssetDatabase.GetAssetPath(unityObj);

                // Asset reference (has a valid asset path)
                if (!string.IsNullOrEmpty(assetPath))
                {
                    string guid = AssetDatabase.AssetPathToGUID(assetPath);
                    var refObj = new JObject
                    {
                        ["$ref"] = "asset",
                        ["assetPath"] = assetPath,
                        ["guid"] = guid
                    };
                    // Include type info for clarity
                    refObj["typeName"] = unityObj.GetType().Name;
                    return refObj;
                }

                // Scene reference (no asset path — it's a scene object)
                var sceneRef = new JObject
                {
                    ["$ref"] = "scene",
                    ["instanceId"] = unityObj.GetInstanceID()
                };

                if (unityObj is Component comp)
                {
                    sceneRef["objectPath"] = GetGameObjectPath(comp.gameObject);
                    sceneRef["componentType"] = comp.GetType().Name;
                }
                else if (unityObj is GameObject go)
                {
                    sceneRef["objectPath"] = GetGameObjectPath(go);
                }

                sceneRef["typeName"] = unityObj.GetType().Name;
                return sceneRef;
            }

            // Handle Unity value types
            if (value is Vector2 v2)
            {
                return new JObject { ["x"] = v2.x, ["y"] = v2.y };
            }
            if (value is Vector3 v3)
            {
                return new JObject { ["x"] = v3.x, ["y"] = v3.y, ["z"] = v3.z };
            }
            if (value is Vector4 v4)
            {
                return new JObject { ["x"] = v4.x, ["y"] = v4.y, ["z"] = v4.z, ["w"] = v4.w };
            }
            if (value is Quaternion q)
            {
                return new JObject { ["x"] = q.x, ["y"] = q.y, ["z"] = q.z, ["w"] = q.w };
            }
            if (value is Color c)
            {
                return new JObject { ["r"] = c.r, ["g"] = c.g, ["b"] = c.b, ["a"] = c.a };
            }
            if (value is Bounds b)
            {
                return new JObject
                {
                    ["center"] = new JObject { ["x"] = b.center.x, ["y"] = b.center.y, ["z"] = b.center.z },
                    ["size"] = new JObject { ["x"] = b.size.x, ["y"] = b.size.y, ["z"] = b.size.z }
                };
            }
            if (value is Rect r)
            {
                return new JObject { ["x"] = r.x, ["y"] = r.y, ["width"] = r.width, ["height"] = r.height };
            }

            // Enums — serialize as string
            if (fieldType.IsEnum)
            {
                return value.ToString();
            }

            // Handle arrays and lists — recurse into elements for proper $ref handling
            if (value is System.Collections.IList list)
            {
                Type elementType = fieldType.IsArray
                    ? fieldType.GetElementType()
                    : (fieldType.IsGenericType ? fieldType.GetGenericArguments()[0] : typeof(object));

                var arr = new JArray();
                foreach (object item in list)
                {
                    arr.Add(ConvertValueToJToken(item, elementType));
                }
                return arr;
            }

            // Primitives and strings — let JToken handle it
            try
            {
                return JToken.FromObject(value);
            }
            catch (Exception)
            {
                // For complex types that can't be serialized, return type info
                return $"[{fieldType.Name}]";
            }
        }

        /// <summary>
        /// Get the full hierarchy path of a GameObject
        /// </summary>
        private static string GetGameObjectPath(GameObject go)
        {
            string path = go.name;
            Transform parent = go.transform.parent;
            while (parent != null)
            {
                path = parent.name + "/" + path;
                parent = parent.parent;
            }
            return path;
        }

        /// <summary>
        /// Convert a JToken to a value of the specified type
        /// </summary>
        /// <param name="token">The JToken to convert</param>
        /// <param name="targetType">The target type to convert to</param>
        /// <returns>The converted value</returns>
        public static object ConvertJTokenToValue(JToken token, Type targetType)
        {
            if (token == null)
            {
                return null;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType)
                && token.Type == JTokenType.Object
                && token["$ref"] != null)
            {
                return ResolveObjectReference((JObject)token, targetType);
            }

            if (targetType == typeof(Vector2) && token.Type == JTokenType.Object)
            {
                JObject vector = (JObject)token;
                return new Vector2(
                    vector["x"]?.ToObject<float>() ?? 0f,
                    vector["y"]?.ToObject<float>() ?? 0f
                );
            }

            if (targetType == typeof(Vector3) && token.Type == JTokenType.Object)
            {
                JObject vector = (JObject)token;
                return new Vector3(
                    vector["x"]?.ToObject<float>() ?? 0f,
                    vector["y"]?.ToObject<float>() ?? 0f,
                    vector["z"]?.ToObject<float>() ?? 0f
                );
            }

            if (targetType == typeof(Vector4) && token.Type == JTokenType.Object)
            {
                JObject vector = (JObject)token;
                return new Vector4(
                    vector["x"]?.ToObject<float>() ?? 0f,
                    vector["y"]?.ToObject<float>() ?? 0f,
                    vector["z"]?.ToObject<float>() ?? 0f,
                    vector["w"]?.ToObject<float>() ?? 0f
                );
            }

            if (targetType == typeof(Quaternion) && token.Type == JTokenType.Object)
            {
                JObject quaternion = (JObject)token;
                return new Quaternion(
                    quaternion["x"]?.ToObject<float>() ?? 0f,
                    quaternion["y"]?.ToObject<float>() ?? 0f,
                    quaternion["z"]?.ToObject<float>() ?? 0f,
                    quaternion["w"]?.ToObject<float>() ?? 1f
                );
            }

            if (targetType == typeof(Color) && token.Type == JTokenType.Object)
            {
                JObject color = (JObject)token;
                return new Color(
                    color["r"]?.ToObject<float>() ?? 0f,
                    color["g"]?.ToObject<float>() ?? 0f,
                    color["b"]?.ToObject<float>() ?? 0f,
                    color["a"]?.ToObject<float>() ?? 1f
                );
            }

            if (targetType == typeof(Bounds) && token.Type == JTokenType.Object)
            {
                JObject bounds = (JObject)token;
                Vector3 center = bounds["center"]?.ToObject<Vector3>() ?? Vector3.zero;
                Vector3 size = bounds["size"]?.ToObject<Vector3>() ?? Vector3.one;
                return new Bounds(center, size);
            }

            if (targetType == typeof(Rect) && token.Type == JTokenType.Object)
            {
                JObject rect = (JObject)token;
                return new Rect(
                    rect["x"]?.ToObject<float>() ?? 0f,
                    rect["y"]?.ToObject<float>() ?? 0f,
                    rect["width"]?.ToObject<float>() ?? 0f,
                    rect["height"]?.ToObject<float>() ?? 0f
                );
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(targetType))
            {
                throw new Exception(
                    $"Cannot set field of type '{targetType.Name}' from a plain JSON value. " +
                    "Use a $ref descriptor: {\"$ref\": \"scene\", \"objectPath\": \"Path/To/Object\"}, " +
                    "{\"$ref\": \"scene\", \"instanceId\": 12345}, or " +
                    "{\"$ref\": \"asset\", \"assetPath\": \"Assets/Path/To/Asset.ext\"}");
            }

            if (targetType.IsEnum)
            {
                if (token.Type == JTokenType.String)
                {
                    string enumName = token.ToObject<string>();
                    if (Enum.TryParse(targetType, enumName, true, out object result))
                    {
                        return result;
                    }

                    if (int.TryParse(enumName, out int enumValue))
                    {
                        return Enum.ToObject(targetType, enumValue);
                    }
                }
                else if (token.Type == JTokenType.Integer)
                {
                    return Enum.ToObject(targetType, token.ToObject<int>());
                }
            }

            try
            {
                return token.ToObject(targetType);
            }
            catch (Exception ex)
            {
                McpLogger.LogError($"[MCP Unity] Error converting value to type {targetType.Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Resolves a $ref descriptor to a live Unity object reference.
        /// Supports scene object references (GameObjects and Components) and asset references (by path or GUID).
        /// </summary>
        /// <param name="refDescriptor">The JSON object containing $ref and lookup parameters</param>
        /// <param name="targetType">The declared field type to resolve to</param>
        /// <returns>The resolved UnityEngine.Object, or null if resolution fails</returns>
        /// <exception cref="Exception">Thrown with descriptive message when resolution fails</exception>
        public static UnityEngine.Object ResolveObjectReference(JObject refDescriptor, Type targetType)
        {
            string refType = refDescriptor["$ref"]?.ToObject<string>();

            if (refType == "asset")
            {
                return ResolveAssetReference(refDescriptor, targetType);
            }

            if (refType != "scene")
            {
                throw new Exception($"Unsupported $ref type '{refType}'. Supported types: 'scene', 'asset'");
            }

            int? instanceId = refDescriptor["instanceId"]?.ToObject<int?>();
            string objectPath = refDescriptor["objectPath"]?.ToObject<string>();

            if (!instanceId.HasValue && string.IsNullOrEmpty(objectPath))
            {
                throw new Exception("Scene reference must provide either 'instanceId' or 'objectPath'");
            }

            GameObject referencedObject = null;
            string refIdentifier;

            if (instanceId.HasValue)
            {
                var obj = EditorUtility.InstanceIDToObject(instanceId.Value);
                referencedObject = obj as GameObject;
                if (referencedObject == null && obj is Component comp)
                {
                    referencedObject = comp.gameObject;
                }
                refIdentifier = $"instanceId {instanceId.Value}";
            }
            else
            {
                referencedObject = PrefabStageUtils.FindGameObject(objectPath);
                if (referencedObject == null && !PrefabStageUtils.IsInPrefabStage())
                {
                    // Fallback to scene hierarchy traversal (skip in Prefab Mode)
                    referencedObject = FindGameObjectByPath(objectPath);
                }
                refIdentifier = $"objectPath '{objectPath}'";
            }

            if (referencedObject == null)
            {
                throw new Exception($"Referenced GameObject not found: {refIdentifier}");
            }

            if (targetType == typeof(GameObject))
            {
                return referencedObject;
            }

            if (typeof(Component).IsAssignableFrom(targetType))
            {
                string componentTypeName = refDescriptor["componentType"]?.ToObject<string>();
                Component resolved;

                if (!string.IsNullOrEmpty(componentTypeName))
                {
                    Type requestedType = FindType(componentTypeName, typeof(Component));
                    if (requestedType == null)
                    {
                        throw new Exception($"Component type '{componentTypeName}' not found");
                    }

                    resolved = referencedObject.GetComponent(requestedType);
                    if (resolved == null)
                    {
                        throw new Exception(
                            $"Component '{componentTypeName}' not found on GameObject '{referencedObject.name}'");
                    }

                    if (!targetType.IsAssignableFrom(requestedType))
                    {
                        throw new Exception(
                            $"Component '{componentTypeName}' is not assignable to field type '{targetType.Name}'");
                    }
                }
                else
                {
                    resolved = referencedObject.GetComponent(targetType);
                    if (resolved == null)
                    {
                        throw new Exception(
                            $"Component of type '{targetType.Name}' not found on GameObject '{referencedObject.name}'");
                    }
                }

                return resolved;
            }

            if (targetType != typeof(UnityEngine.Object))
            {
                throw new Exception(
                    $"Cannot resolve scene reference to field type '{targetType.Name}'. " +
                    "Only GameObject, Component subclasses, and UnityEngine.Object fields are supported.");
            }

            string baseComponentType = refDescriptor["componentType"]?.ToObject<string>();
            if (!string.IsNullOrEmpty(baseComponentType))
            {
                Type requestedType = FindType(baseComponentType, typeof(Component));
                if (requestedType == null)
                {
                    throw new Exception($"Component type '{baseComponentType}' not found");
                }

                Component resolved = referencedObject.GetComponent(requestedType);
                if (resolved == null)
                {
                    throw new Exception(
                        $"Component '{baseComponentType}' not found on GameObject '{referencedObject.name}'");
                }

                return resolved;
            }

            return referencedObject;
        }

        private static UnityEngine.Object ResolveAssetReference(JObject refDescriptor, Type targetType)
        {
            string assetPath = refDescriptor["assetPath"]?.ToObject<string>();
            string guid = refDescriptor["guid"]?.ToObject<string>();

            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
            {
                throw new Exception("Asset reference must provide either 'assetPath' or 'guid'");
            }

            string guidResolvedPath = null;
            if (!string.IsNullOrEmpty(guid))
            {
                guidResolvedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(guidResolvedPath))
                {
                    throw new Exception($"No asset found for GUID '{guid}'");
                }
            }

            if (!string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(guidResolvedPath))
            {
                if (!string.Equals(assetPath, guidResolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new Exception(
                        $"Asset path mismatch: assetPath='{assetPath}' but GUID '{guid}' resolves to '{guidResolvedPath}'");
                }
            }

            string resolvedPath = !string.IsNullOrEmpty(assetPath) ? assetPath : guidResolvedPath;

            UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(resolvedPath, typeof(UnityEngine.Object));
            if (asset == null)
            {
                throw new Exception($"No asset found at path '{resolvedPath}'");
            }

            if (typeof(Component).IsAssignableFrom(targetType) && asset is GameObject prefabGo)
            {
                string componentTypeName = refDescriptor["componentType"]?.ToObject<string>();
                Component resolved;

                if (!string.IsNullOrEmpty(componentTypeName))
                {
                    Type requestedType = FindType(componentTypeName, typeof(Component));
                    if (requestedType == null)
                    {
                        throw new Exception($"Component type '{componentTypeName}' not found");
                    }

                    resolved = prefabGo.GetComponent(requestedType);
                    if (resolved == null)
                    {
                        throw new Exception($"Component '{componentTypeName}' not found on prefab '{prefabGo.name}'");
                    }

                    if (!targetType.IsAssignableFrom(requestedType))
                    {
                        throw new Exception($"Component '{componentTypeName}' is not assignable to field type '{targetType.Name}'");
                    }
                }
                else
                {
                    resolved = prefabGo.GetComponent(targetType);
                    if (resolved == null)
                    {
                        throw new Exception($"Component of type '{targetType.Name}' not found on prefab '{prefabGo.name}'");
                    }
                }
                return resolved;
            }

            if (targetType == typeof(GameObject) && asset is GameObject go)
            {
                return go;
            }

            // Handle UnityEngine.Object target type with componentType override for prefab assets
            if (targetType == typeof(UnityEngine.Object) && asset is GameObject goForComponent)
            {
                string componentTypeName = refDescriptor["componentType"]?.ToObject<string>();
                if (!string.IsNullOrEmpty(componentTypeName))
                {
                    Type requestedType = FindType(componentTypeName, typeof(Component));
                    if (requestedType == null)
                    {
                        throw new Exception($"Component type '{componentTypeName}' not found");
                    }

                    Component resolved = goForComponent.GetComponent(requestedType);
                    if (resolved == null)
                    {
                        throw new Exception($"Component '{componentTypeName}' not found on prefab '{goForComponent.name}'");
                    }

                    return resolved;
                }
            }

            if (!targetType.IsAssignableFrom(asset.GetType()))
            {
                throw new Exception(
                    $"Asset at '{resolvedPath}' is of type '{asset.GetType().Name}', not assignable to field type '{targetType.Name}'");
            }

            return asset;
        }
    }
}
