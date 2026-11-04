namespace HVR.Vixxy
{
    public interface IHVRVixxyActuator
    {
        /// Called on initialization, and then, if any of the acquisition or aggregation values that this Actuator
        /// listens on changes to a different value, this gets called once after all acquisitions and aggregations have been
        /// processed by the orchestrator.<br/>
        /// An actuator should never submit values to an orchestrator. While undesirable, side effects are allowed to
        /// submit new values for acquisition, but it should be avoided as part of the design. Tolerated side effects would be like
        /// moving an object, which in turns changes a metric that is then submitted for acquisition.
        public void Actuate();

        /// True if this actuator doesn't feed its input value directly to its actuator. There may be a situation where the input value
        /// does not immediately cause the actuated value to change.
        public bool HasFilters();

        /// Write the new actuated value to the actuator, based on its filters.
        HVRVixxyActuatorApplyFilterResult ApplyFilters(float deltaTime);
    }

    public struct HVRVixxyActuatorApplyFilterResult
    {
        public bool actuatorNeedsUpdate;
        public bool filterNeedsCheckNextTick;
    }
}
