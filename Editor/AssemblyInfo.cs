using System.Runtime.CompilerServices;

// The pull/push tests run Push and Pull against throwaway project directories,
// which needs UniLfsPaths.OverrideProjectRoot. That seam stays internal rather
// than becoming part of the package's API: repointing the project root is a
// test fixture, not something a consumer should be able to do at runtime.
[assembly: InternalsVisibleTo("UniLFS.Editor.Tests")]
