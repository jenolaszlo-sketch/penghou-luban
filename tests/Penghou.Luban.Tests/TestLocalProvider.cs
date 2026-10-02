using Penghou.IO.Abstractions;
using Penghou.IO.Local;

namespace Penghou.Luban.Tests;

internal static class TestLocalProvider
{
    internal static IWorkspaceProvider Create(string workspaceId, string root,
        LocalPatchNamespace namespaceProfile = LocalPatchNamespace.HostControlled) =>
        new LocalWorkspaceProvider(new WorkspaceId(workspaceId), root, namespaceProfile);
}
