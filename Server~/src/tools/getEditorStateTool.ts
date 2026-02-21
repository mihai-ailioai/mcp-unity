import * as z from 'zod';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
import { McpUnity } from '../unity/mcpUnity.js';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { Logger } from '../utils/logger.js';

const toolName = 'get_editor_state';
const toolDescription = 'Gets a snapshot of Unity editor state including play mode status, compilation status, active scene details, active build platform, and Unity version.';
const paramsSchema = z.object({});

export function registerGetEditorStateTool(server: McpServer, mcpUnity: McpUnity, logger: Logger) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, params);
        const result = await toolHandler(mcpUnity);
        logger.info(`Tool execution successful: ${toolName}`);
        return result;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}

async function toolHandler(mcpUnity: McpUnity) {
  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {}
  });

  if (!response.success) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || 'Failed to get editor state');
  }

  const editorState = response.editorState;
  const activeScene = editorState.activeScene;
  let text = `Play Mode: ${editorState.playModeState}\n`;
  text += `Is Playing: ${editorState.isPlaying}\n`;
  text += `Is Paused: ${editorState.isPaused}\n`;
  text += `Is Compiling: ${editorState.isCompiling}\n`;
  text += `Platform: ${editorState.platform}\n`;
  text += `Unity Version: ${editorState.unityVersion}\n`;
  text += `Active Scene: ${activeScene.name || '(untitled)'}\n`;
  text += `Scene Path: ${activeScene.path || '(unsaved)'}\n`;
  text += `Scene Dirty: ${activeScene.isDirty}\n`;
  text += `Scene Build Index: ${activeScene.buildIndex}`;

  return {
    content: [
      {
        type: 'text' as const,
        text
      }
    ],
    data: {
      editorState: response.editorState
    }
  };
}
