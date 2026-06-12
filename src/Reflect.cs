using System;
using System.Reflection;

namespace REPOPathfinder
{
    internal static class Reflect
    {
        private const BindingFlags InstanceAny = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        public static FieldInfo Field(Type t, string name)
        {
            if (t == null) return null;
            return t.GetField(name, InstanceAny);
        }

        public static T GetFieldValue<T>(FieldInfo fi, object instance, T fallback = default)
        {
            if (fi == null || instance == null) return fallback;
            try
            {
                var v = fi.GetValue(instance);
                if (v is T tv) return tv;
                return fallback;
            }
            catch
            {
                return fallback;
            }
        }
    }
}
