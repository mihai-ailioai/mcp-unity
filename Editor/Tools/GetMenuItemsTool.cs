using McpUnity.Resources;
using McpUnity.Unity;
using Newtonsoft.Json.Linq;

namespace McpUnity.Tools
{
    /// <summary>
    /// Tool wrapper for GetMenuItemsResource.
    /// Returns all available Unity Editor menu items that can be executed via execute_menu_item.
    /// </summary>
    public class GetMenuItemsTool : McpToolBase
    {
        private readonly GetMenuItemsResource _resource;

        public GetMenuItemsTool(GetMenuItemsResource resource)
        {
            Name = "get_menu_items";
            Description = "Returns all available Unity Editor menu items. Use this to discover menu item paths before calling execute_menu_item.";
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
