using System;
using System.IO;
using System.Reflection;
using McpUnity.Utils;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine;

namespace McpUnity.Tests
{
    public class ClaudeCodeConfigTests
    {
        private string _tempConfigPath;

        [SetUp]
        public void SetUp()
        {
            _tempConfigPath = Path.Combine(Path.GetTempPath(), $"ClaudeCodeConfigTests-{Guid.NewGuid():N}.json");
        }

        [TearDown]
        public void TearDown()
        {
            if (!string.IsNullOrEmpty(_tempConfigPath) && File.Exists(_tempConfigPath))
            {
                File.Delete(_tempConfigPath);
            }
        }

        private static MethodInfo GetMcpServersConfigMethod()
        {
            MethodInfo method = typeof(McpUtils).GetMethod(
                "GetMcpServersConfig",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            Assert.IsNotNull(method, "Expected to find private static McpUtils.GetMcpServersConfig method.");
            return method;
        }

        private static JObject InvokeGetMcpServersConfig(JObject existingConfig, string productName)
        {
            try
            {
                return (JObject)GetMcpServersConfigMethod().Invoke(null, new object[] { existingConfig, productName });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        private static MethodInfo GetTryMergeMcpServersMethod()
        {
            MethodInfo method = typeof(McpUtils).GetMethod(
                "TryMergeMcpServers",
                BindingFlags.NonPublic | BindingFlags.Static
            );

            Assert.IsNotNull(method, "Expected to find private static McpUtils.TryMergeMcpServers method.");
            return method;
        }

        private static bool InvokeTryMergeMcpServers(string configFilePath, JObject mcpConfig, string productName)
        {
            try
            {
                return (bool)GetTryMergeMcpServersMethod().Invoke(null, new object[] { configFilePath, mcpConfig, productName });
            }
            catch (TargetInvocationException ex) when (ex.InnerException != null)
            {
                throw ex.InnerException;
            }
        }

        [Test]
        public void GetMcpServersConfig_ClaudeCode_UsesUnityProjectRootKey()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            var expectedProjectConfig = new JObject();
            var existingConfig = new JObject
            {
                ["projects"] = new JObject
                {
                    [projectRoot] = expectedProjectConfig
                }
            };

            JObject result = InvokeGetMcpServersConfig(existingConfig, "Claude Code");

            Assert.AreSame(expectedProjectConfig, result);
        }

        [Test]
        public void GetMcpServersConfig_ClaudeCode_CreatesMissingProjectEntry()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            var existingConfig = new JObject();

            JObject result = InvokeGetMcpServersConfig(existingConfig, "Claude Code");

            Assert.IsNotNull(result);
            Assert.IsNotNull(existingConfig["projects"]);
            Assert.IsInstanceOf<JObject>(existingConfig["projects"]);
            Assert.AreSame(result, existingConfig["projects"][projectRoot]);
        }

        [Test]
        public void TryMergeMcpServers_ClaudeCode_DisablesAugmentForProject()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            File.WriteAllText(
                _tempConfigPath,
                new JObject
                {
                    ["projects"] = new JObject
                    {
                        [projectRoot] = new JObject
                        {
                            ["mcpServers"] = new JObject()
                        }
                    }
                }.ToString()
            );

            bool merged = InvokeTryMergeMcpServers(_tempConfigPath, JObject.Parse(McpUtils.GenerateMcpConfigJson()), "Claude Code");
            JObject mergedConfig = JObject.Parse(File.ReadAllText(_tempConfigPath));
            JArray disabledMcpServers = (JArray)mergedConfig["projects"][projectRoot]["disabledMcpServers"];

            Assert.IsTrue(merged);
            Assert.IsNotNull(disabledMcpServers);
            CollectionAssert.Contains(disabledMcpServers, "augment");
        }

        [Test]
        public void TryMergeMcpServers_ClaudeCode_PreservesExistingDisabledServers()
        {
            string projectRoot = Directory.GetParent(Application.dataPath).FullName.Replace("\\", "/");
            File.WriteAllText(
                _tempConfigPath,
                new JObject
                {
                    ["projects"] = new JObject
                    {
                        [projectRoot] = new JObject
                        {
                            ["mcpServers"] = new JObject(),
                            ["disabledMcpServers"] = new JArray("morph")
                        }
                    }
                }.ToString()
            );

            bool merged = InvokeTryMergeMcpServers(_tempConfigPath, JObject.Parse(McpUtils.GenerateMcpConfigJson()), "Claude Code");
            JObject mergedConfig = JObject.Parse(File.ReadAllText(_tempConfigPath));
            JArray disabledMcpServers = (JArray)mergedConfig["projects"][projectRoot]["disabledMcpServers"];

            Assert.IsTrue(merged);
            CollectionAssert.AreEquivalent(new[] { "morph", "augment" }, disabledMcpServers.ToObject<string[]>());
        }
    }
}
