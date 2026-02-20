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
  "Use summary=true for a lightweight overview (names, instanceIds, component type names only).";

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
  const { assetPath, summary } = params;

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

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      assetPath: assetPath,
      summary: summary ?? false,
    },
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
