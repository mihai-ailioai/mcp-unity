import * as z from 'zod';
import { McpUnityError, ErrorType } from '../utils/errors.js';
const toolName = 'get_scriptable_object';
const toolDescription = `Reads all user-defined field values from a ScriptableObject asset. Identify the asset by assetPath, guid, or both (must agree).

Returns field values in JSON format. UnityEngine.Object references are returned as $ref descriptors:
  { "$ref": "asset", "assetPath": "...", "guid": "...", "typeName": "..." }
  { "$ref": "scene", "instanceId": 12345, "objectPath": "...", "typeName": "..." }

These $ref values can be passed directly to update_scriptable_object or update_component for round-trip editing.`;
const paramsSchema = z.object({
    assetPath: z.string().optional().describe('Path to the ScriptableObject asset (e.g. "Assets/Config/GameConfig.asset")'),
    guid: z.string().optional().describe('GUID of the ScriptableObject asset (found in .meta files)')
});
export function registerGetScriptableObjectTool(server, mcpUnity, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async (params) => {
        try {
            logger.info(`Executing tool: ${toolName}`, params);
            const result = await toolHandler(mcpUnity, params);
            logger.info(`Tool execution successful: ${toolName}`);
            return result;
        }
        catch (error) {
            logger.error(`Tool execution failed: ${toolName}`, error);
            throw error;
        }
    });
}
async function toolHandler(mcpUnity, params) {
    if ((!params.assetPath || params.assetPath.trim() === '') &&
        (!params.guid || params.guid.trim() === '')) {
        throw new McpUnityError(ErrorType.VALIDATION, "Either 'assetPath' or 'guid' must be provided");
    }
    const response = await mcpUnity.sendRequest({
        method: toolName,
        params: {
            assetPath: params.assetPath,
            guid: params.guid
        }
    });
    if (!response.success) {
        throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to read ScriptableObject');
    }
    let text = response.message || 'ScriptableObject read successfully';
    if (response.data) {
        text += `\n\nAsset path: ${response.data.assetPath}`;
        text += `\nType: ${response.data.typeName}`;
        text += `\nGUID: ${response.data.guid}`;
        text += `\nName: ${response.data.name}`;
        text += `\n\nField data:\n${JSON.stringify(response.data.fieldData, null, 2)}`;
    }
    return {
        content: [{
                type: response.type,
                text
            }]
    };
}
