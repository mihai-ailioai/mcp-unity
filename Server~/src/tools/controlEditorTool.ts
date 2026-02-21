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
    // 1. Fire sendRequest but DON'T await — let it settle in background
    // 2. For no-op cases, the response comes back fast (race against 500ms)
    // 3. For actual state changes, poll until connection is back and responsive

    // Fire request — don't await
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

    // Give the no-op case a short window to resolve
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

    // Domain reload is happening — poll until Unity is back and responsive.
    // We avoid waitForReconnect because the disconnect→reconnect cycle timing
    // is unpredictable (stop exits play mode faster than play enters it, and
    // the state change events may complete before the listener is registered).
    logger.info(`Waiting for Unity to come back after '${action}'...`);
    const pollStart = Date.now();
    const pollTimeout = 120000;
    const pollInterval = 500;

    while (Date.now() - pollStart < pollTimeout) {
      await new Promise(resolve => setTimeout(resolve, pollInterval));

      // Try a lightweight request to see if Unity is back
      try {
        const probe = await mcpUnity.sendRequest({
          method: 'get_editor_state',
          params: {}
        });
        if (probe?.success) {
          logger.info('Domain reload complete — Unity is responsive.');
          const message = `Editor ${action === 'play' ? 'entered play mode' : 'stopped'}. Domain reload complete.`;
          return {
            content: [{ type: 'text' as const, text: message }],
            data: { stateChanged: true, editorState: probe.editorState ?? {} }
          };
        }
      } catch {
        // Not ready yet — keep polling
      }
    }

    // Timed out
    logger.warn(`Timed out waiting for Unity after '${action}' (${pollTimeout}ms)`);
    return {
      content: [
        { type: 'text' as const, text: `Editor ${action} command sent, but timed out waiting for Unity to come back. Unity may still be loading.` }
      ],
      data: { stateChanged: true, editorState: {} }
    };
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
