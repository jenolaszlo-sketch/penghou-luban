using System.Collections;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class Utf8PatchMaterializerTests
{
    [Fact]
    public void Captures_untrusted_collection_count_once()
    {
        var patches = new ChangingCount([new TextPatch(0, 1, Encoding.UTF8.GetBytes("b"))]);
        var result = Utf8PatchMaterializer.Materialize(Encoding.UTF8.GetBytes("a"), patches, 16);
        Assert.Equal("b", Encoding.UTF8.GetString(result));
        Assert.Equal(1, patches.CountReads);
    }

    [Fact]
    public void Rejects_replacement_over_declared_bound_before_materialization()
    {
        var patch = new TextPatch(0, 0, new byte[1024 * 1024 + 1]);
        Assert.Throws<InvalidDataException>(() => Utf8PatchMaterializer.Materialize([], [patch], 2 * 1024 * 1024));
    }

    private sealed class ChangingCount(TextPatch[] values) : IReadOnlyList<TextPatch>
    {
        public int CountReads { get; private set; }
        public int Count => ++CountReads == 1 ? values.Length : int.MaxValue;
        public TextPatch this[int index] => values[index];
        public IEnumerator<TextPatch> GetEnumerator() => ((IEnumerable<TextPatch>)values).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
    }
}
