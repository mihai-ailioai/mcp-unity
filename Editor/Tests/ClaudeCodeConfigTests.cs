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
    }
}
