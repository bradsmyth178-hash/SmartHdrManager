using System;
using System.Collections.Generic;
using System.Linq;

namespace SmartHdrManager;

public static class DisplaySelection
{
    // Never substitute primary/all when a persisted identity is absent or ambiguous.
    public static T[] Match<T>(IEnumerable<T> live, string key, Func<T, string> identity)
    {
        if (string.IsNullOrWhiteSpace(key)) return Array.Empty<T>();
        var matches = live.Where(item => string.Equals(identity(item), key, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1 ? matches : Array.Empty<T>();
    }
}
