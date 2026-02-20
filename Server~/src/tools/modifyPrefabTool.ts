import * as z from "zod";
import { Logger } from "../utils/logger.js";
import { McpUnity } from "../unity/mcpUnity.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { McpUnityError, ErrorType } from "../utils/errors.js";
import { CallToolResult } from "@modelcontextprotocol/sdk/types.js";

const toolName = "modify_prefab";
const toolDescription = `Modify a prefab asset headlessly by executing batched operations against its contents.
Uses an isolated editing context — no Prefab Mode or scene instantiation needed.
All objectPath references in operations resolve against the prefab hierarchy.
Changes are saved automatically if any operation succeeds.
Set 'variantPath' to create a prefab variant from assetPath before applying operations.
When creating a variant, 'operations' is optional (variant is created even with no operations).
Example modify: modify_prefab({assetPath: "Assets/Prefabs/Enemy.prefab", operations: [
  {tool: "update_component", params: {objectPath: "Enemy", componentType: "EnemyController", componentData: {health: 200}}}
]})
Example variant: modify_prefab({assetPath: "Assets/Prefabs/Enemy.prefab", variantPath: "Assets/Prefabs/EnemyBoss.prefab", operations: [
  {tool: "update_component", params: {objectPath: "EnemyBoss", componentType: "EnemyController", componentData: {health: 500}}}
]})`;

const operationSchema = z.object({
  tool: z
    .string()
    .describe("The name of the tool to execute against the prefab contents"),
  params: z
    .record(z.any())
    .optional()
    .default({})
    .describe("Parameters to pass to the tool"),
  id: z
    .string()
    .optional()
    .describe(
      "Optional identifier for this operation (for tracking in results)"
    ),
});

const paramsSchema = z.object({
  assetPath: z
    .string()
    .describe(
      "The asset path of the prefab to modify (e.g., 'Assets/Prefabs/Player.prefab'). Must start with 'Assets/' and end with '.prefab'."
    ),
  variantPath: z
    .string()
    .optional()
    .describe(
      "If provided, creates a prefab variant from assetPath at this path before applying operations. Must start with 'Assets/' and end with '.prefab'. When set, 'operations' becomes optional."
    ),
  operations: z
    .array(operationSchema)
    .max(100, "Maximum of 100 operations allowed")
    .optional()
    .describe(
      "Array of operations to execute against the prefab contents. Same format as batch_execute operations. Required unless 'variantPath' is specified."
    ),
  stopOnError: z
    .boolean()
    .optional()
    .default(true)
    .describe(
      "If true, stops execution on the first error. Default: true"
    ),
});

interface OperationResult {
  index: number;
  id: string;
  success: boolean;
  result?: any;
  error?: string;
}

interface ModifyPrefabResponse {
  success: boolean;
  message: string;
  assetPath: string;
  results: OperationResult[];
  summary: {
    total: number;
    succeeded: number;
    failed: number;
    executed: number;
  };
}

/**
 * Creates and registers the Modify Prefab tool with the MCP server
 */
export function registerModifyPrefabTool(
  server: McpServer,
  mcpUnity: McpUnity,
  logger: Logger
) {
  logger.info(`Registering tool: ${toolName}`);

  server.tool(
    toolName,
    toolDescription,
    paramsSchema.shape,
    async (params: any) => {
      try {
        logger.info(`Executing tool: ${toolName}`, {
          assetPath: params.assetPath,
          operationCount: params.operations?.length,
          stopOnError: params.stopOnError,
        });
        const result = await toolHandler(mcpUnity, params, logger);
        logger.info(`Tool execution completed: ${toolName}`);
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
  params: z.infer<typeof paramsSchema>,
  logger: Logger
): Promise<CallToolResult> {
  const { assetPath, variantPath, operations, stopOnError } = params;

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

  // Validate variantPath if provided
  if (variantPath) {
    if (!variantPath.startsWith("Assets/")) {
      throw new McpUnityError(
        ErrorType.VALIDATION,
        "variantPath must start with 'Assets/'"
      );
    }
    if (!variantPath.endsWith(".prefab")) {
      throw new McpUnityError(
        ErrorType.VALIDATION,
        "variantPath must end with '.prefab'"
      );
    }
  }

  // Validate operations — required unless creating a variant
  if (!variantPath && (!operations || operations.length === 0)) {
    throw new McpUnityError(
      ErrorType.VALIDATION,
      "The 'operations' array is required and must contain at least one operation (unless 'variantPath' is specified)"
    );
  }

  // Validate no blocked tools in operations
  const blockedTools = new Set([
    "batch_execute", "modify_prefab",
    "create_scene", "load_scene", "save_scene", "delete_scene", "unload_scene",
    "add_asset_to_scene", "add_package", "run_tests", "recompile_scripts",
    "execute_menu_item", "save_as_prefab", "create_prefab",
    "move_asset", "rename_asset", "copy_asset", "delete_asset",
  ]);
  if (operations) {
    for (const op of operations) {
      if (blockedTools.has(op.tool)) {
        throw new McpUnityError(
          ErrorType.VALIDATION,
          `Tool '${op.tool}' is not allowed inside modify_prefab (it affects scenes or project state outside the prefab editing context)`
        );
      }
      if (!op.tool || op.tool.trim().length === 0) {
        throw new McpUnityError(
          ErrorType.VALIDATION,
          "Each operation must have a non-empty 'tool' name"
        );
      }
    }
  }

  logger.info(
    `Sending modify_prefab with ${operations?.length ?? 0} operations for ${assetPath}${variantPath ? ` (variant: ${variantPath})` : ""} to Unity`
  );

  const requestParams: any = {
    assetPath: assetPath,
    stopOnError: stopOnError ?? true,
  };

  if (variantPath) {
    requestParams.variantPath = variantPath;
  }

  if (operations && operations.length > 0) {
    requestParams.operations = operations.map((op, index) => ({
      tool: op.tool,
      params: op.params ?? {},
      id: op.id ?? index.toString(),
    }));
  }

  const response = (await mcpUnity.sendRequest({
    method: toolName,
    params: requestParams,
  })) as ModifyPrefabResponse;

  // Format response message
  let resultText = response.message || "Prefab modification completed";

  if (response.summary) {
    resultText += `\n\nSummary: ${response.summary.succeeded}/${response.summary.total} succeeded`;
    if (response.summary.failed > 0) {
      resultText += `, ${response.summary.failed} failed`;
    }
  }

  // Add failure details
  if (response.results && response.results.length > 0) {
    const failures = response.results.filter((r) => !r.success);
    if (failures.length > 0) {
      resultText += "\n\nFailed operations:";
      for (const failure of failures) {
        resultText += `\n  - [${failure.id}] ${failure.error || "Unknown error"}`;
      }
    }
  }

  if (!response.success && stopOnError) {
    throw new McpUnityError(ErrorType.TOOL_EXECUTION, resultText);
  }

  return {
    content: [
      {
        type: "text",
        text: resultText,
      },
    ],
  };
}
