namespace StreamTweak
{
    /// <summary>
    /// Represents a user-configured application that StreamTweak kills at stream start
    /// and relaunches at stream end. Serialized to managedapps.json.
    /// </summary>
    public class ManagedApp
    {
        public string Name { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public bool AutoManage { get; set; } = true;
    }
}
