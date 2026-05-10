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
    }
}
