namespace SonsVRMod
{
    /// <summary>
    /// What this build calls itself.
    /// <para>
    /// One constant, read by everything that shows a version: the MelonLoader metadata, the log
    /// line at startup, and the settings panel. A version written in several places eventually
    /// disagrees with itself, and the copy a player reads is the one that goes stale.
    /// </para>
    /// <para>
    /// Kept in step with the tag on the GitHub release by hand, so the number a player reports from
    /// the panel names a release that can actually be downloaded and compared against.
    /// </para>
    /// </summary>
    public static class ModInfo
    {
        /// <summary>
        /// The release this build corresponds to. Must be a constant: it is used as an attribute
        /// argument, which the compiler needs to resolve at build time.
        /// </summary>
        public const string Version = "0.0.5-alpha";

        public const string Name = "SonsVR_Mod";

        public const string Author = "Anthony";
    }
}
