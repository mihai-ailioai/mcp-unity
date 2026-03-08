import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
/**
 * Creates and registers the Modify Prefab tool with the MCP server
 */
export declare function registerModifyPrefabTool(server: McpServer, mcpUnity: McpUnity, logger: Logger): void;
