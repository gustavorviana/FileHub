namespace FileHub
{
    /// <summary>
    /// Registry of <see cref="IFileHub"/> instances keyed by a string name.
    /// Built up through <see cref="NamedFileHubs.Register"/>; queried here.
    /// Lookups return <c>null</c> when the name is unknown, null, or empty —
    /// they never throw.
    /// </summary>
    public interface INamedFileHubs
    {
        /// <summary>
        /// Returns the hub registered under <paramref name="name"/>, or <c>null</c>
        /// if no matching registration exists.
        /// </summary>
        IFileHub GetByName(string name);

        /// <summary>
        /// Returns the root directory of the hub registered under <paramref name="name"/>,
        /// or <c>null</c> if no matching registration exists.
        /// </summary>
        FileDirectory GetRootByName(string name);
    }
}
