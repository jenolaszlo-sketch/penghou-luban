using System.Collections.ObjectModel;
using System.Text;
using Penghou.IO.Abstractions;
using Penghou.Luban.Language;

namespace Penghou.Luban.Resolution;

/// <summary>Validates and freezes the closed programmatic preview profile.</summary>
public static class PreviewCompiler
{
    private const int HardNodes = 64, HardTargets = 1000, HardReadBytes = 16 * 1024 * 1024;
    private const int HardFileBytes = 16 * 1024 * 1024, HardReplacementBytes = 1024 * 1024;
    private const int HardPatchCount = 128, HardPlanBytes = 4 * 1024 * 1024, HardCalls = 100_000, HardTimeout = 30_000;
    private const int HardDepth = 16, HardEntries = 100_000;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static PreviewCompilation Compile(IReadOnlyList<PreviewStage> stages, WorkspaceId workspace,
        PreviewLimits? limits = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        limits ??= new PreviewLimits();
        var diagnostics = new List<LanguageDiagnostic>();
        if (!ValidLimits(limits)) diagnostics.Add(new("PREVIEW_LIMITS", "Preview limits must be positive and within the frozen profile."));
        if (stages is null) diagnostics.Add(new("PREVIEW_STAGES_NULL", "Preview stages cannot be null."));
        if (!ValidWorkspace(workspace)) diagnostics.Add(new("PREVIEW_WORKSPACE", "Workspace identity must be nonempty, bounded UTF-8 without control characters."));
        if (diagnostics.Count != 0) return Failure(diagnostics);

        try
        {
            var count = stages!.Count;
            if (count == 0) return Fail("PREVIEW_EMPTY", "A preview document must contain at least one operation.");
            if (count > Math.Min(limits!.MaxNodes, HardNodes)) return Fail("PREVIEW_NODE_LIMIT", "Preview node limit exceeded.");

            var operations = new List<PreviewOperation>(count);
            var exactPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long possibleTargets = 0, declaredReplacementBytes = 0, declaredPatchCount = 0, minimumCalls = 0;
            var blocked = false;
            for (var i = 0; i < count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                switch (stages[i])
                {
                    case FilePatchStage file:
                    {
                        var path = LanguagePaths.Normalize(file.Path);
                        if (!exactPaths.Add(path)) { diagnostics.Add(new("PREVIEW_DUPLICATE_TARGET", "Exact patch targets cannot appear more than once.")); break; }
                        var patches = FreezePatches(file.Patches, limits, ref declaredReplacementBytes, ref declaredPatchCount, diagnostics, cancellationToken);
                        if (patches is null) break;
                        if (file.ExpectedVersion is { } expected && !ValidVersion(expected))
                        { diagnostics.Add(new("PREVIEW_EXPECTED_VERSION", "Expected version must be a nonempty bounded token without control characters.")); break; }
                        possibleTargets++;
                        minimumCalls += blocked ? 2 : 7 + 2 * path.Split('/').Length;
                        operations.Add(new ExactPatchOperation(path, patches, file.ExpectedVersion is { } version ? new ResourceVersion(version.Value) : null));
                        break;
                    }
                    case GlobPatchStage glob:
                    {
                        var root = LanguagePaths.Normalize(glob.Root, allowRoot: true);
                        var pattern = LanguageGlob.Normalize(glob.Pattern);
                        if (glob.Selection is not PreviewSelection.AuthorizedView and not PreviewSelection.AllMatches)
                        { diagnostics.Add(new("PREVIEW_SELECTION", "Selection semantics must be AuthorizedView or AllMatches.")); break; }
                        if (glob.Limits is null) { diagnostics.Add(new("PREVIEW_TRAVERSAL_LIMITS", "Glob traversal limits cannot be null.")); break; }
                        if (!ValidTraversal(glob.Limits, limits))
                        { diagnostics.Add(new("PREVIEW_TRAVERSAL_LIMITS", "Glob traversal limits exceed the frozen profile.")); break; }
                        var patches = FreezePatches(glob.Patches, limits, ref declaredReplacementBytes, ref declaredPatchCount, diagnostics, cancellationToken);
                        if (patches is null) break;
                        possibleTargets += glob.Limits.MaxMatches;
                        // Only a static lower bound is knowable: candidate counts,
                        // private metadata release dependencies and matches are dynamic.
                        minimumCalls += blocked || glob.Selection == PreviewSelection.AllMatches ? 2 :
                            4 + 2 * (root.Length == 0 ? 0 : root.Split('/').Length);
                        blocked |= glob.Selection == PreviewSelection.AllMatches;
                        operations.Add(new SelectedPatchOperation(root, pattern, glob.Selection, patches, glob.Limits with { }));
                        break;
                    }
                    case DeferredToolStage deferred:
                        if (!Enum.IsDefined(deferred.Tool)) { diagnostics.Add(new("PREVIEW_DEFERRED_TOOL", "Deferred tool is outside the closed preview catalogue.")); break; }
                        var deferredRoot = LanguagePaths.Normalize(deferred.Root, allowRoot: true);
                        operations.Add(new DeferredToolOperation(deferred.Tool, deferredRoot));
                        minimumCalls += 2;
                        blocked = true;
                        break;
                    default:
                        diagnostics.Add(new("PREVIEW_UNKNOWN_STAGE", "Preview stage is not registered in this profile."));
                        break;
                }
                if (possibleTargets > limits.MaxTargets || possibleTargets > HardTargets)
                    diagnostics.Add(new("PREVIEW_TARGET_LIMIT", "Preview target ceiling exceeded."));
                if (declaredPatchCount > limits.MaxPatchCount || declaredPatchCount > HardPatchCount)
                    diagnostics.Add(new("PREVIEW_PATCH_COUNT", "Declared patch count exceeds the profile."));
                if (declaredReplacementBytes > limits.MaxReplacementBytes || declaredReplacementBytes > HardReplacementBytes)
                    diagnostics.Add(new("PREVIEW_REPLACEMENT_LIMIT", "Declared replacement bytes exceed the profile."));
                if (minimumCalls > limits.MaxResourceCalls || minimumCalls > HardCalls)
                    diagnostics.Add(new("PREVIEW_RESOURCE_CALLS", "Even the minimum required authorization calls exceed the budget; dynamic execution may need more."));
                if (diagnostics.Count != 0) return Failure(diagnostics);
            }

            var frozen = new ReadOnlyCollection<PreviewOperation>(operations.ToArray());
            var identity = PreviewIdentity.Document(workspace, limits, frozen);
            var compiled = frozen.Select((op, index) => new CompiledPreviewNode(index, PreviewIdentity.Node(identity, index), op)).ToArray();
            return new(new CompiledPreviewDocument(workspace, limits with { }, identity, compiled), Array.Empty<LanguageDiagnostic>());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or OverflowException or EncoderFallbackException or InvalidDataException)
        {
            diagnostics.Add(new("PREVIEW_INVALID_VALUE", "A preview value is invalid or exceeds its bounded encoding."));
            return Failure(diagnostics);
        }

        static PreviewCompilation Failure(List<LanguageDiagnostic> ds) => new(null, new ReadOnlyCollection<LanguageDiagnostic>(ds.ToArray()));
        static PreviewCompilation Fail(string code, string message) => new(null, Array.AsReadOnly(new[] { new LanguageDiagnostic(code, message) }));
    }

    private static IReadOnlyList<FrozenTextPatch>? FreezePatches(IReadOnlyList<TextPatch> patches, PreviewLimits limits,
        ref long replacementBytes, ref long patchCount, List<LanguageDiagnostic> diagnostics, CancellationToken ct)
    {
        if (patches is null) { diagnostics.Add(new("PREVIEW_PATCHES_NULL", "Patch list cannot be null.")); return null; }
        var count = patches.Count;
        if (count == 0) { diagnostics.Add(new("PREVIEW_PATCHES_EMPTY", "Patch list must contain at least one edit.")); return null; }
        patchCount += count;
        if (patchCount > limits.MaxPatchCount || patchCount > HardPatchCount)
        { diagnostics.Add(new("PREVIEW_PATCH_COUNT", "Declared patch count exceeds the profile.")); return null; }
        var frozen = new List<FrozenTextPatch>(count);
        var priorStart = -1;
        long priorEnd = -1;
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var patch = patches[i];
            if (patch is null)
            { diagnostics.Add(new("PREVIEW_PATCH_NULL", "Patch entries cannot be null.")); return null; }
            if (patch.StartOffset < 0 || patch.DeleteLength < 0)
            { diagnostics.Add(new("PREVIEW_PATCH_RANGE", "Patch offsets and delete lengths must be nonnegative.")); return null; }
            var end = (long)patch.StartOffset + patch.DeleteLength;
            if (end > int.MaxValue || (i != 0 && (patch.StartOffset <= priorStart || patch.StartOffset < priorEnd)))
            { diagnostics.Add(new("PREVIEW_PATCH_ORDER", "Patches must be strictly ordered, nonoverlapping, and within signed offset bounds.")); return null; }
            var replacement = patch.ReplacementUtf8;
            replacementBytes += replacement.Length;
            if (replacementBytes > limits.MaxReplacementBytes || replacementBytes > HardReplacementBytes)
            { diagnostics.Add(new("PREVIEW_REPLACEMENT_LIMIT", "Declared replacement bytes exceed the profile.")); return null; }
            var immutable = new ImmutableBytes(replacement.Span);
            if (!IsStrictUtf8(immutable.Span))
            { diagnostics.Add(new("PREVIEW_REPLACEMENT_UTF8", "Replacement bytes must be strict UTF-8.")); return null; }
            frozen.Add(new FrozenTextPatch(patch.StartOffset, patch.DeleteLength, immutable));
            priorStart = patch.StartOffset; priorEnd = end;
        }
        return new ReadOnlyCollection<FrozenTextPatch>(frozen.ToArray());
    }

    private static bool IsStrictUtf8(ReadOnlySpan<byte> bytes)
    {
        try { _ = StrictUtf8.GetCharCount(bytes); return true; }
        catch (DecoderFallbackException) { return false; }
    }

    private static bool ValidWorkspace(WorkspaceId workspace)
    {
        var value = workspace.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool ValidVersion(ResourceVersion version)
    {
        var value = version.Value;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Any(char.IsControl)) return false;
        try { return StrictUtf8.GetByteCount(value) <= 1024; }
        catch (EncoderFallbackException) { return false; }
    }

    private static bool ValidTraversal(TraversalLimits traversal, PreviewLimits limits) =>
        traversal.MaxDepth is >= 0 and <= HardDepth &&
        traversal.MaxEntries is >= 1 and <= HardEntries && traversal.MaxEntries <= limits.MaxResourceCalls &&
        traversal.MaxMatches is >= 0 and <= HardTargets && traversal.MaxMatches <= limits.MaxTargets &&
        traversal.MaxOutputBytes is >= 0 and <= HardPlanBytes && traversal.MaxOutputBytes <= limits.MaxPlanBytes;

    internal static bool ValidLimits(PreviewLimits limits) =>
        limits.MaxNodes is > 0 and <= HardNodes && limits.MaxTargets is > 0 and <= HardTargets &&
        limits.MaxReadBytes is > 0 and <= HardReadBytes && limits.MaxFileBytes is > 0 and <= HardFileBytes &&
        limits.MaxReplacementBytes is > 0 and <= HardReplacementBytes && limits.MaxPatchCount is > 0 and <= HardPatchCount &&
        limits.MaxPlanBytes is > 0 and <= HardPlanBytes && limits.MaxResourceCalls is > 0 and <= HardCalls &&
        limits.TimeoutMilliseconds is > 0 and <= HardTimeout && limits.MaxFileBytes <= limits.MaxReadBytes;
}
