namespace WebContent.Client
{
    /// <summary>
    /// Generation-based guard that bounds delayed and background callbacks to their
    /// owning lifecycle. A caller captures a token when it schedules work; before the
    /// work runs it checks <see cref="IsCurrent"/>. After the owner calls <see cref="End"/>
    /// (e.g. when it is closed or disposed), every previously captured token is stale and
    /// the scheduled work is ignored.
    /// </summary>
    public sealed class CallbackOwnership
    {
        private long _generation;

        public long Capture()
        {
            return _generation;
        }

        public bool IsCurrent(long token)
        {
            return token == _generation;
        }

        public void End()
        {
            unchecked
            {
                _generation++;
            }
        }
    }
}
