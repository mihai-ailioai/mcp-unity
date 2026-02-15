import { jest, describe, it, expect, beforeEach } from '@jest/globals';
import { McpUnityError, ErrorType } from '../utils/errors.js';
import { registerGetImportSettingsTool } from '../tools/getImportSettingsTool.js';
import { registerUpdateImportSettingsTool } from '../tools/updateImportSettingsTool.js';

const mockSendRequest = jest.fn();
const mockMcpUnity = {
  sendRequest: mockSendRequest
};

const mockLogger = {
  info: jest.fn(),
  debug: jest.fn(),
  warn: jest.fn(),
  error: jest.fn()
};

const mockServerTool = jest.fn();
const mockServer = {
  tool: mockServerTool
};

describe('Import Settings Tools', () => {
  beforeEach(() => {
    jest.clearAllMocks();
  });

  describe('registerGetImportSettingsTool', () => {
    it('registers get_import_settings with the server', () => {
      registerGetImportSettingsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);

      expect(mockServerTool).toHaveBeenCalledTimes(1);
      expect(mockServerTool).toHaveBeenCalledWith(
        'get_import_settings',
        expect.any(String),
        expect.any(Object),
        expect.any(Function)
      );
      expect(mockLogger.info).toHaveBeenCalledWith('Registering tool: get_import_settings');
    });
  });

  describe('get_import_settings handler', () => {
    let handler: Function;

    beforeEach(() => {
      registerGetImportSettingsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
      handler = mockServerTool.mock.calls[0][3];
    });

    it('throws validation error when neither assetPath nor guid are provided', async () => {
      await expect(handler({})).rejects.toThrow(McpUnityError);
      await expect(handler({})).rejects.toMatchObject({
        type: ErrorType.VALIDATION,
        message: expect.stringContaining("Either 'assetPath' or 'guid' must be provided")
      });
    });

    it('treats whitespace-only identifiers as missing', async () => {
      await expect(handler({ assetPath: '   ', guid: '\t' })).rejects.toThrow(McpUnityError);
    });

    it('forwards request to Unity and includes structured fields in text', async () => {
      mockSendRequest.mockResolvedValue({
        success: true,
        type: 'text',
        message: 'Import settings read',
        data: {
          assetPath: 'Assets/Textures/hero.png',
          guid: 'guid123',
          importerType: 'TextureImporter',
          settings: {
            isReadable: true
          },
          platformOverrides: {
            Android: {
              maxTextureSize: 512,
              format: 'ASTC_6x6'
            }
          }
        }
      });

      const result = await handler({ assetPath: 'Assets/Textures/hero.png' });

      expect(mockSendRequest).toHaveBeenCalledWith({
        method: 'get_import_settings',
        params: {
          assetPath: 'Assets/Textures/hero.png',
          guid: undefined
        }
      });
      expect(result.content[0].text).toContain('Importer type: TextureImporter');
      expect(result.content[0].text).toContain('Settings:');
      expect(result.content[0].text).toContain('Platform overrides:');
      expect(result.data).toBeDefined();
      expect(result.data.importerType).toBe('TextureImporter');
    });
  });

  describe('registerUpdateImportSettingsTool', () => {
    it('registers update_import_settings with the server', () => {
      registerUpdateImportSettingsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);

      expect(mockServerTool).toHaveBeenCalledTimes(1);
      expect(mockServerTool).toHaveBeenCalledWith(
        'update_import_settings',
        expect.any(String),
        expect.any(Object),
        expect.any(Function)
      );
      expect(mockLogger.info).toHaveBeenCalledWith('Registering tool: update_import_settings');
    });
  });

  describe('update_import_settings handler', () => {
    let handler: Function;

    beforeEach(() => {
      registerUpdateImportSettingsTool(mockServer as any, mockMcpUnity as any, mockLogger as any);
      handler = mockServerTool.mock.calls[0][3];
    });

    it('throws validation error when neither assetPath nor guid are provided', async () => {
      await expect(handler({ settings: { isReadable: true } })).rejects.toThrow(McpUnityError);
      await expect(handler({ settings: { isReadable: true } })).rejects.toMatchObject({
        type: ErrorType.VALIDATION,
        message: expect.stringContaining("Either 'assetPath' or 'guid' must be provided")
      });
    });

    it('throws validation error when both settings and platformOverrides are missing', async () => {
      await expect(handler({ assetPath: 'Assets/Textures/hero.png' })).rejects.toThrow(McpUnityError);
      await expect(handler({ assetPath: 'Assets/Textures/hero.png' })).rejects.toMatchObject({
        type: ErrorType.VALIDATION,
        message: expect.stringContaining("At least one of 'settings' or 'platformOverrides'")
      });
    });

    it('forwards all params to Unity and includes updated/errors in text', async () => {
      mockSendRequest.mockResolvedValue({
        success: true,
        type: 'text',
        message: 'Updated import settings',
        data: {
          assetPath: 'Assets/Textures/hero.png',
          guid: 'guid123',
          importerType: 'TextureImporter',
          updatedProperties: ['isReadable', 'platformOverrides.Android'],
          errors: ['Error setting filterMode']
        }
      });

      const params = {
        guid: 'guid123',
        settings: {
          isReadable: true,
          filterMode: 'Bilinear'
        },
        platformOverrides: {
          Android: {
            maxTextureSize: 512,
            format: 'ASTC_6x6'
          }
        }
      };

      const result = await handler(params);

      expect(mockSendRequest).toHaveBeenCalledWith({
        method: 'update_import_settings',
        params
      });
      expect(result.content[0].text).toContain('Importer type: TextureImporter');
      expect(result.content[0].text).toContain('Updated:');
      expect(result.content[0].text).toContain('Errors:');
      expect(result.data).toBeDefined();
      expect(result.data.updatedProperties).toEqual(['isReadable', 'platformOverrides.Android']);
    });
  });
});
