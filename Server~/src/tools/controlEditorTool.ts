import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'control_editor';
const toolDescription = 'Controls Unity editor play mode state. Actions: play (enter play mode), pause (pause play mode), unpause (resume play mode), stop (exit play mode), step (advance one frame while playing). Note: play and stop can trigger Unity domain reload.';
const paramsSchema = z.object({
  action: z.enum(['play', 'pause', 'unpause', 'stop', 'step'])
});

export function registerControlEditorTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity, params, logger);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity, params: z.infer<typeof paramsSchema>, logger: Logger): Promise<CallToolResult> {
  const { action } = params;

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: { action }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || 'Failed to control editor state'
    );
  }

  let message = response.message || 'Editor state updated.';

  const shouldWaitForReconnect = (action === 'play' || action === 'stop') && response.stateChanged === true;

  if (shouldWaitForReconnect) {
    try {
      await mcpUnity.waitForReconnect(120000);
      message += ' Domain reload complete.';
    } catch (err) {
      logger.warn(`Timed out waiting for domain reload after '${action}': ${err instanceof Error ? err.message : String(err)}`);
      message += ' WARNING: Timed out waiting for domain reload reconnect.';
    }
  }

  return {
    content: [
      {
        type: 'text' as const,
        text: message
      },
      {
        type: 'text' as const,
        text: JSON.stringify(response.editorState)
      }
    ],
    data: {
      stateChanged: response.stateChanged,
      editorState: response.editorState
    }
  };
}
