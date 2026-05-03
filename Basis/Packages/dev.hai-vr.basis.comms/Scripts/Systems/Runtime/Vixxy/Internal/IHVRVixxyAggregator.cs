using System.Collections.Generic;

namespace HVR.Vixxy
{
    public interface IHVRVixxyAggregator
    {
        /// Aggregates the data. If the result is different, or if this was never transformed before, this returns true.
        public bool TryAggregate(out IEnumerable<IHVRVixxyAggregator> aggregators, out IEnumerable<IHVRVixxyActuator> actuators);
    }
}
