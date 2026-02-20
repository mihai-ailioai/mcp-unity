import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "get_animator_info";
const toolDescription =
  "Inspects an Animator on a GameObject and returns controller, parameter, layer, state, and transition details. " +
  "Use this to gather enough context to make informed setup_animation_clip and setup_animator_controller calls.";

const paramsSchema = z.object({
  instanceId: z
    .number()
    .int()
    .optional()
    .describe("Optional Unity instance ID of the target GameObject."),
  objectPath: z
    .string()
    .optional()
    .describe("Optional hierarchy path of the target GameObject (e.g. '/Root/Character')."),
  includeClipDetails: z
    .boolean()
    .optional()
    .describe(
      "When true, includes clip metadata (length, frameRate, loop, curve bindings, and events) for clip-backed states and BlendTree children."
    ),
});

export function registerGetAnimatorInfoTool(
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
  rawParams: z.infer<typeof paramsSchema>
): Promise<CallToolResult> {
  const parsedParams = paramsSchema.safeParse(rawParams);
  if (!parsedParams.success) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      `Invalid parameters for ${toolName}`,
      parsedParams.error.flatten()
    );
  }

  const params = parsedParams.data;
  const hasInstanceId = typeof params.instanceId === "number";
  const hasObjectPath = typeof params.objectPath === "string" && params.objectPath.trim().length > 0;

  if (!hasInstanceId && !hasObjectPath) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "Either instanceId or objectPath must be provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params: {
      instanceId: params.instanceId,
      objectPath: hasObjectPath ? params.objectPath : undefined,
      includeClipDetails: params.includeClipDetails ?? false,
    },
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || "Failed to get animator info"
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
