import * as z from 'zod';
import { Logger } from '../utils/logger.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { CallToolResult } from '@modelcontextprotocol/sdk/types.js';

const toolName = 'control_editor';
const toolDescription = `Controls Unity editor play mode state.

Actions:
- play: Enter play mode (triggers domain reload — waits for Unity to reconnect)
- pause: Pause play mode (only while playing)
- unpause: Resume from pause
- stop: Exit play mode (triggers domain reload — waits for Unity to reconnect)
- step: Advance one frame (only while playing)`;
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
  const triggersReload = action === 'play' || action === 'stop';

  if (triggersReload) {
    // play/stop trigger domain reload which kills the WebSocket BEFORE the response
    // can be sent back. Strategy: start listening for reconnect BEFORE sending the
    // request, then treat connection errors as expected.
    const reconnectPromise = mcpUnity.waitForReconnect(120000);

    // Send the request — it may fail with connection error (expected during domain reload)
    let response: any = null;
    try {
      response = await mcpUnity.sendRequest({
        method: toolName,
        params: { action }
      });
    } catch (err) {
      // Connection error is expected — domain reload killed the WebSocket
      logger.info(`Request lost during domain reload for '${action}' (expected): ${err instanceof Error ? err.message : String(err)}`);
    }

    // If we got a response and it indicates no state change (already in target state),
    // no domain reload will happen — return immediately
    if (response && response.stateChanged === false) {
      return {
        content: [
          { type: 'text' as const, text: response.message || 'No state change needed.' },
          { type: 'text' as const, text: JSON.stringify(response.editorState, null, 2) }
        ],
        data: { stateChanged: false, editorState: response.editorState }
      };
    }

    // Domain reload is happening — wait for reconnect
    logger.info(`Waiting for domain reload reconnect after '${action}'...`);
    try {
      await reconnectPromise;
      logger.info('Domain reload complete.');
      const message = response?.message
        ? `${response.message} Domain reload complete.`
        : `Editor ${action === 'play' ? 'entered play mode' : 'stopped'}. Domain reload complete.`;
      return {
        content: [
          { type: 'text' as const, text: message },
          { type: 'text' as const, text: JSON.stringify(response?.editorState ?? {}, null, 2) }
        ],
        data: { stateChanged: true, editorState: response?.editorState ?? {} }
      };
    } catch (err) {
      logger.warn(`Timed out waiting for domain reload after '${action}': ${err instanceof Error ? err.message : String(err)}`);
      return {
        content: [
          { type: 'text' as const, text: `Editor ${action} command sent, but timed out waiting for domain reload reconnect. Unity may still be loading.` }
        ],
        data: { stateChanged: true, editorState: {} }
      };
    }
  }

  // pause/unpause/step — no domain reload, simple request/response
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

  return {
    content: [
      { type: 'text' as const, text: response.message || 'Editor state updated.' },
      { type: 'text' as const, text: JSON.stringify(response.editorState, null, 2) }
    ],
    data: { stateChanged: response.stateChanged, editorState: response.editorState }
  };
}
