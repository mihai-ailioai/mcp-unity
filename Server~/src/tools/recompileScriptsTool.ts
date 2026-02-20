import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'recompile_scripts';
const toolDescription = 'Recompiles all scripts in the Unity project. Waits for both compilation AND Unity domain reload to complete before returning, ensuring all new types are available. This may take a while on large projects.';
const paramsSchema = z.object({
  returnWithLogs: z.boolean().optional().default(true).describe('Whether to return compilation logs'),
  logsLimit: z.number().int().min(0).max(1000).optional().default(100).describe('Maximum number of compilation logs to return')
});

export function registerRecompileScriptsTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
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
  const returnWithLogs = params.returnWithLogs ?? true;
  const logsLimit = Math.max(0, Math.min(1000, params.logsLimit || 100));

  // Send compilation request to Unity
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      returnWithLogs,
      logsLimit
    }
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || `Failed to recompile scripts`
    );
  }

  // Check if compilation had errors — if so, domain reload won't happen
  const hasErrors = response.logs?.some?.((log: any) => log.type === 'Error');

  if (!hasErrors) {
    // Compilation succeeded — Unity will do a domain reload.
    // The WebSocket will disconnect and reconnect. Wait for that cycle
    // to complete so all new types are available before we return.
    logger.info('Compilation succeeded. Waiting for Unity domain reload (disconnect + reconnect)...');
    try {
      await mcpUnity.waitForReconnect(120000); // 120s timeout for large projects
      logger.info('Domain reload complete — all types are now available.');
      
      // Augment the response message
      const originalMessage = response.message || 'Recompilation completed';
      return {
        content: [
          {
            type: 'text' as const,
            text: originalMessage + ' Domain reload complete — all new types are available.'
          },
          {
            type: 'text' as const,
            text: JSON.stringify({ logs: response.logs }, null, 2)
          }
        ]
      };
    } catch (err) {
      // Timeout waiting for reconnect — domain reload may be very slow
      logger.warn(`Timed out waiting for domain reload: ${err instanceof Error ? err.message : String(err)}`);
      return {
        content: [
          {
            type: 'text' as const,
            text: (response.message || 'Recompilation completed') + 
                  ' WARNING: Timed out waiting for domain reload. Unity may still be loading — try again in a moment if types are not available.'
          },
          {
            type: 'text' as const,
            text: JSON.stringify({ logs: response.logs }, null, 2)
          }
        ]
      };
    }
  }

  // Compilation had errors — no domain reload, return immediately
  return {
    content: [
      {
        type: 'text' as const,
        text: response.message
      },
      {
        type: 'text' as const,
        text: JSON.stringify({ logs: response.logs }, null, 2)
      }
    ]
  };
}
