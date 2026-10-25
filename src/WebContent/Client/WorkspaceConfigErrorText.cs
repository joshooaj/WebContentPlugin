namespace WebContent.Client
{
    /// <summary>
    /// Localized wording for a detected workspace configuration error, shared by the Smart Client
    /// WorkSpaceNotification (ticket #9) and the Management Client admin banner (ticket #27).
    /// Kept out of the pure config classes on purpose: it reads satellite resources and therefore
    /// is not linked into the test project.
    /// </summary>
    internal static class WorkspaceConfigErrorText
    {
        public static string Describe(WorkspaceConfigError error)
        {
            switch (error)
            {
                case WorkspaceConfigError.MalformedJson:
                    return Strings.WorkspaceConfigErrorMalformedJson;
                case WorkspaceConfigError.MissingBrowserConfig:
                    return Strings.WorkspaceConfigErrorMissingBrowserConfig;
                default:
                    return string.Empty;
            }
        }
    }
}
