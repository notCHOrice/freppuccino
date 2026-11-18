#if !NET9_0_OR_GREATER
using System.Text.Json.Nodes;

namespace Freppuccino;

static class Net8Compat
{
    public static void Insert(this JsonObject o, int index, string key, JsonNode? value)
    {
        var entries = o.ToList();
        o.Clear();
        index = Math.Clamp(index, 0, entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            if (i == index) o[key] = value;
            if (entries[i].Key != key) o[entries[i].Key] = entries[i].Value;
        }
        if (index == entries.Count) o[key] = value;
    }

    public static string ToHexStringLower(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
}
#endif
