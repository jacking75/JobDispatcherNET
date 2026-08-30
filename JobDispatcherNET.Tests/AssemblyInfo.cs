using Xunit;

// These tests start real worker pools and measure timing; running them in parallel makes the
// machine oversubscribed and the timing assertions flaky.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
