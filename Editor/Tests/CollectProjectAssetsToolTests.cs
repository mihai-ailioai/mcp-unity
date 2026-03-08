using System.Collections;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using McpUnity.Tools;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.TestTools;

namespace McpUnity.Tests
{
    public class CollectProjectAssetsToolTests
    {
        private const string TestRootFolder = "Assets/TestCollectProjectAssetsTool";
        private const string IncludedFolder = TestRootFolder + "/Included";
        private const string ExcludedFolder = TestRootFolder + "/Excluded";
        private const string IncludedFolderRelative = "TestCollectProjectAssetsTool/Included";
        private const string PrefabPath = IncludedFolder + "/CollectedPrefab.prefab";
        private const string ExcludedPrefabPath = ExcludedFolder + "/ExcludedPrefab.prefab";
        private const string ScenePath = IncludedFolder + "/CollectedScene.unity";

        private CollectProjectAssetsTool _tool;

        [SetUp]
        public void SetUp()
        {
            _tool = new CollectProjectAssetsTool();

            EnsureFolder("Assets", "TestCollectProjectAssetsTool");
            EnsureFolder(TestRootFolder, "Included");
            EnsureFolder(TestRootFolder, "Excluded");

            var prefabRoot = new GameObject("CollectedPrefab");
            prefabRoot.AddComponent<BoxCollider>();
            PrefabUtility.SaveAsPrefabAsset(prefabRoot, PrefabPath);
            Object.DestroyImmediate(prefabRoot);

            var excludedPrefabRoot = new GameObject("ExcludedPrefab");
            PrefabUtility.SaveAsPrefabAsset(excludedPrefabRoot, ExcludedPrefabPath);
            Object.DestroyImmediate(excludedPrefabRoot);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            new GameObject("CollectedSceneRoot");
            EditorSceneManager.SaveScene(scene, ScenePath);
            EditorSceneManager.CloseScene(scene, true);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        [TearDown]
        public void TearDown()
        {
            if (AssetDatabase.IsValidFolder(TestRootFolder))
            {
                AssetDatabase.DeleteAsset(TestRootFolder);
            }

            AssetDatabase.Refresh();
        }

        [Test]
        public void CollectProjectAssetsTool_HasExpectedMetadata()
        {
            Assert.AreEqual("collect_project_assets", _tool.Name);
            Assert.IsTrue(_tool.IsAsync);
            Assert.IsNotNull(_tool.Description);
        }

        [Test]
        public void CollectProjectAssetsTool_CollectScripts_ReturnsFilePathAndContents()
        {
            string[] scriptGuids = AssetDatabase.FindAssets("CollectProjectAssetsTool t:MonoScript");
            Assert.IsNotEmpty(scriptGuids, "Expected to find the CollectProjectAssetsTool script asset");

            string scriptPath = AssetDatabase.GUIDToAssetPath(scriptGuids[0]);
            string scriptFolder = Path.GetDirectoryName(scriptPath)?.Replace('\\', '/');
            Assert.IsFalse(string.IsNullOrEmpty(scriptFolder), "Expected a valid script folder");

            var documents = CollectProjectAssetsTool.CollectScripts(new System.Collections.Generic.List<string> { scriptFolder });
            JObject scriptDocument = documents
                .Select(document => document.ToResponseJObject())
                .FirstOrDefault(document => document["path"]?.ToString() == scriptPath);

            Assert.IsNotNull(scriptDocument, "Expected collected script document");
            StringAssert.StartsWith($"// File: {scriptPath}\n", scriptDocument["contents"]?.ToString());
            StringAssert.Contains("class CollectProjectAssetsTool", scriptDocument["contents"]?.ToString());
        }

        [UnityTest]
        public IEnumerator CollectProjectAssetsTool_CollectsPrefabsWithoutScenesByDefault()
        {
            var resultTask = ExecuteTool(new JObject
            {
                ["folders"] = new JArray(IncludedFolderRelative)
            });

            yield return WaitForTask(resultTask);

            JObject result = resultTask.Result;
            Assert.IsTrue(result["success"]?.ToObject<bool>() ?? false);

            JArray documents = result["documents"] as JArray;
            Assert.IsNotNull(documents);

            JObject prefabDocument = documents
                .Children<JObject>()
                .FirstOrDefault(doc => doc["path"]?.ToString() == PrefabPath);
            Assert.IsNotNull(prefabDocument, "Expected collected prefab document");
            StringAssert.Contains("CollectedPrefab", prefabDocument["contents"]?.ToString());
            StringAssert.DoesNotContain(ScenePath, documents.ToString());
        }

        [UnityTest]
        public IEnumerator CollectProjectAssetsTool_RespectsFolderFilterAndIncludesScenesWhenRequested()
        {
            var resultTask = ExecuteTool(new JObject
            {
                ["includeScenes"] = true,
                ["folders"] = new JArray(IncludedFolderRelative)
            });

            yield return WaitForTask(resultTask);

            JObject result = resultTask.Result;
            Assert.IsTrue(result["success"]?.ToObject<bool>() ?? false);

            JArray documents = result["documents"] as JArray;
            Assert.IsNotNull(documents);

            Assert.IsFalse(documents.Children<JObject>().Any(doc => doc["path"]?.ToString() == ExcludedPrefabPath));

            JObject sceneDocument = documents
                .Children<JObject>()
                .FirstOrDefault(doc => doc["path"]?.ToString() == ScenePath);
            Assert.IsNotNull(sceneDocument, "Expected collected scene document");
            StringAssert.Contains("CollectedSceneRoot", sceneDocument["contents"]?.ToString());
        }

        private static Task<JObject> ExecuteTool(JObject parameters)
        {
            var tool = new CollectProjectAssetsTool();
            var tcs = new TaskCompletionSource<JObject>();
            tool.ExecuteAsync(parameters, tcs);
            return tcs.Task;
        }

        private static IEnumerator WaitForTask(Task task)
        {
            while (!task.IsCompleted)
            {
                yield return null;
            }
        }

        private static void EnsureFolder(string parentFolder, string childFolderName)
        {
            string fullPath = parentFolder + "/" + childFolderName;
            if (!AssetDatabase.IsValidFolder(fullPath))
            {
                AssetDatabase.CreateFolder(parentFolder, childFolderName);
            }
        }
    }
}
