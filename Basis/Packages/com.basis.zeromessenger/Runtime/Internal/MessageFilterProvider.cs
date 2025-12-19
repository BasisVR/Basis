using System.Collections.Generic;
using System.Linq;
using UnityEngine.Scripting;

namespace Basis.ZeroMessenger.Internal
{
    // for Dependency Injection

    [Preserve]
    public sealed class MessageFilterProvider<T>
    {
        readonly IMessageFilter<T>[] filters;

        [Preserve]
        public MessageFilterProvider(IEnumerable<IMessageFilterBase> untypedFilters, IEnumerable<IMessageFilter<T>> typedFilters)
        {
            filters = untypedFilters
                .OfType<IMessageFilter<T>>()
                .Concat(typedFilters)
                .Distinct()
                .ToArray();
        }

        public IEnumerable<IMessageFilter<T>> GetGlobalFilters()
        {
            return filters;
        }
    }
}
