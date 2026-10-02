using System.Text;
using System.Security.Cryptography;
using Penghou.Luban.Changes;
using Xunit;

namespace Penghou.Luban.Tests;

public sealed class TextChangeTransportTests
{
    [Fact]
    public void DiffMatchesIndependentCanonicalGoldenVector()
    {
        var result = TextDiffEngine.Diff("a\n", "b\n");
        var encoded = TextChangeTransport.EncodeDiff(result);
        Assert.Equal(TextChangeTransportStatus.Succeeded, encoded.Status);
        var expectedBody = "{\"schema\":\"penghou.luban.text-change\",\"version\":1,\"kind\":\"diff\",\"profile\":\"penghou.luban.text-lcs.v1\",\"options\":{\"maxInputBytes\":131072,\"maxLines\":4096,\"maxWorkCells\":4000000,\"maxMatrixBytes\":16777216,\"maxEdits\":4096,\"maxOutputBytes\":262144,\"maxConflicts\":4096},\"status\":\"succeeded\",\"before\":{\"sha256\":\"87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7\",\"byteLength\":2},\"after\":{\"sha256\":\"0263829989b6fd954f72baaf2fc64bc2e2f01d692d4de72986ea808f6e99813f\",\"byteLength\":2},\"edits\":[{\"start\":0,\"deleteLength\":2,\"replacement\":\"b\\n\"}]}";
        const string expectedIdentity = "426c50acaffb0bf2d2a6583fdcc3d946834f879b4c991cb8c71584b4872c42b5";
        var independentlyHashed = SHA256.HashData(Encoding.UTF8.GetBytes("penghou.luban.text-change.transport.v1\0" + expectedBody));
        Assert.Equal(expectedIdentity, Convert.ToHexString(independentlyHashed).ToLowerInvariant());
        Assert.Equal(expectedBody[..^1] + ",\"identity\":\"" + expectedIdentity + "\"}", Encoding.UTF8.GetString(encoded.Value!.ToArray()));
        Assert.Equal(expectedIdentity, encoded.Value.Identity);

        var decoded = TextChangeTransport.DecodeDiff(encoded.Value.ToArray());
        Assert.Equal(TextChangeTransportStatus.Succeeded, decoded.Status);
        Assert.Equal(expectedIdentity, decoded.Value!.Identity);
        Assert.Equal(result.Before!.Sha256, decoded.Value.Result.Before!.Sha256);
        Assert.Equal(result.Edits, decoded.Value.Result.Edits);
        Assert.Equal(result.Options, decoded.Value.Options);
    }

    [Fact]
    public void MergeRoundTripsSnapshotsOrderedEditsAndExactConflictStrings()
    {
        var clean = TextMergeEngine.Merge("a\r\nb\n", "A\r\nb\n", "a\r\nB\n");
        var cleanBytes = TextChangeTransport.EncodeMerge(clean).Value!.ToArray();
        var decodedClean = TextChangeTransport.DecodeMerge(cleanBytes);
        Assert.Equal(TextChangeTransportStatus.Succeeded, decodedClean.Status);
        Assert.Equal(clean.Status, decodedClean.Value!.Result.Status);
        Assert.Equal(clean.Value, decodedClean.Value.Result.Value);
        Assert.Equal(clean.Base!.Sha256, decodedClean.Value.Result.Base!.Sha256);
        Assert.Equal(clean.Edits, decodedClean.Value.Result.Edits);

        var conflict = TextMergeEngine.Merge("a\nb\n", "A\nb\n", "B\nb\n");
        var conflictBytes = TextChangeTransport.EncodeMerge(conflict).Value!.ToArray();
        var decodedConflict = TextChangeTransport.DecodeMerge(conflictBytes);
        Assert.Equal(TextChangeTransportStatus.Succeeded, decodedConflict.Status);
        Assert.Equal(conflict.Conflicts, decodedConflict.Value!.Result.Conflicts);
        Assert.Equal("a\n", decodedConflict.Value.Result.Conflicts[0].BaseText);
        Assert.Equal("A\n", decodedConflict.Value.Result.Conflicts[0].OursText);
        Assert.Equal("B\n", decodedConflict.Value.Result.Conflicts[0].TheirsText);
        Assert.Null(decodedConflict.Value.Result.Value);
    }

    [Fact]
    public void MergeMatchesIndependentCanonicalGoldenVector()
    {
        var result = TextMergeEngine.Merge("a\n", "b\n", "a\n");
        var encoded = TextChangeTransport.EncodeMerge(result);
        Assert.Equal(TextChangeTransportStatus.Succeeded, encoded.Status);
        var expectedBody = "{\"schema\":\"penghou.luban.text-change\",\"version\":1,\"kind\":\"merge\",\"profile\":\"penghou.luban.text-lcs.v1\",\"options\":{\"maxInputBytes\":131072,\"maxLines\":4096,\"maxWorkCells\":4000000,\"maxMatrixBytes\":16777216,\"maxEdits\":4096,\"maxOutputBytes\":262144,\"maxConflicts\":4096},\"status\":\"clean\",\"base\":{\"sha256\":\"87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7\",\"byteLength\":2},\"ours\":{\"sha256\":\"0263829989b6fd954f72baaf2fc64bc2e2f01d692d4de72986ea808f6e99813f\",\"byteLength\":2},\"theirs\":{\"sha256\":\"87428fc522803d31065e7bce3cf03fe475096631e5e07bbd7a0fde60c4cf25c7\",\"byteLength\":2},\"result\":{\"sha256\":\"0263829989b6fd954f72baaf2fc64bc2e2f01d692d4de72986ea808f6e99813f\",\"byteLength\":2},\"value\":\"b\\n\",\"edits\":[{\"start\":0,\"deleteLength\":2,\"replacement\":\"b\\n\"}],\"conflicts\":[]}";
        const string expectedIdentity = "883c16399cf77d5601e60b0504eb762a6cb1e47bb86887c3729f148f8ea524a4";
        var independentlyHashed = SHA256.HashData(Encoding.UTF8.GetBytes("penghou.luban.text-change.transport.v1\0" + expectedBody));
        Assert.Equal(expectedIdentity, Convert.ToHexString(independentlyHashed).ToLowerInvariant());
        Assert.Equal(expectedBody[..^1] + ",\"identity\":\"" + expectedIdentity + "\"}", Encoding.UTF8.GetString(encoded.Value!.ToArray()));
        var decoded = TextChangeTransport.DecodeMerge(encoded.Value.ToArray());
        Assert.Equal(TextChangeTransportStatus.Succeeded, decoded.Status);
        Assert.Equal(expectedIdentity, decoded.Value!.Identity);
        Assert.Equal(result.Value, decoded.Value.Result.Value);
        Assert.Equal(result.Edits, decoded.Value.Result.Edits);
    }

    [Fact]
    public void OptionsAreFrozenOnResultsAndBecomeTransportIdentity()
    {
        var selected = new TextChangeOptions { MaxInputBytes = 64, MaxLines = 12, MaxWorkCells = 999,
            MaxMatrixBytes = 4096, MaxEdits = 7, MaxOutputBytes = 31, MaxConflicts = 5 };
        var diff = TextDiffEngine.Diff("x", "y", selected);
        selected = selected with { MaxInputBytes = 32 };
        Assert.Equal(64, diff.Options!.MaxInputBytes);
        var decoded = TextChangeTransport.DecodeDiff(TextChangeTransport.EncodeDiff(diff).Value!.ToArray());
        Assert.Equal(64, decoded.Value!.Options.MaxInputBytes);
        Assert.Equal(12, decoded.Value.Options.MaxLines);
        Assert.Equal(999, decoded.Value.Options.MaxWorkCells);
        Assert.Equal(4096, decoded.Value.Options.MaxMatrixBytes);
        Assert.Equal(7, decoded.Value.Options.MaxEdits);
        Assert.Equal(31, decoded.Value.Options.MaxOutputBytes);
        Assert.Equal(5, decoded.Value.Options.MaxConflicts);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("x", "y", new TextChangeOptions { MaxLines = 0 })).Status);
    }

    [Fact]
    public void CleanMergeWithLargeBaseAndShortFinalOutputSurvivesTransportBounds()
    {
        var options = new TextChangeOptions { MaxOutputBytes = 8 };
        var result = TextMergeEngine.Merge("A\nB\nC\nD\n", "X\nA\nB\nC\nD\n", "A\nB\n", options);
        Assert.Equal(TextMergeStatus.Clean, result.Status);
        Assert.Equal("X\nA\nB\n", result.Value);
        var encoded = TextChangeTransport.EncodeMerge(result);
        Assert.Equal(TextChangeTransportStatus.Succeeded, encoded.Status);
        var decoded = TextChangeTransport.DecodeMerge(encoded.Value!.ToArray());
        Assert.Equal(TextChangeTransportStatus.Succeeded, decoded.Status);
        Assert.Equal(result.Value, decoded.Value!.Result.Value);
    }

    [Fact]
    public void DecoderRejectsDuplicateUnknownNonCanonicalAndTamperedDocuments()
    {
        var canonical = TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b")).Value!.ToArray();
        var json = Encoding.UTF8.GetString(canonical);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(json.Replace("\"version\":1", "\"version\":1,\"version\":1", StringComparison.Ordinal))).Status);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(json.Replace("\"kind\":\"diff\"", "\"kind\":\"diff\",\"extra\":true", StringComparison.Ordinal))).Status);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(" " + json)).Status);
        var tampered = (byte[])canonical.Clone();
        var replacement = Array.LastIndexOf(tampered, (byte)'b');
        tampered[replacement] = (byte)'c';
        Assert.Equal(TextChangeTransportStatus.InvalidInput, TextChangeTransport.DecodeDiff(tampered).Status);
    }

    [Fact]
    public void DecoderReturnsExplicitUnsupportedLimitAndCancelledOutcomes()
    {
        var canonical = TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b")).Value!.ToArray();
        var future = Encoding.UTF8.GetString(canonical).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal);
        Assert.Equal(TextChangeTransportStatus.Unsupported, TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(future)).Status);
        Assert.Equal(TextChangeTransportStatus.LimitExceeded,
            TextChangeTransport.DecodeDiff(new byte[TextChangeTransport.MaxDocumentBytes + 1]).Status);
        using var source = new CancellationTokenSource(); source.Cancel();
        Assert.Equal(TextChangeTransportStatus.Cancelled, TextChangeTransport.DecodeDiff(canonical, source.Token).Status);
        Assert.Equal(TextChangeTransportStatus.Cancelled, TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b"), source.Token).Status);
    }

    [Fact]
    public void EncodedAndDecodedBuffersHaveIndependentOwnership()
    {
        var encoded = TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b")).Value!;
        var exposedCopy = encoded.ToArray();
        exposedCopy[0] = (byte)' ';
        Assert.StartsWith("{", Encoding.UTF8.GetString(encoded.ToArray()));

        var callerBuffer = encoded.ToArray();
        var decoded = TextChangeTransport.DecodeDiff(callerBuffer);
        Assert.Equal(TextChangeTransportStatus.Succeeded, decoded.Status);
        Array.Fill(callerBuffer, (byte)0);
        Assert.Equal("b", decoded.Value!.Result.Edits[0].Replacement);
        Assert.Equal(1, decoded.Value.Result.Before!.ByteLength);
        Assert.Equal(1, decoded.Value.Result.After!.ByteLength);
    }

    [Fact]
    public void DecoderKeepsImportedRecordsExplicitlyUntrustedAndChecksRanges()
    {
        var encoded = TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b")).Value!.ToArray();
        var decoded = TextChangeTransport.DecodeDiff(encoded);
        Assert.Equal(TextChangeTransportStatus.Succeeded, decoded.Status);
        Assert.Equal(0, decoded.Value!.Result.Edits[0].StartOffset);

        var malformed = Encoding.UTF8.GetString(encoded).Replace("\"start\":0", "\"start\":2", StringComparison.Ordinal);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(malformed)).Status);
    }

    [Fact]
    public void DecoderRejectsRehashedInconsistentAndMalformedUnicodePayloads()
    {
        var encoded = Encoding.UTF8.GetString(TextChangeTransport.EncodeDiff(TextDiffEngine.Diff("a", "b")).Value!.ToArray());
        var noChanges = Rehash(encoded, "\"edits\":[{\"start\":0,\"deleteLength\":1,\"replacement\":\"b\"}]", "\"edits\":[]");
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(noChanges)).Status);

        var noOp = Rehash(encoded, "\"deleteLength\":1,\"replacement\":\"b\"", "\"deleteLength\":0,\"replacement\":\"\"");
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(noOp)).Status);

        var escapedSurrogate = encoded.Replace("\"replacement\":\"b\"", "\"replacement\":\"\\ud800\"", StringComparison.Ordinal);
        var decode = Record.Exception(() => TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(escapedSurrogate)));
        Assert.Null(decode);
        Assert.Equal(TextChangeTransportStatus.InvalidInput,
            TextChangeTransport.DecodeDiff(Encoding.UTF8.GetBytes(escapedSurrogate)).Status);
    }

    private static string Rehash(string document, string oldText, string newText)
    {
        const string marker = ",\"identity\":\"";
        var identityOffset = document.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(identityOffset > 0);
        var body = document[..identityOffset];
        Assert.Contains(oldText, body, StringComparison.Ordinal);
        body = body.Replace(oldText, newText, StringComparison.Ordinal);
        var input = Encoding.UTF8.GetBytes("penghou.luban.text-change.transport.v1\0" + body + "}");
        var identity = Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
        return body + marker + identity + "\"}";
    }
}
