# ScriptableObject Tools Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add `create_scriptable_object` and `update_scriptable_object` tools with shared utility extraction and `$ref: "asset"` support.

**Architecture:** Extract duplicated reflection/type-resolution code from `UpdateComponentTool`, `RemoveComponentTool`, and `CreatePrefabTool` into `Editor/Utils/SerializedFieldUtils.cs`. Extend `$ref` protocol with `"asset"` type. Build two new tools on the shared utility.

**Tech Stack:** C# (Unity Editor), TypeScript (Node MCP server), Newtonsoft.Json, Unity AssetDatabase API.

---

### Task 1: Extract `SerializedFieldUtils` — Type Resolution

**Files:**
- Create: `Editor/Utils/SerializedFieldUtils.cs`
- Modify: `Editor/Tools/UpdateComponentTool.cs`
- Modify: `Editor/Tools/RemoveComponentTool.cs`
- Modify: `Editor/Tools/CreatePrefabTool.cs`

**Step 1: Create `SerializedFieldUtils.cs` with `FindType`**

Create `Editor/Utils/SerializedFieldUtils.cs`:

```csharp
using System;
using System.Reflection;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Shared utilities for reflection-based field setting, type resolution,
    /// and object reference resolution. Used by UpdateComponentTool,
    /// CreatePrefabTool, and ScriptableObject tools.
    /// </summary>
    public static class SerializedFieldUtils
    {
        private static readonly string[] CommonNamespaces = new string[]
        {
            "UnityEngine",
            "UnityEngine.UI",
            "UnityEngine.EventSystems",
            "UnityEngine.Animations",
            "UnityEngine.Rendering",
            "TMPro"
        };

        /// <summary>
        /// Find a type by name, optionally constrained to a base type.
        /// Searches direct match, common Unity namespaces, then all loaded assemblies.
        /// </summary>
        /// <param name="typeName">The simple or fully-qualified type name</param>
        /// <param name="baseConstraint">Optional base type constraint (e.g. typeof(Component), typeof(ScriptableObject)). Pass null for no constraint.</param>
        /// <returns>The resolved Type, or null if not found</returns>
        public static Type FindType(string typeName, Type baseConstraint)
        {
            // First try direct match
            Type type = Type.GetType(typeName);
            if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
            {
                return type;
            }

            // Try common Unity namespaces
            foreach (string ns in CommonNamespaces)
            {
                type = Type.GetType($"{ns}.{typeName}, UnityEngine");
                if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
                {
                    return type;
                }
            }

            // Try all loaded assemblies
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == typeName && (baseConstraint == null || baseConstraint.IsAssignableFrom(t)))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    // Some assemblies might throw exceptions when getting types
                    continue;
                }
            }

            return null;
        }
    }
}
```

**Step 2: Update `UpdateComponentTool.cs` to use `SerializedFieldUtils.FindType`**

In `Editor/Tools/UpdateComponentTool.cs`, replace the call at line 89:
```csharp
// Before:
Type componentType = FindComponentType(componentName);
// After:
Type componentType = SerializedFieldUtils.FindType(componentName, typeof(Component));
```

Also replace the two calls inside `ResolveObjectReference` (lines 544, 589):
```csharp
// Before:
Type requestedType = FindComponentType(componentTypeName);
// After:
Type requestedType = SerializedFieldUtils.FindType(componentTypeName, typeof(Component));
```

Then delete the private `FindComponentType` method (lines 190-240).

**Step 3: Update `RemoveComponentTool.cs` to use `SerializedFieldUtils.FindType`**

In `Editor/Tools/RemoveComponentTool.cs`, replace the call at line 69:
```csharp
// Before:
Type componentType = FindComponentType(componentName);
// After:
Type componentType = SerializedFieldUtils.FindType(componentName, typeof(Component));
```

Then delete the private `FindComponentType` method (lines 106-146).

**Step 4: Update `CreatePrefabTool.cs` to use `SerializedFieldUtils.FindType`**

In `Editor/Tools/CreatePrefabTool.cs`, replace the `AddComponent` method body (lines 98-132) to use the shared utility:

```csharp
private Component AddComponent(GameObject gameObject, string componentName)
{
    Type scriptType = SerializedFieldUtils.FindType(componentName, typeof(Component));
    if (scriptType == null)
    {
        return null;
    }
    return gameObject.AddComponent(scriptType);
}
```

**Step 5: Commit**

```bash
git add Editor/Utils/SerializedFieldUtils.cs Editor/Tools/UpdateComponentTool.cs Editor/Tools/RemoveComponentTool.cs Editor/Tools/CreatePrefabTool.cs
git commit -m "refactor: extract FindType into SerializedFieldUtils"
```

---

### Task 2: Extract `SerializedFieldUtils` — Field Setting & Object References

**Files:**
- Modify: `Editor/Utils/SerializedFieldUtils.cs`
- Modify: `Editor/Tools/UpdateComponentTool.cs`

**Step 1: Move `ConvertJTokenToValue` to `SerializedFieldUtils`**

Copy the `ConvertJTokenToValue` method from `UpdateComponentTool.cs` (lines 337-470) into `SerializedFieldUtils` as a `public static` method. Update the `$ref` handler inside it to call `SerializedFieldUtils.ResolveObjectReference` (which we'll move next).

**Step 2: Move `ResolveObjectReference` to `SerializedFieldUtils`**

Copy `ResolveObjectReference` from `UpdateComponentTool.cs` (lines 480-608) into `SerializedFieldUtils` as a `public static` method. It needs `FindGameObjectByPath` — also move that as a `public static` method (lines 142-183). The `FindComponentType` calls inside `ResolveObjectReference` are already replaced with `SerializedFieldUtils.FindType` from Task 1.

**Step 3: Move `UpdateComponentData` to `SerializedFieldUtils` as `UpdateFieldsFromJson`**

Copy `UpdateComponentData` from `UpdateComponentTool.cs` (lines 248-329) into `SerializedFieldUtils` as:

```csharp
/// <summary>
/// Update fields and properties on a UnityEngine.Object from a JSON object.
/// Works for Components, ScriptableObjects, and any UnityEngine.Object subclass.
/// </summary>
/// <param name="target">The object to update</param>
/// <param name="fieldData">JSON key/value pairs mapping field/property names to values</param>
/// <param name="errorMessage">Error message if any field fails to set</param>
/// <returns>True if all fields were set successfully</returns>
public static bool UpdateFieldsFromJson(UnityEngine.Object target, JObject fieldData, out string errorMessage)
```

The signature changes from `(Component component, JObject componentData, out string errorMessage)` to `(UnityEngine.Object target, JObject fieldData, out string errorMessage)`. The method body is identical — `component.GetType()` becomes `target.GetType()`, parameter names updated. The `Undo.RecordObject` call stays inside this method.

**Step 4: Update `UpdateComponentTool.cs` to call shared methods**

Replace the call at line 112:
```csharp
// Before:
bool success = UpdateComponentData(component, componentData, out string errorMessage);
// After:
bool success = SerializedFieldUtils.UpdateFieldsFromJson(component, componentData, out string errorMessage);
```

Delete the private methods: `ConvertJTokenToValue`, `ResolveObjectReference`, `FindGameObjectByPath`, `UpdateComponentData`. The tool becomes a thin wrapper.

**Step 5: Commit**

```bash
git add Editor/Utils/SerializedFieldUtils.cs Editor/Tools/UpdateComponentTool.cs
git commit -m "refactor: extract field setting and object resolution into SerializedFieldUtils"
```

---

### Task 3: Build Verification After Refactor

**Step 1: Build the Node server**

```bash
cd Server~ && npm run build
```

Expected: Clean build with no errors. No TS changes were made yet, so this verifies the build system is healthy.

**Step 2: Verify C# compilation status**

C# compilation happens in Unity Editor. Since we can't run Unity from CLI, verify the files are syntactically correct by reviewing them. Ensure:
- All `using` statements are present in `SerializedFieldUtils.cs`
- `UpdateComponentTool.cs` has no references to deleted methods
- `RemoveComponentTool.cs` has no references to deleted methods
- `CreatePrefabTool.cs` has no references to deleted methods
- No circular dependencies

**Step 3: Commit if any fixes needed**

```bash
git add -A && git commit -m "fix: address compilation issues from refactor"
```

---

### Task 4: Add `$ref: "asset"` Support

**Files:**
- Modify: `Editor/Utils/SerializedFieldUtils.cs`
- Modify: `Server~/src/tools/updateComponentTool.ts`

**Step 1: Extend `ResolveObjectReference` in `SerializedFieldUtils.cs`**

Add asset reference resolution. The method currently only handles `$ref: "scene"`. Add a branch for `$ref: "asset"`:

```csharp
// In ResolveObjectReference, after the refType check:
if (refType == "asset")
{
    return ResolveAssetReference(refDescriptor, targetType);
}
```

Add the new private static method:

```csharp
/// <summary>
/// Resolves a $ref: "asset" descriptor to a Unity asset.
/// Supports assetPath, guid, or both (must agree).
/// If target type is a Component and the asset is a prefab (GameObject), GetComponent is used.
/// </summary>
private static UnityEngine.Object ResolveAssetReference(JObject refDescriptor, Type targetType)
{
    string assetPath = refDescriptor["assetPath"]?.ToObject<string>();
    string guid = refDescriptor["guid"]?.ToObject<string>();

    if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
    {
        throw new Exception("Asset reference must provide either 'assetPath' or 'guid'");
    }

    // Resolve guid to path
    string guidResolvedPath = null;
    if (!string.IsNullOrEmpty(guid))
    {
        guidResolvedPath = AssetDatabase.GUIDToAssetPath(guid);
        if (string.IsNullOrEmpty(guidResolvedPath))
        {
            throw new Exception($"No asset found for GUID '{guid}'");
        }
    }

    // If both provided, validate they agree
    if (!string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(guidResolvedPath))
    {
        if (!string.Equals(assetPath, guidResolvedPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new Exception(
                $"Asset path mismatch: assetPath='{assetPath}' but GUID '{guid}' resolves to '{guidResolvedPath}'");
        }
    }

    string resolvedPath = !string.IsNullOrEmpty(assetPath) ? assetPath : guidResolvedPath;

    // Load the asset
    UnityEngine.Object asset = AssetDatabase.LoadAssetAtPath(resolvedPath, typeof(UnityEngine.Object));
    if (asset == null)
    {
        throw new Exception($"No asset found at path '{resolvedPath}'");
    }

    // If target type is a Component and the asset is a GameObject (prefab), use GetComponent
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

    // If target type is GameObject and asset is a GameObject, return directly
    if (targetType == typeof(GameObject) && asset is GameObject go)
    {
        return go;
    }

    // For any other type, check assignability
    if (!targetType.IsAssignableFrom(asset.GetType()))
    {
        throw new Exception(
            $"Asset at '{resolvedPath}' is of type '{asset.GetType().Name}', not assignable to field type '{targetType.Name}'");
    }

    return asset;
}
```

Also update the `$ref` error message and the UnityEngine.Object fallback error message in `ConvertJTokenToValue` to mention `"asset"`:

```csharp
// In ConvertJTokenToValue, the error for plain JSON on Object fields:
throw new Exception(
    $"Cannot set field of type '{targetType.Name}' from a plain JSON value. " +
    "Use a $ref descriptor: {\"$ref\": \"scene\", \"objectPath\": \"Path/To/Object\"} or " +
    "{\"$ref\": \"asset\", \"assetPath\": \"Assets/Path/To/Asset.ext\"}");
```

**Step 2: Update `updateComponentTool.ts` description**

Add asset reference documentation to the tool description:

```typescript
const toolDescription = `Updates component fields on a GameObject or adds it to the GameObject if it does not contain the component.

For setting references to scene objects (GameObjects, Components, MonoBehaviours), use a reference descriptor instead of a plain value in componentData:
  { "$ref": "scene", "objectPath": "Path/To/Object" }
  { "$ref": "scene", "instanceId": 12345 }
  { "$ref": "scene", "objectPath": "Path/To/Object", "componentType": "Camera" }

For setting references to asset objects (ScriptableObjects, Prefabs, Materials, Textures, etc.):
  { "$ref": "asset", "assetPath": "Assets/Path/To/Asset.ext" }
  { "$ref": "asset", "guid": "a1b2c3d4e5f6..." }
  { "$ref": "asset", "assetPath": "Assets/Prefabs/Enemy.prefab", "componentType": "EnemyAI" }

If both assetPath and guid are provided, they must point to the same asset.

When componentType is omitted, the type is inferred from the field's declared type.
When the field type is GameObject, the GameObject itself is returned.
When the field type is a Component subclass, GetComponent is called automatically using the field type.
Use componentType to override inference (e.g., when the field type is a base class like Collider and you want a specific BoxCollider).`;
```

**Step 3: Commit**

```bash
git add Editor/Utils/SerializedFieldUtils.cs "Server~/src/tools/updateComponentTool.ts"
git commit -m "feat: add \$ref asset reference support"
```

---

### Task 5: Implement `create_scriptable_object` C# Tool

**Files:**
- Create: `Editor/Tools/CreateScriptableObjectTool.cs`

**Step 1: Create the tool**

```csharp
using System;
using System.IO;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for creating ScriptableObject assets in the Unity Editor
    /// </summary>
    public class CreateScriptableObjectTool : McpToolBase
    {
        public CreateScriptableObjectTool()
        {
            Name = "create_scriptable_object";
            Description = "Creates a ScriptableObject asset of the specified type with optional initial field values";
        }

        public override JObject Execute(JObject parameters)
        {
            string scriptableObjectType = parameters["scriptableObjectType"]?.ToObject<string>();
            string savePath = parameters["savePath"]?.ToObject<string>();
            JObject fieldData = parameters["fieldData"] as JObject;
            bool overwrite = parameters["overwrite"]?.ToObject<bool>() ?? false;

            // Validate required parameter
            if (string.IsNullOrEmpty(scriptableObjectType))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'scriptableObjectType' not provided",
                    "validation_error"
                );
            }

            // Resolve the ScriptableObject type
            Type soType = SerializedFieldUtils.FindType(scriptableObjectType, typeof(ScriptableObject));
            if (soType == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"ScriptableObject type '{scriptableObjectType}' not found. Ensure the class exists and inherits from ScriptableObject.",
                    "type_error"
                );
            }

            // Resolve and validate save path
            if (string.IsNullOrEmpty(savePath))
            {
                savePath = $"Assets/{soType.Name}.asset";
            }

            // Validate path
            if (!savePath.StartsWith("Assets/"))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Save path must start with 'Assets/'",
                    "validation_error"
                );
            }

            if (savePath.Contains(".."))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Save path must not contain '..' path traversal",
                    "validation_error"
                );
            }

            if (!savePath.EndsWith(".asset"))
            {
                savePath += ".asset";
            }

            // Handle collision
            if (!overwrite && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(savePath, AssetPathToGUIDOptions.OnlyExistingAssets)))
            {
                string basePath = savePath.Substring(0, savePath.Length - ".asset".Length);
                int counter = 1;
                do
                {
                    savePath = $"{basePath}_{counter}.asset";
                    counter++;
                } while (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(savePath, AssetPathToGUIDOptions.OnlyExistingAssets)));
            }

            // Ensure directory exists
            string directory = Path.GetDirectoryName(savePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // Create the ScriptableObject instance
            ScriptableObject instance = ScriptableObject.CreateInstance(soType);
            if (instance == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to create instance of ScriptableObject type '{scriptableObjectType}'",
                    "creation_error"
                );
            }

            instance.name = Path.GetFileNameWithoutExtension(savePath);

            // Set field data if provided
            string fieldError = null;
            if (fieldData != null && fieldData.Count > 0)
            {
                bool fieldSuccess = SerializedFieldUtils.UpdateFieldsFromJson(instance, fieldData, out fieldError);
                if (!fieldSuccess)
                {
                    // Clean up the instance since we haven't saved it yet
                    UnityEngine.Object.DestroyImmediate(instance);
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Failed to set field data: {fieldError}",
                        "field_error"
                    );
                }
            }

            // Save the asset
            AssetDatabase.CreateAsset(instance, savePath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            string guid = AssetDatabase.AssetPathToGUID(savePath);

            McpLogger.LogInfo($"[MCP Unity] Created ScriptableObject '{soType.Name}' at '{savePath}' (GUID: {guid})");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully created ScriptableObject '{soType.Name}' at '{savePath}'",
                ["data"] = new JObject
                {
                    ["assetPath"] = savePath,
                    ["typeName"] = soType.FullName,
                    ["guid"] = guid
                }
            };
        }
    }
}
```

**Step 2: Commit**

```bash
git add Editor/Tools/CreateScriptableObjectTool.cs
git commit -m "feat: add create_scriptable_object C# tool"
```

---

### Task 6: Implement `update_scriptable_object` C# Tool

**Files:**
- Create: `Editor/Tools/UpdateScriptableObjectTool.cs`

**Step 1: Create the tool**

```csharp
using System;
using McpUnity.Unity;
using McpUnity.Utils;
using UnityEngine;
using UnityEditor;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool for updating field values on existing ScriptableObject assets
    /// </summary>
    public class UpdateScriptableObjectTool : McpToolBase
    {
        public UpdateScriptableObjectTool()
        {
            Name = "update_scriptable_object";
            Description = "Updates field values on an existing ScriptableObject asset";
        }

        public override JObject Execute(JObject parameters)
        {
            string assetPath = parameters["assetPath"]?.ToObject<string>();
            string guid = parameters["guid"]?.ToObject<string>();
            JObject fieldData = parameters["fieldData"] as JObject;

            // Validate: at least one identifier required
            if (string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(guid))
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Either 'assetPath' or 'guid' must be provided",
                    "validation_error"
                );
            }

            // Validate fieldData
            if (fieldData == null || fieldData.Count == 0)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    "Required parameter 'fieldData' must be provided and non-empty",
                    "validation_error"
                );
            }

            // Resolve path from guid if provided
            string guidResolvedPath = null;
            if (!string.IsNullOrEmpty(guid))
            {
                guidResolvedPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(guidResolvedPath))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"No asset found for GUID '{guid}'",
                        "not_found_error"
                    );
                }
            }

            // If both provided, validate they agree
            if (!string.IsNullOrEmpty(assetPath) && !string.IsNullOrEmpty(guidResolvedPath))
            {
                if (!string.Equals(assetPath, guidResolvedPath, StringComparison.OrdinalIgnoreCase))
                {
                    return McpUnitySocketHandler.CreateErrorResponse(
                        $"Asset path mismatch: assetPath='{assetPath}' but GUID '{guid}' resolves to '{guidResolvedPath}'",
                        "validation_error"
                    );
                }
            }

            string resolvedPath = !string.IsNullOrEmpty(assetPath) ? assetPath : guidResolvedPath;

            // Load the ScriptableObject
            ScriptableObject so = AssetDatabase.LoadAssetAtPath<ScriptableObject>(resolvedPath);
            if (so == null)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"No ScriptableObject found at path '{resolvedPath}'",
                    "not_found_error"
                );
            }

            // Update fields
            bool success = SerializedFieldUtils.UpdateFieldsFromJson(so, fieldData, out string errorMessage);
            if (!success)
            {
                return McpUnitySocketHandler.CreateErrorResponse(
                    $"Failed to update fields: {errorMessage}",
                    "update_error"
                );
            }

            // Save changes
            EditorUtility.SetDirty(so);
            AssetDatabase.SaveAssets();

            string resolvedGuid = AssetDatabase.AssetPathToGUID(resolvedPath);

            McpLogger.LogInfo($"[MCP Unity] Updated ScriptableObject '{so.GetType().Name}' at '{resolvedPath}'");

            return new JObject
            {
                ["success"] = true,
                ["type"] = "text",
                ["message"] = $"Successfully updated ScriptableObject '{so.GetType().Name}' at '{resolvedPath}'",
                ["data"] = new JObject
                {
                    ["assetPath"] = resolvedPath,
                    ["typeName"] = so.GetType().FullName,
                    ["guid"] = resolvedGuid
                }
            };
        }
    }
}
```

Note: `UpdateFieldsFromJson` already calls `Undo.RecordObject` internally, so we don't need to call it again here.

**Step 2: Commit**

```bash
git add Editor/Tools/UpdateScriptableObjectTool.cs
git commit -m "feat: add update_scriptable_object C# tool"
```

---

### Task 7: Implement TypeScript Tools + Registration

**Files:**
- Create: `Server~/src/tools/createScriptableObjectTool.ts`
- Create: `Server~/src/tools/updateScriptableObjectTool.ts`
- Modify: `Server~/src/index.ts`
- Modify: `Editor/UnityBridge/McpUnityServer.cs`

**Step 1: Create `createScriptableObjectTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'create_scriptable_object';
const toolDescription = `Creates a ScriptableObject asset of the specified type with optional initial field values.

The scriptableObjectType parameter should be the C# class name (e.g. "GameConfig", "WeaponData").

For setting references to other assets in fieldData, use $ref descriptors:
  { "$ref": "asset", "assetPath": "Assets/Path/To/Asset.ext" }
  { "$ref": "asset", "guid": "a1b2c3d4e5f6..." }
  { "$ref": "scene", "objectPath": "Path/To/SceneObject" }

If both assetPath and guid are provided in a $ref, they must point to the same asset.`;

const paramsSchema = z.object({
  scriptableObjectType: z.string().describe('The C# class name of the ScriptableObject type to create'),
  savePath: z.string().optional().describe('Asset save path (e.g. "Assets/Config/GameConfig.asset"). Defaults to "Assets/{TypeName}.asset"'),
  fieldData: z.record(z.any()).optional().describe('Optional key/value pairs to set on the ScriptableObject fields'),
  overwrite: z.boolean().optional().default(false).describe('When true, overwrite an existing asset at the target path')
});

export function registerCreateScriptableObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if (!params.scriptableObjectType || params.scriptableObjectType.trim() === '') {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Required parameter 'scriptableObjectType' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      scriptableObjectType: params.scriptableObjectType,
      savePath: params.savePath,
      fieldData: params.fieldData,
      overwrite: params.overwrite
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to create ScriptableObject'
    );
  }

  let text = response.message || 'ScriptableObject created successfully';
  if (response.data) {
    text += `\n\nAsset path: ${response.data.assetPath}`;
    text += `\nType: ${response.data.typeName}`;
    text += `\nGUID: ${response.data.guid}`;
  }

  return {
    content: [{
      type: response.type,
      text
    }]
  };
}
```

**Step 2: Create `updateScriptableObjectTool.ts`**

```typescript
import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'update_scriptable_object';
const toolDescription = `Updates field values on an existing ScriptableObject asset. Identify the asset by assetPath, guid, or both (must agree).

For setting references to other assets or scene objects in fieldData, use $ref descriptors:
  { "$ref": "asset", "assetPath": "Assets/Path/To/Asset.ext" }
  { "$ref": "asset", "guid": "a1b2c3d4e5f6..." }
  { "$ref": "scene", "objectPath": "Path/To/SceneObject" }

If both assetPath and guid are provided in a $ref, they must point to the same asset.`;

const paramsSchema = z.object({
  assetPath: z.string().optional().describe('Path to the ScriptableObject asset (e.g. "Assets/Config/GameConfig.asset")'),
  guid: z.string().optional().describe('GUID of the ScriptableObject asset (found in .meta files)'),
  fieldData: z.record(z.any()).describe('Key/value pairs of fields to update on the ScriptableObject')
});

export function registerUpdateScriptableObjectTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  if ((!params.assetPath || params.assetPath.trim() === '') &&
      (!params.guid || params.guid.trim() === '')) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either 'assetPath' or 'guid' must be provided"
    );
  }

  if (!params.fieldData || Object.keys(params.fieldData).length === 0) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Required parameter 'fieldData' must be provided and non-empty"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      assetPath: params.assetPath,
      guid: params.guid,
      fieldData: params.fieldData
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to update ScriptableObject'
    );
  }

  let text = response.message || 'ScriptableObject updated successfully';
  if (response.data) {
    text += `\n\nAsset path: ${response.data.assetPath}`;
    text += `\nType: ${response.data.typeName}`;
    text += `\nGUID: ${response.data.guid}`;
  }

  return {
    content: [{
      type: response.type,
      text
    }]
  };
}
```

**Step 3: Register in `Server~/src/index.ts`**

Add imports after line 31 (after `saveAsPrefabTool` import):
```typescript
import { registerCreateScriptableObjectTool } from './tools/createScriptableObjectTool.js';
import { registerUpdateScriptableObjectTool } from './tools/updateScriptableObjectTool.js';
```

Add registration calls after line 99 (after `registerSaveAsPrefabTool`):
```typescript
registerCreateScriptableObjectTool(server, mcpUnity, toolLogger);
registerUpdateScriptableObjectTool(server, mcpUnity, toolLogger);
```

**Step 4: Register in `Editor/UnityBridge/McpUnityServer.cs`**

Add tool registration before the BatchExecuteTool registration (before line 434):
```csharp
// Register CreateScriptableObjectTool
CreateScriptableObjectTool createScriptableObjectTool = new CreateScriptableObjectTool();
_tools.Add(createScriptableObjectTool.Name, createScriptableObjectTool);

// Register UpdateScriptableObjectTool
UpdateScriptableObjectTool updateScriptableObjectTool = new UpdateScriptableObjectTool();
_tools.Add(updateScriptableObjectTool.Name, updateScriptableObjectTool);
```

**Step 5: Commit**

```bash
git add -f "Server~/src/tools/createScriptableObjectTool.ts" "Server~/src/tools/updateScriptableObjectTool.ts" "Server~/src/index.ts" Editor/UnityBridge/McpUnityServer.cs
git commit -m "feat: add ScriptableObject TypeScript tools and register all"
```

---

### Task 8: Meta Files, Build, and AGENTS.md

**Files:**
- Create: `Editor/Tools/CreateScriptableObjectTool.cs.meta`
- Create: `Editor/Tools/UpdateScriptableObjectTool.cs.meta`
- Create: `Editor/Utils/SerializedFieldUtils.cs.meta`
- Modify: `AGENTS.md`

**Step 1: Generate meta files**

Generate 3 unique GUIDs and create `.meta` files for each new C# file:
- `Editor/Utils/SerializedFieldUtils.cs.meta`
- `Editor/Tools/CreateScriptableObjectTool.cs.meta`
- `Editor/Tools/UpdateScriptableObjectTool.cs.meta`

Format:
```
fileFormatVersion: 2
guid: <32-char-hex>
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData: 
  assetBundleName: 
  assetBundleVariant: 
```

Use `python3 -c "import uuid; print(uuid.uuid4().hex[:32])"` to generate each GUID.

**Step 2: Update `AGENTS.md`**

Add to the "Available tools" section:
- `create_scriptable_object` — Create ScriptableObject assets with optional initial field values
- `update_scriptable_object` — Update field values on existing ScriptableObject assets

**Step 3: Build Node server**

```bash
cd Server~ && npm run build
```

Expected: Clean build.

**Step 4: Commit**

```bash
git add Editor/Tools/CreateScriptableObjectTool.cs.meta Editor/Tools/UpdateScriptableObjectTool.cs.meta Editor/Utils/SerializedFieldUtils.cs.meta AGENTS.md
git commit -m "docs: add meta files and update AGENTS.md with ScriptableObject tools"
```

---

### Task 9: Code Review

**Step 1: Review all changes**

Review the full diff from the branch against `main`. Check for:
- Consistent naming (snake_case for tool names, camelCase for TS, PascalCase for C#)
- All deleted methods actually removed (no orphaned code in UpdateComponentTool)
- `SerializedFieldUtils` has all necessary `using` statements
- Asset path validation consistency between create and update tools
- `$ref: "asset"` error messages are clear and actionable
- TS descriptions accurately document the C# behavior
- No missing registrations (both sides)

**Step 2: Fix any issues found and commit**

```bash
git add -A && git commit -m "fix: address review findings"
```
