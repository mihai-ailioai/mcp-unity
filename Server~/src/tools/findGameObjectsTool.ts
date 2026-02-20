import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'find_gameobjects';
const toolDescription =
  'Finds GameObjects in the current scene hierarchy using one or more filters (componentType, namePattern, tag, layer). ' +
  'Examples: Find all Animators (componentType="Animator"), find all buttons by name (namePattern="*Button*"), or find gameplay objects by tag/layer.';

const paramsSchema = z.object({
  componentType: z.string().optional().describe('Optional component type name, e.g. Animator or RectTransform.'),
  namePattern: z
    .string()
    .optional()
    .describe('Optional name search pattern. Substring match by default; supports simple * glob forms like start*, *end, *contains*.'),
  tag: z.string().optional().describe('Optional Unity tag to match.'),
  layer: z.union([z.string(), z.number().int()]).optional().describe('Optional Unity layer name or index.'),
  includeInactive: z.boolean().optional().describe('When true, includes inactive GameObjects. Defaults to false.'),
  rootPath: z.string().optional().describe('Optional hierarchy path that limits search scope to that object and its descendants.'),
});

export function registerFindGameObjectsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger): void {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(toolName, toolDescription, paramsSchema.shape, async (params: z.infer<typeof paramsSchema>) => {
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

async function toolHandler(mcpUnity: McpUnity, rawParams: z.infer<typeof paramsSchema>): Promise<CallToolResult> {
  const parsedParams = paramsSchema.safeParse(rawParams);
  if (!parsedParams.success) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      `Invalid parameters for ${toolName}`,
      parsedParams.error.flatten()
    );
  }

  const params = parsedParams.data;
  const hasFilter =
    (params.componentType?.trim().length ?? 0) > 0 ||
    (params.namePattern?.trim().length ?? 0) > 0 ||
    (params.tag?.trim().length ?? 0) > 0 ||
    params.layer !== undefined;

  if (!hasFilter) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      'At least one filter must be provided: componentType, namePattern, tag, or layer'
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      componentType: params.componentType,
      namePattern: params.namePattern,
      tag: params.tag,
      layer: params.layer,
      includeInactive: params.includeInactive ?? false,
      rootPath: params.rootPath,
    },
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to find GameObjects');
  }

  return {
    content: [
      {
        type: 'text',
        text: JSON.stringify(response, null, 2),
      },
    ],
  };
}
