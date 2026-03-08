import { McpUnity } from '../unity/mcpUnity.js';
import { Logger } from '../utils/logger.js';
import { McpServer } from '@modelcontextprotocol/sdk/server/mcp.js';
/**
 * Registers the Selection resource with the MCP server.
 * Returns the current Unity Editor selection: active GameObject,
 * all selected GameObjects, and selected assets in the Project window.
 */
export declare function registerGetSelectionResource(server: McpServer, mcpUnity: McpUnity, logger: Logger): void;
