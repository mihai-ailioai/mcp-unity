using System;
using System.Reflection;

namespace McpUnity.Tools
{
    public static class SerializedFieldUtils
    {
        public static Type FindType(string typeName, Type baseConstraint)
        {
            Type type = Type.GetType(typeName);
            if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
            {
                return type;
            }

            // Try Assembly-CSharp (user scripts)
            type = Type.GetType($"{typeName}, Assembly-CSharp");
            if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
            {
                return type;
            }

            string[] commonNamespaces = new string[]
            {
                "UnityEngine",
                "UnityEngine.UI",
                "UnityEngine.EventSystems",
                "UnityEngine.Animations",
                "UnityEngine.Rendering",
                "TMPro"
            };

            foreach (string ns in commonNamespaces)
            {
                type = Type.GetType($"{ns}.{typeName}, UnityEngine");
                if (type != null && (baseConstraint == null || baseConstraint.IsAssignableFrom(type)))
                {
                    return type;
                }
            }

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    foreach (Type t in assembly.GetTypes())
                    {
                        if (t.Name == typeName && (baseConstraint == null || baseConstraint.IsAssignableFrom(t)))
                        {
                            return t;
                        }
                    }
                }
                catch (Exception)
                {
                    continue;
                }
            }

            return null;
        }
    }
}
