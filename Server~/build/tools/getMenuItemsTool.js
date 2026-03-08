import * as z from "zod";
import { McpUnityError, ErrorType } from "../utils/errors.js";
const toolName = "get_menu_items";
const toolDescription = "Returns all available Unity Editor menu items. " +
    "Use this to discover menu item paths before calling execute_menu_item.";
const paramsSchema = z.object({});
export function registerGetMenuItemsTool(server, mcpUnity, logger) {
    logger.info(`Registering tool: ${toolName}`);
    server.tool(toolName, toolDescription, paramsSchema.shape, async () => {
        try {
            logger.info(`Executing tool: ${toolName}`);
            const response = await mcpUnity.sendRequest({
                method: toolName,
                params: {},
            });
            if (!response.success) {
                throw new McpUnityError(ErrorType.TOOL_EXECUTION, response.message || "Failed to get menu items");
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
