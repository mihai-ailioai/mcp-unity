import { jest, describe, it, expect, beforeEach } from '@jest/globals';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { registerFindGameObjectsTool } from '../tools/findGameObjectsTool.js';

const mockSendRequest = jest.fn();
const mockMcpUnity = {
  sendRequest: mockSendRequest,
};

const mockLogger = {
  info: jest.fn(),
  debug: jest.fn(),
  warn: jest.fn(),
  error: jest.fn(),
};

const mockServerTool = jest.fn();
const mockServer = {
  tool: mockServerTool,
};

describe('Find GameObjects Tool', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  it('registers find_gameobjects with the server', () => {
    registerFindGameObjectsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);

    expect(mockServerTool).toHaveBeenCalledTimes(1);
    expect(mockServerTool).toHaveBeenCalledWith(
      'find_gameobjects',
      expect.any(String),
      expect.any(Object),
      expect.any(Function)
    );
    expect(mockLogger.info).toHaveBeenCalledWith('Registering tool: find_gameobjects');
  });

  it('throws validation error when no filters are provided', async () => {
    registerFindGameObjectsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
    const handler = mockServerTool.mock.calls[0][3];

    await expect(handler({})).rejects.toThrow(McpUnityError);
    await expect(handler({})).rejects.toMatchObject({
      type: ErrorType.VALIDATION,
      message: expect.stringContaining('At least one filter must be provided'),
    });
  });

  it('forwards params to Unity and applies default includeInactive=false', async () => {
    registerFindGameObjectsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
    const handler = mockServerTool.mock.calls[0][3];

    mockSendRequest.mockResolvedValue({
      success: true,
      type: 'text',
      message: 'Found 1 GameObject(s)',
      matches: [],
      totalFound: 1,
    });

    const result = await handler({ componentType: 'Animator' });

    expect(mockSendRequest).toHaveBeenCalledWith({
      method: 'find_gameobjects',
      params: {
        componentType: 'Animator',
        namePattern: undefined,
        tag: undefined,
        layer: undefined,
        includeInactive: false,
        rootPath: undefined,
      },
    });
    expect(result.content[0].text).toContain('Found 1 GameObject(s)');
  });

  it('throws tool execution error when Unity returns failure', async () => {
    registerFindGameObjectsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
    const handler = mockServerTool.mock.calls[0][3];

    mockSendRequest.mockResolvedValue({
      success: false,
      message: 'Layer not found',
    });

    await expect(handler({ layer: 'NotRealLayer' })).rejects.toMatchObject({
      type: ErrorType.TOOL_EXECUTION,
      message: 'Layer not found',
    });
  });
});
