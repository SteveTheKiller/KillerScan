namespace KillerScan.Engine
{
    internal static class TaskFaults
    {
        /// <summary>
        /// Marks a fault on <paramref name="task"/> as seen. A connect or receive that loses a
        /// timeout race keeps running and usually faults once its socket is disposed; without this,
        /// every one of them reaches the finalizer as an unobserved task exception. Returns the
        /// same task, so it can wrap the call that starts it.
        /// </summary>
        internal static T ObserveFault<T>(this T task) where T : Task
        {
            _ = task.ContinueWith(t => { _ = t.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return task;
        }
    }
}
