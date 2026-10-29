using Xunit;

// The integration facts drive real browsers and the foreground window; nothing may run beside them.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
