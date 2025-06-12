namespace BattlePhaze.SaveSystem
{
    /// <summary>
    /// KeyPair class is used in keys map to keep value and comment for a single key
    /// </summary>
    [System.Serializable]
    public class SMOptionInformation
    {
        public string key;
        public string value;
        public string comment;
        /// <summary>
        /// Initializes a new instance of the <see cref="SettingsManagerSaveSystem+KeyPair"/> class.
        /// </summary>
        /// <param name="key">Key name.</param>
        /// <param name="value">Value of key.</param>
        /// <param name="comment">Comment of key.</param>
        public SMOptionInformation(string key, string value, string comment)
        {
            this.key = key;
            this.value = value;
            this.comment = comment;
        }
    }
}