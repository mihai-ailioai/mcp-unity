import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "setup_animation_clip";
const toolDescription = `Creates or updates an AnimationClip asset and applies clip settings, curves, and animation events.

Path normalization is automatic: if assetPath does not start with Assets/ it is added, and if .anim is missing it is appended.

Examples:
- UI fade-in:
  setup_animation_clip({
    assetPath: "Animations/UI/FadeIn",
    frameRate: 60,
    curves: [
      {
        propertyPath: "m_Color.a",
        type: "CanvasGroup",
        relativePath: "Panel",
        keys: [
          { time: 0, value: 0 },
          { time: 0.35, value: 1 }
        ]
      }
    ]
  })

- Looping scale pulse:
  setup_animation_clip({
    assetPath: "Assets/Animations/UI/Pulse.anim",
    frameRate: 60,
    loop: true,
    curves: [
      {
        propertyPath: "m_LocalScale.x",
        type: "Transform",
        keys: [
          { time: 0, value: 1 },
          { time: 0.2, value: 1.08 },
          { time: 0.4, value: 1 }
        ]
      },
      {
        propertyPath: "m_LocalScale.y",
        type: "Transform",
        keys: [
          { time: 0, value: 1 },
          { time: 0.2, value: 1.08 },
          { time: 0.4, value: 1 }
        ]
      }
    ]
  })`;

const keySchema = z.object({
  time: z.number(),
  value: z.number(),
  inTangent: z.number().optional(),
  outTangent: z.number().optional(),
});

const curveSchema = z.object({
  propertyPath: z.string().min(1),
  type: z.string().optional().default("Transform"),
  relativePath: z.string().optional().default(""),
  keys: z.array(keySchema).min(1),
});

const eventSchema = z.object({
  functionName: z.string().min(1),
  time: z.number(),
  stringParameter: z.string().optional(),
  floatParameter: z.number().optional(),
  intParameter: z.number().int().optional(),
});

const removeCurveSchema = z.object({
  propertyPath: z.string().min(1),
  type: z.string().optional(),
  relativePath: z.string().optional().default(""),
});

const removeEventSchema = z.object({
  functionName: z.string().min(1),
  time: z.number().optional(),
});

const paramsSchema = z.object({
  assetPath: z.string().min(1),
  frameRate: z.number().optional(),
  loop: z.boolean().optional(),
  curves: z.array(curveSchema).optional(),
  events: z.array(eventSchema).optional(),
  removeCurves: z.array(removeCurveSchema).optional(),
  removeEvents: z.array(removeEventSchema).optional(),
});

export function registerSetupAnimationClipTool(
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
  if (params.frameRate !== undefined && params.frameRate <= 0) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "frameRate must be greater than 0 when provided"
    );
  }

  const response = await mcpUnity.sendRequest({
    method: toolName,
    params,
  });

  if (!response.success) {
    throw new McpUnityError(
      ErrorType.TOOL_EXECUTION,
      response.message || "Failed to setup animation clip"
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
