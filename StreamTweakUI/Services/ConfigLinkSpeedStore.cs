namespace StreamTweak.Services
{
    /// <summary>
    /// Backs <see cref="LinkSpeedManager"/> with config.json. The manager lives in
    /// StreamTweak.Core, which has no UI dependencies and therefore no access to
    /// ConfigService — this adapter is the seam.
    /// </summary>
    public sealed class ConfigLinkSpeedStore : ILinkSpeedStore
    {
        public string Get(string key, string fallback) => ConfigService.Get(key, fallback);
        public bool GetBool(string key, bool fallback) => ConfigService.GetBool(key, fallback);
        public void Set(string key, string value) => ConfigService.Set(key, value);
        public void Set(string key, bool value) => ConfigService.Set(key, value);
    }
}
