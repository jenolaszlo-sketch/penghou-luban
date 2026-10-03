# Consumer impact for the Luban preview package

Status: **candidate package; no public publication or downstream package
adoption is claimed**. The package is intended to be provider-neutral and to
carry Luban's API surface without requiring consumers to build a sibling source
checkout.

## Package dependency impact

`Penghou.Luban` references `Penghou.IO.Abstractions` and
`Penghou.IO.Protocols` at the same preview version. It must not reference
`Penghou.IO.Local`: a host selects and composes its provider, and tests/sample
may separately use Local. Consumers currently need the IO packages available
from the feed or candidate package source to restore the closure. CI's
source-built isolated feed proves package-reference closure only; it is not
evidence of publication or of public-feed availability.

## Package graph and change ownership

| Producer → consumer | Current impact | Owner / next check |
| --- | --- | --- |
| IO.Abstractions + IO.Protocols → Luban | Exact `[0.1.0-preview.1]` package references; normal builds have no sibling source dependency | IO 0.1.0-preview.1 is published; Luban public-only restore/tests and package consumer pass |
| IO.Local → host/sample/tests | Concrete reader/writer stays outside Luban core | Each host explicitly selects its provider and controlled write namespace |
| Luban → Hufu integration | Exact candidate package reference and 101 tests per framework qualified; v2 fails closed | Hufu verifies public-feed adoption; any v2 policy support is separate work |
| Luban → other hosts | Required authorization/start/outcome contracts stay provider/policy neutral | The host qualifies its selected profiles; no generic ecosystem-wide adoption is claimed |

`TextPatch` and `PatchLimits` now live in `Penghou.Luban.Changes` in the Luban
assembly; consumer imports must follow that final namespace. The unused legacy
`FilePatchRequest` was removed before publication because no runtime accepted it.
Do not mix these candidates with older IO assemblies containing patch types.
The [API stability policy](api-stability.md) records the final ownership and
checked-in signature baseline; future breaking changes require a versioned
migration entry here.

IO publication and Luban qualification against public IO are complete. Next,
publish Luban from the public dependency feed, then verify public adoption in Hufu. The initial Luban
feature baseline stays closed unless a concrete consumer finding requires a
producer correction. Record that finding, the affected contract/profile and the
consumers to requalify in this document.

## Source and ABI impact

The runtime constructors take an explicit workspace, provider and authorizer.
For example, `LanguageRuntime(WorkspaceReference, IWorkspaceProvider,
ILanguageAuthorizer)` has no permissive default. Consumer-owned composition must
continue supplying those dependencies and must not infer authorization from a
language value. This is an intentional provider/authority seam rather than a
convenience local-filesystem constructor.

The language's v1 `SearchMatchValue` contains path, line number and full line;
there is no v1 match-span or context-window contract. The separately opted-in v2
profile implements richer range and match information. Keep v1
serialization and meaning stable. New v2 semantic requests should be denied by
default until a consumer policy understands them; they do not require Hufu to
ship a v2 implementation.

Luban is a new package candidate, so no previously published Luban package ABI
is being preserved by this preview. The public API baseline is now checked in and enforced during builds.
Review compatibility across previews and document any enum additions and source
changes. In particular, append-only enum evolution
does not guarantee that downstream exhaustive switches behave safely.

## Hufu as an optional consumer

Hufu.Luban now selects exact `[0.1.0-preview.1]` PackageReference by default;
`UseLubanSource=true` and `LubanRoot` retain explicit source-build composition.
The original Hufu checkout passes **101 existing/Luban/Cedar/SQLite tests per
framework** against the stabilized Luban package with a fresh cache. Its separate
IO suite passes **19 per framework** in the staged qualification. Assets confirm
Luban is a package and there is no Luban source project in that closure.

Both the concurrent read-v2 regression and the new diff-v2 regression are
preserved. Hufu's current language policy deliberately accepts only v1 and
rejects a genuine v2 document before authority lookup, evaluation or evidence
recording. Supporting v2 in Hufu is a separately scoped policy feature; it does
not require changing Luban's completed baseline.

This proves candidate-package compatibility, not public-feed adoption. Hufu's
provider, policy store and workflow/journal integration remain its own work.
See [Hufu's qualification record](../../Penghou.Hufu/docs/luban-api-consumer-qualification.md)
and the [API stability policy](api-stability.md).
