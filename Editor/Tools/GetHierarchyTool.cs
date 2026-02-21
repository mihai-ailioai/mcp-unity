using McpUnity.Resources;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool wrapper for GetScenesHierarchyResource.
    /// Returns the full scene hierarchy with instanceIds, or prefab stage contents if in Prefab Mode.
    /// </summary>
    public class GetHierarchyTool : McpToolBase
    {
        private readonly GetScenesHierarchyResource _resource;

        public GetHierarchyTool(GetScenesHierarchyResource resource)
        {
            Name = "get_hierarchy";
            Description = "Returns the full GameObject hierarchy of all loaded scenes (or prefab stage if in Prefab Mode). Each entry includes name, instanceId, active state, and children. Use this to discover GameObjects and their instanceIds before using other tools.";
            _resource = resource;
        }

        public override JObject Execute(JObject parameters)
        {
            JObject result = _resource.Fetch(parameters);
            result["type"] = "text";
            return result;
        }
    }
}
