import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "setup_animator_controller";
const toolDescription = `Creates or updates an AnimatorController asset, with support for parameters, layers, states, and transitions.

Path normalization is automatic: if assetPath does not start with Assets/ it is added, and if .controller is missing it is appended.

Example (2-state UI animation controller):
- setup_animator_controller({
    assetPath: "Animations/UI/ButtonStates",
    parameters: [
      { name: "isHighlighted", type: "bool", defaultValue: false },
      { name: "pressed", type: "trigger" }
    ],
    states: [
      { name: "Idle", clipPath: "Animations/UI/ButtonIdle", isDefault: true },
      { name: "Highlighted", clipPath: "Animations/UI/ButtonHighlighted" }
    ],
    transitions: [
      {
        fromState: "Idle",
        toState: "Highlighted",
        duration: 0.1,
        conditions: [{ parameter: "isHighlighted", mode: "if" }]
      },
      {
        fromState: "Highlighted",
        toState: "Idle",
        duration: 0.1,
        conditions: [{ parameter: "isHighlighted", mode: "ifNot" }]
      }
    ]
  })`;

const conditionModeSchema = z.enum([
  "greater",
  "less",
  "equals",
  "notEquals",
  "if",
  "ifNot",
]);

const parameterTypeSchema = z.enum(["float", "int", "bool", "trigger"]);
const layerBlendingModeSchema = z.enum(["override", "additive"]);

const parameterSchema = z.object({
  name: z.string().min(1),
  type: parameterTypeSchema.optional().default("float"),
  defaultValue: z.union([z.number(), z.boolean()]).optional(),
});

const layerSchema = z.object({
  name: z.string().min(1),
  weight: z.number().optional().default(1),
  blendingMode: layerBlendingModeSchema.optional().default("override"),
});

const stateSchema = z.object({
  name: z.string().min(1),
  clipPath: z.string().optional(),
  speed: z.number().optional().default(1),
  isDefault: z.boolean().optional(),
  layerIndex: z.number().int().nonnegative().optional().default(0),
});

const transitionConditionSchema = z.object({
  parameter: z.string().min(1),
  mode: conditionModeSchema,
  threshold: z.number().optional(),
});

const transitionSchema = z.object({
  fromState: z.string().min(1),
  toState: z.string().min(1),
  hasExitTime: z.boolean().optional(),
  exitTime: z.number().optional(),
  duration: z.number().optional(),
  layerIndex: z.number().int().nonnegative().optional().default(0),
  conditions: z.array(transitionConditionSchema).optional(),
});

const removeStateSchema = z.object({
  name: z.string().min(1),
  layerIndex: z.number().int().nonnegative().optional().default(0),
});

const removeTransitionSchema = z.object({
  fromState: z.string().min(1),
  toState: z.string().min(1),
  layerIndex: z.number().int().nonnegative().optional().default(0),
});

const removeParameterSchema = z.object({
  name: z.string().min(1),
});

const removeLayerSchema = z.object({
  name: z.string().min(1),
});

const paramsSchema = z.object({
  assetPath: z.string().min(1),
  parameters: z.array(parameterSchema).optional(),
  layers: z.array(layerSchema).optional(),
  states: z.array(stateSchema).optional(),
  transitions: z.array(transitionSchema).optional(),
  removeStates: z.array(removeStateSchema).optional(),
  removeTransitions: z.array(removeTransitionSchema).optional(),
  removeParameters: z.array(removeParameterSchema).optional(),
  removeLayers: z.array(removeLayerSchema).optional(),
});

export function registerSetupAnimatorControllerTool(
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
  if (!params.assetPath.trim()) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "assetPath must be a non-empty string"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params,
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || "Failed to setup animator controller"
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
