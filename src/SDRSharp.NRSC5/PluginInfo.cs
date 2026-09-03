namespace SDRSharp.NRSC5;

internal static class PluginInfo
{
    /// <summary>
    /// Build shown in the header byline, so a tester can tell versions apart. It also
    /// identifies the plugin to the FCC query service, which is why it does not live in
    /// the panel any more: the lookup has no business depending on the user interface.
    /// </summary>
    internal const string DevelopmentVersion = "3.3.4";
}
