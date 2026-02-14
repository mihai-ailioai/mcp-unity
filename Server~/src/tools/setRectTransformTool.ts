import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'set_rect_transform';
const toolDescription =
  'Sets RectTransform layout properties on a UI GameObject. Supports presets (stretch, center, top-left, top-center, top-right, middle-left, middle-right, bottom-left, bottom-center, bottom-right, stretch-horizontal, stretch-vertical) and optional raw overrides (anchoredPosition, sizeDelta, anchorMin, anchorMax, pivot, offsetMin, offsetMax). Preset is applied first, then raw overrides.';

const vector2Schema = z
  .object({
    x: z.number().optional(),
    y: z.number().optional()
  })
  .describe('Vector2 with optional x/y components');

const presetSchema = z.enum([
  'stretch',
  'center',
  'top-left',
  'top-center',
  'top-right',
  'middle-left',
  'middle-right',
  'bottom-left',
  'bottom-center',
  'bottom-right',
  'stretch-horizontal',
  'stretch-vertical'
]);

const paramsSchema = z.object({
  instanceId: z.number().optional().describe('The instance ID of the target GameObject'),
  objectPath: z.string().optional().describe('The hierarchy path of the target GameObject (alternative to instanceId)'),
  preset: presetSchema.optional().describe('Optional layout preset to apply first'),
  anchoredPosition: vector2Schema.optional().describe('Optional anchoredPosition override'),
  sizeDelta: vector2Schema.optional().describe('Optional sizeDelta override'),
  anchorMin: vector2Schema.optional().describe('Optional anchorMin override'),
  anchorMax: vector2Schema.optional().describe('Optional anchorMax override'),
  pivot: vector2Schema.optional().describe('Optional pivot override'),
  offsetMin: vector2Schema.optional().describe('Optional offsetMin override'),
  offsetMax: vector2Schema.optional().describe('Optional offsetMax override')
});

export function registerSetRectTransformTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: any) => {
    try {
      logger.info(`Executing tool: ${toolName}`, params);
      const result = await toolHandler(mcpUnity, params);
      logger.info(`Tool execution successful: ${toolName}`);
      return result;
    } catch (error) {
      logger.error(`Tool execution failed: ${toolName}`, error);
      throw error;
    }
  });
}

async function toolHandler(mcpUnity: McpUnity, params: any): Promise<CallToolResult> {
  validateGameObjectIdentifier(params);

  // Validate at least one layout input is provided
  const hasLayoutInput = params.preset !== undefined ||
    params.anchoredPosition !== undefined || params.sizeDelta !== undefined ||
    params.anchorMin !== undefined || params.anchorMax !== undefined ||
    params.pivot !== undefined || params.offsetMin !== undefined ||
    params.offsetMax !== undefined;

  if (!hasLayoutInput) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "At least one of 'preset', 'anchoredPosition', 'sizeDelta', 'anchorMin', 'anchorMax', 'pivot', 'offsetMin', or 'offsetMax' must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      instanceId: params.instanceId,
      objectPath: params.objectPath,
      preset: params.preset,
      anchoredPosition: params.anchoredPosition,
      sizeDelta: params.sizeDelta,
      anchorMin: params.anchorMin,
      anchorMax: params.anchorMax,
      pivot: params.pivot,
      offsetMin: params.offsetMin,
      offsetMax: params.offsetMax
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to set RectTransform'
    );
  }

  // Include structured RectTransform state in response for programmatic use
  let text = response.message || 'RectTransform updated';
  if (response.data?.rectTransform) {
    const rt = response.data.rectTransform;
    text += `\n\nCurrent state:`;
    text += `\n  anchoredPosition: (${rt.anchoredPosition.x}, ${rt.anchoredPosition.y})`;
    text += `\n  sizeDelta: (${rt.sizeDelta.x}, ${rt.sizeDelta.y})`;
    text += `\n  anchorMin: (${rt.anchorMin.x}, ${rt.anchorMin.y})`;
    text += `\n  anchorMax: (${rt.anchorMax.x}, ${rt.anchorMax.y})`;
    text += `\n  pivot: (${rt.pivot.x}, ${rt.pivot.y})`;
  }

  return {
    content: [
      {
        type: response.type,
        text
      }
    ]
  };
}

function validateGameObjectIdentifier(params: { instanceId?: number; objectPath?: string }) {
  if (
    (params.instanceId === undefined || params.instanceId === null) &&
    (!params.objectPath || params.objectPath.trim() === '')
  ) {
    throw new McpUnityError(ErrorType.VALIDATION, "Either 'instanceId' or 'objectPath' must be provided");
  }
}
