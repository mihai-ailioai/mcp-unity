import * as z from "zod";
import { McpUnityError, ErrorType } from "../utils/errors.js";
const toolName = "get_selection";
const toolDescription = "Returns the current Unity Editor selection: active GameObject, all selected GameObjects in the scene, " +
    "and selected assets in the Project window. Use this to see what the user is looking at or has selected.";
const paramsSchema = z.object({});
export function registerGetSelectionTool(server, mcpUnity, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async (params) => {
        try {
            logger.info(`Executing tool: ${toolName}`);
            const response = await mcpUnity.sendRequest({
                method: toolName,
                params: {},
            });
            if (!response.success) {
                throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || "Failed to get selection");
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
        catch (error) {
            logger.error(`Tool execution failed: ${toolName}`, error);
            throw error;
        }
    });
}
