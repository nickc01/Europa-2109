using System;
using System.Reflection;
/// <summary>
/// Contains utility functions related to unity objects
/// </summary>
public static class UnityUtilities
{
    /// <summary>
    /// Checks if the native pointer for an object is still alive
    /// </summary>
    public static Func<UnityEngine.Object, bool> IsNativeObjectAlive { get; private set; }

    /// <summary>
    /// Gets the cached pointer for an object
    /// </summary>
    public static Func<UnityEngine.Object, IntPtr> GetCachedPtr { get; private set; }

    static UnityUtilities()
    {
        GetCachedPtr = MethodToDelegate<Func<UnityEngine.Object, IntPtr>, UnityEngine.Object>("GetCachedPtr");
        IsNativeObjectAlive = o =>
        {
            return GetCachedPtr(o) != IntPtr.Zero;
        };
    }

    public static bool IsObjectTrulyNull(UnityEngine.Object obj)
    {
        return ((object)obj) == null;
    }

    public static DelegateType MethodToDelegate<DelegateType, InstanceType>(string methodName, BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
    {
        return MethodToDelegate<DelegateType>(typeof(InstanceType).GetMethod(methodName, Flags));
    }

    public static DelegateType MethodToDelegate<DelegateType>(MethodInfo method)
    {
        if (!typeof(Delegate).IsAssignableFrom(typeof(DelegateType)))
        {
            throw new Exception(typeof(DelegateType).FullName + " is not a delegate type");
        }
        return (DelegateType)(object)Delegate.CreateDelegate(typeof(DelegateType), method);
    }
}