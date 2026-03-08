import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
/**
 * Creates and registers the Get Prefab Info tool with the MCP server
 */
export declare function registerGetPrefabInfoTool(server: McpServer, mcpUnity: McpUnity, logger: Logger): void;
