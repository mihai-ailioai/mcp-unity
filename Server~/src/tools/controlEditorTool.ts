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
    // can be sent back. The C# side tries to Send() the response but the socket is
    // already closing, causing "An error has occurred in sending data" on Unity side.
    //
    // Strategy:
    // 1. Start waitForReconnect FIRST (so it's listening when disconnect happens)
    // 2. Fire sendRequest but DON'T await it — let it settle in background
    // 3. Wait for reconnect (the only reliable signal that Unity is back)
    //
    // For no-op cases (already playing/stopped), sendRequest returns fast with
    // stateChanged=false. We race it against a short delay to detect this.

    // Start reconnect listener BEFORE sending (catches disconnect immediately)
    const reconnectPromise = mcpUnity.waitForReconnect(120000);

    // Fire request — don't await. It will either:
    // a) Resolve quickly (no-op case, stateChanged=false)
    // b) Fail with connection error (domain reload killed the socket)
    // c) Timeout after ~10s and trigger forceReconnect (undesirable but harmless
    //    since waitForReconnect is already listening)
    let noOpResponse: any = null;
    const requestPromise = mcpUnity.sendRequest({
      method: toolName,
      params: { action }
    }).then(response => {
      if (response?.stateChanged === false) {
        noOpResponse = response;
      }
    }).catch(err => {
      // Expected — domain reload killed the connection
      logger.info(`Request lost during domain reload for '${action}' (expected): ${err instanceof Error ? err.message : String(err)}`);
    });

    // Give the no-op case a short window to resolve (500ms is plenty for a
    // synchronous Unity response on localhost)
    await Promise.race([
      requestPromise,
      new Promise(resolve => setTimeout(resolve, 500))
    ]);

    // If we got a no-op response, return immediately (no domain reload)
    if (noOpResponse) {
      return {
        content: [
          { type: 'text' as const, text: noOpResponse.message || 'No state change needed.' },
          { type: 'text' as const, text: JSON.stringify(noOpResponse.editorState, null, 2) }
        ],
        data: { stateChanged: false, editorState: noOpResponse.editorState }
      };
    }

    // Domain reload is happening — wait for reconnect
    logger.info(`Waiting for domain reload reconnect after '${action}'...`);
    try {
      await reconnectPromise;
      logger.info('Domain reload complete.');
      const message = `Editor ${action === 'play' ? 'entered play mode' : 'stopped'}. Domain reload complete.`;
      return {
        content: [{ type: 'text' as const, text: message }],
        data: { stateChanged: true, editorState: {} }
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
