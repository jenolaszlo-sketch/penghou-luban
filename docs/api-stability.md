# Luban public API stability

The initial preview API is now recorded in
[src/Penghou.Luban/PublicAPI.Shipped.txt](../src/Penghou.Luban/PublicAPI.Shipped.txt).
The companion `PublicAPI.Unshipped.txt` is reserved for reviewed future additions.
The analyzer's filename is a compatibility convention; it does not mean this
candidate has been published on NuGet.

## Final ownership

`TextPatch` and `PatchLimits` belong to `Penghou.Luban.Changes` in the Luban
assembly. The temporary `Penghou.IO.Abstractions` namespace is removed without
alias types. Update consumer imports to `using Penghou.Luban.Changes;`.
The unused legacy `FilePatchRequest` was removed: no Luban runtime accepted it
and no inspected consumer used it. Physical byte persistence remains in IO;
Luban still uses neutral IO invocation/path/version contracts in its operations.
This is the last coordinated ownership correction before the initial preview.

## Build enforcement

The private development dependency
[Microsoft.CodeAnalysis.PublicApiAnalyzers 3.3.4](https://www.nuget.org/packages/Microsoft.CodeAnalysis.PublicApiAnalyzers/3.3.4)
checks public signatures, nullable annotations, optional defaults and constants
against the baseline. Ordinary builds, tests and packing enforce missing/new
API diagnostics as errors on both .NET 8 and .NET 10. The analyzer is not a
runtime or transitive package dependency. Packing also runs SDK package validation in strict mode for compatible target frameworks. See
[Microsoft's API analyzer documentation](https://github.com/dotnet/roslyn/blob/main/src/RoslynAnalyzers/PublicApiAnalyzers/PublicApiAnalyzers.Help.md).

The baseline was generated using the analyzer's `RS0016` code fix after the
ownership review, then frozen. Qualification deliberately added an undeclared
public type (rejected with RS0016) and removed a public constant (rejected with
RS0017) in an isolated working copy; both probes were reverted before packaging.

Existing `Compile` and `Materialize` overloads have distinct required input types.
Their narrowly scoped RS0026 exemptions preserve those existing entry points;
the baseline freezes the optional parameters, and new overloads must be reviewed.

## Change and review rules

- Add APIs explicitly in `PublicAPI.Unshipped.txt` after reviewing the consumer
  need, bounds, authority/identity contracts and documentation.
- Preserve established namespace, signatures, parameter names/defaults,
  nullability and enum/constant values. A removal or signature change needs an
  explicit versioned migration and consumer qualification; do not rewrite the
  frozen baseline to make a failing build pass.
- Existing language v1 identities/results remain stable. V2 does not silently
  widen v1. Changes to either profile's meaning require profile/identity review
  and its conformance tests even if signatures remain unchanged.
- At a release, review additions before promoting them to the frozen baseline.
  The normal analyzer checks compare source to the checked-in contract; reviewers
  must also inspect edits to that contract.

This completes the initial API review and guard setup. It does not assert stable
1.0 compatibility for all hypothetical hosts, a completed Hufu v2 policy, or
public-feed adoption. Concrete consumer findings are recorded in the
[consumer impact guide](consumer-impact.md); current evidence is in the
[completion ledger](leaf-completion.md).
