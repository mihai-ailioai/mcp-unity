import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'create_primitive';
const toolDescription = 'Creates a primitive GameObject (Cube, Sphere, Capsule, Cylinder, Plane, Quad) in the current scene';
const paramsSchema = z.object({
  primitiveType: z.enum(['Cube', 'Sphere', 'Capsule', 'Cylinder', 'Plane', 'Quad']).describe('The type of primitive to create'),
  name: z.string().optional().describe('Optional name for the GameObject (defaults to the primitive type name)'),
  parentPath: z.string().optional().describe('Optional path of a parent GameObject in the hierarchy'),
  parentId: z.number().optional().describe('Optional instance ID of a parent GameObject'),
  position: z.object({
    x: z.number().optional(),
    y: z.number().optional(),
    z: z.number().optional()
  }).optional().describe('Optional local position'),
  rotation: z.object({
    x: z.number().optional(),
    y: z.number().optional(),
    z: z.number().optional()
  }).optional().describe('Optional local rotation in euler angles'),
  scale: z.object({
    x: z.number().optional(),
    y: z.number().optional(),
    z: z.number().optional()
  }).optional().describe('Optional local scale (defaults to 1,1,1)')
});

export function registerCreatePrimitiveTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      primitiveType: params.primitiveType,
      name: params.name,
      parentPath: params.parentPath,
      parentId: params.parentId,
      position: params.position,
      rotation: params.rotation,
      scale: params.scale
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to create primitive'
    );
  }

  return {
    content: [{
      type: response.type,
      text: response.message
    }]
  };
}
