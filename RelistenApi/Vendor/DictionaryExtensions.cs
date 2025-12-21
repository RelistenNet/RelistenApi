using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Relisten.Vendor
{
    public static class DictionaryExtensions
    {
        [return: MaybeNull]
        public static TV GetValue<TK, TV>(this IDictionary<TK, TV> dict, TK key, [AllowNull] TV defaultValue = default!)
        {
            return dict.TryGetValue(key, out var value) ? value : defaultValue;
        }
    }
}
