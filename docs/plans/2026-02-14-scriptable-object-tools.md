# ScriptableObject Tools — Design

**Date:** 2026-02-14
**Status:** Approved
**Branch:** `feat/scriptable-object-tools`

## Problem

MCP Unity cannot create or modify ScriptableObject assets. SOs are how Unity projects store game configuration, AI behaviors, item databases, etc. This is the highest-impact remaining gap.

## Solution

Two new tools (`create_scriptable_object`, `update_scriptable_object`) plus a prerequisite shared utility extraction and `$ref: "asset"` support.

## Design Decisions

- **Two separate tools** (consistent with existing patterns: create_prefab/save_as_prefab, create_material/modify_material)
- **Shared utility extraction** — `ConvertJTokenToValue`, `UpdateFieldsFromJson`, `ResolveObjectReference`, `FindType` pulled into `Editor/Utils/SerializedFieldUtils.cs` to avoid duplication
- **`$ref: "asset"` support** — SOs without asset references have limited utility; added alongside scene refs
- **`fieldData` on create** — avoids mandatory two-step workflow
- **`assetPath` + `guid` identification** — agents see GUIDs in .meta files and serialized references; at least one required, if both provided they must agree
- **Update is fields only** — no rename/move (separate future `move_asset` concern)

## `$ref: "asset"` Protocol

```json
{"weaponSprite": {"$ref": "asset", "assetPath": "Assets/Sprites/sword.png"}}
{"baseConfig": {"$ref": "asset", "guid": "a1b2c3d4e5f6..."}}
{"enemyPrefab": {"$ref": "asset", "assetPath": "Assets/Prefabs/Goblin.prefab", "componentType": "EnemyAI"}}
```

Resolution:
1. Validate `$ref` is `"scene"` or `"asset"`
2. For `"asset"`: require at least one of `assetPath` or `guid`
3. If `guid` provided, resolve to path via `AssetDatabase.GUIDToAssetPath`
4. If both provided, validate they agree (error if mismatch)
5. Load via `AssetDatabase.LoadAssetAtPath(path, targetType)`
6. If target type is Component and loaded asset is GameObject (prefab), use `GetComponent`
7. `componentType` optional override for base-class fields

## `create_scriptable_object`

**Parameters:**
- `scriptableObjectType` (string, required) — C# class name
- `savePath` (string, optional) — defaults `"Assets/{TypeName}.asset"`, auto-suffix on collision
- `fieldData` (object, optional) — key/value pairs via shared `UpdateFieldsFromJson`
- `overwrite` (bool, optional, default false)

**Flow:**
1. `FindType(typeName, typeof(ScriptableObject))`
2. Validate path: `Assets/` prefix, `.asset` extension, no `..` traversal
3. Collision: auto-suffix if !overwrite
4. `Directory.CreateDirectory` if needed
5. `ScriptableObject.CreateInstance(type)`
6. `UpdateFieldsFromJson(instance, fieldData)` if provided
7. `AssetDatabase.CreateAsset(instance, path)`
8. `AssetDatabase.SaveAssets()` + `AssetDatabase.Refresh()`
9. Return path, type name, GUID

## `update_scriptable_object`

**Parameters:**
- `assetPath` (string, optional)
- `guid` (string, optional)
- `fieldData` (object, required)

At least one of `assetPath`/`guid` required. Both provided → validate agreement.

**Flow:**
1. Resolve path from `assetPath` and/or `guid`
2. `AssetDatabase.LoadAssetAtPath<ScriptableObject>(path)`
3. `Undo.RecordObject(so, "Update ScriptableObject")`
4. `UpdateFieldsFromJson(so, fieldData)`
5. `EditorUtility.SetDirty(so)`
6. `AssetDatabase.SaveAssets()`
7. Return path, type name, GUID, fields summary

## Shared Utility: `Editor/Utils/SerializedFieldUtils.cs`

Extracted from `UpdateComponentTool.cs`:
- `ConvertJTokenToValue(JToken token, Type targetType)` — vectors, colors, enums, $ref resolution
- `UpdateFieldsFromJson(UnityEngine.Object target, JObject fieldData)` — reflection field/property loop
- `ResolveObjectReference(JObject refDescriptor, Type targetType)` — scene + asset resolution
- `FindType(string typeName, Type baseConstraint)` — assembly-wide scan with namespace shortcuts

Callers updated: `UpdateComponentTool`, `RemoveComponentTool`, `CreatePrefabTool`.

## Implementation Order

1. Extract `SerializedFieldUtils` (refactor, no behavior change) → build to verify
2. Add `$ref: "asset"` support in shared utility
3. Implement `create_scriptable_object` (C# + TS + registration)
4. Implement `update_scriptable_object` (C# + TS + registration)
5. Meta files + AGENTS.md update
6. Build + review

Step 1 is riskiest (touches working code). Steps 2-4 are additive.
