namespace WebContent.Admin
{
    /// <summary>
    /// Name gate for <see cref="WorkspaceItemManager"/>: an empty or whitespace-only name is
    /// rejected on save, because such an item is impossible to address later and the tree shows
    /// only a blank node. The name the item was created with is deliberately not restricted —
    /// keeping the <see cref="WorkspaceConfiguration.NewWorkspacePlaceholderName"/> default is a
    /// legitimate choice, already special-cased by
    /// <see cref="WorkspaceConfiguration.IsUnfinishedNewWorkspace"/>.
    /// </summary>
    internal static class WorkspaceNameValidation
    {
        public static bool IsEmpty(string name)
        {
            return string.IsNullOrWhiteSpace(name);
        }
    }
}
