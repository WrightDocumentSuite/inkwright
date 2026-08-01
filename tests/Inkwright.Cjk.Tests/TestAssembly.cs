using Xunit;

// These tests deliberately replace the process-wide CMapProvider.Source. Keep them serial so the
// fallback test cannot clear the source while another test is enumerating the packaged CMaps.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
