namespace BattlePhaze.SettingsManager
{
#if UNITY_EDITOR
    [System.Serializable]
    public class EditorToggles
    {
        public bool SupportedRenderPipelines;
        public bool DefaultPlatform;
        public bool ExcludePlatform;
        public bool ValueToggle;
        public bool EditorVisable;
    }
#endif
}