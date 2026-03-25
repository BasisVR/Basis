using System;
using System.Collections.Generic;

namespace Basis.BTween
{
    public static class BTweenManager
    {
        private const int MAX_GROUP_COUNT = 16;
        private static readonly List<Action<double>> _tweenProcessors = new(MAX_GROUP_COUNT);

        internal static void RegisterGroup(Action<double> processor)
        {
            if (processor == null)
            {
                return;
            }

            if (_tweenProcessors.Contains(processor))
            {
                return;
            }

            _tweenProcessors.Add(processor);
        }

        public static void Simulate(double realtimeSinceStartupAsDouble)
        {
            List<Action<double>> processors = _tweenProcessors;
            foreach (Action<double> tween in processors)
            {
                tween(realtimeSinceStartupAsDouble);
            }
        }
    }
}
