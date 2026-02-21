import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "get_selection";
const toolDescription =
  "Returns the current Unity Editor selection: active GameObject, all selected GameObjects in the scene, " +
  "and selected assets in the Project window. Use this to see what the user is looking at or has selected.";

const paramsSchema = z.object({});

export function registerGetSelectionTool(
  server: McpServer,
  mcpUnity: McpUnity,
  logger: Logger
): void {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: z.infer<typeof paramsSchema>) => {
      try {
        logger.info(`Executing tool: ${toolName}`);
        const response = await mcpUnity.sendRequest({
          method: toolName,
          params: {},
        });

        if (!response.success) {
          throw new McpUnityError(
            ErrorType.TOOL_EXECUTION,
            response.message || "Failed to get selection"
          );
        }

        return {
          content: [
            {
              type: "text" as const,
              text: JSON.stringify(response, null, 2),
            },
          ],
        } satisfies CallToolResult;
      } catch (error) {
        logger.error(`Tool execution failed: ${toolName}`, error);
        throw error;
      }
    }
  );
}
