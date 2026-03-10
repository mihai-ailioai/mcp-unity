import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "get_prefab_info";
const toolDescription =
  "Get detailed information about a prefab asset by asset path, without entering Prefab Mode or instantiating in scene. " +
  "Returns hierarchy, components, and prefab metadata (variant status, base prefab path). " +
  "Use summary=true for a lightweight listing with name, instanceId, and component type names per child (no deduplication, no property details). " +
  "Without summary (default), returns full component property serialization. " +
  "For large prefabs, use 'rootPath' to inspect a specific subtree, or 'namePattern'/'componentType' to " +
  "search for matching GameObjects (returns a flat list of matches instead of the full hierarchy). " +
  "IMPORTANT: instanceIds from this tool are NOT valid inside modify_prefab (different object graph). " +
  "Use objectPath for modify_prefab operations.";

const paramsSchema = z.object({
  assetPath: z
    .string()
    .describe(
      "The asset path of the prefab (e.g., 'Assets/Prefabs/Player.prefab'). Must start with 'Assets/' and end with '.prefab'."
    ),
  summary: z
    .boolean()
    .optional()
    .describe(
      "When true, returns a lightweight response with only names, instanceIds, and component type names (no property details). Useful for scanning large prefab hierarchies."
    ),
  rootPath: z
    .string()
    .optional()
    .describe(
      "Path within the prefab hierarchy to use as the starting point (e.g., 'CarBody/Exterior'). Only the subtree at this path is included in the response."
    ),
  namePattern: z
    .string()
    .optional()
    .describe(
      "Filter GameObjects by name using wildcard patterns (e.g., '*Livery*', 'Wheel*'). " +
      "When set, returns a flat list of matching GameObjects instead of the full hierarchy tree. Case-insensitive."
    ),
  componentType: z
    .string()
    .optional()
    .describe(
      "Filter GameObjects that have this component type (e.g., 'MeshRenderer', 'BoxCollider'). " +
      "When set, returns a flat list of matching GameObjects instead of the full hierarchy tree."
    ),
});

/**
 * Creates and registers the Get Prefab Info tool with the MCP server
 */
export function registerGetPrefabInfoTool(
  server: McpServer,
  mcpUnity: McpUnity,
  logger: Logger
) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>) => {
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

async function toolHandler(
  mcpUnity: McpUnity,
  params: z.infer<typeof paramsSchema>
): Promise<CallToolResult> {
  const { assetPath, summary, rootPath, namePattern, componentType } = params;

  // Validate assetPath
  if (!assetPath || assetPath.trim().length === 0) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "assetPath is required and cannot be empty"
    );
  }

  if (!assetPath.startsWith("Assets/")) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "assetPath must start with 'Assets/'"
    );
  }

  if (!assetPath.endsWith(".prefab")) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "assetPath must end with '.prefab'"
    );
  }

  const requestParams: Record<string, any> = {
    assetPath: assetPath,
    summary: summary ?? false,
  };

  if (rootPath) requestParams.rootPath = rootPath;
  if (namePattern) requestParams.namePattern = namePattern;
  if (componentType) requestParams.componentType = componentType;

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: requestParams,
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || "Failed to get prefab info from Unity"
    );
  }

  return {
    content: [
      {
        type: "text",
        text: JSON.stringify(response, null, 2),
      },
    ],
  };
}
