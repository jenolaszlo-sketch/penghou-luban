using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class PatchContractOwnershipTests
{
    [Fact]
    public void PatchContractsAreExportedOnlyByLuban()
    {
        var lubanAssembly = typeof(TextPatch).Assembly;
        var abstractionsAssembly = typeof(ResourceVersion).Assembly;
        var expectedNames = new[] { nameof(TextPatch), nameof(PatchLimits) };

        foreach (var name in expectedNames)
        {
            var type = lubanAssembly.GetExportedTypes().Single(candidate => candidate.Name == name);

            Assert.Equal("Penghou.Luban.Changes", type.Namespace);
            Assert.Same(lubanAssembly, type.Assembly);
            Assert.DoesNotContain(abstractionsAssembly.GetExportedTypes(), candidate => candidate.Name == name);
        }

        Assert.DoesNotContain(lubanAssembly.GetExportedTypes(), candidate => candidate.Name == "FilePatchRequest");
        Assert.DoesNotContain(abstractionsAssembly.GetExportedTypes(), candidate =>
            candidate.Namespace == "Penghou.IO.Abstractions" && candidate.Name is "TextPatch" or "PatchLimits" or "FilePatchRequest");
    }
}
