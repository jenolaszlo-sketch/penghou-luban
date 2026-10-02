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
| IO.Abstractions + IO.Protocols → Luban | Exact `[0.1.0-preview.1]` package references; normal builds have no sibling source dependency | IO publishes its qualified candidates; Luban verifies public-feed restore |
| IO.Local → host/sample/tests | Concrete reader/writer stays outside Luban core | Each host explicitly selects its provider and controlled write namespace |
| Luban → Hufu integration | Constructor injection and optional v2 requests require consumer qualification; current Hufu uses a source project reference | Hufu replaces the reference with the qualified Luban package and runs its integration matrix |
| Luban → other hosts | Required authorization/start/outcome contracts stay provider/policy neutral | The host qualifies its selected profiles; no generic ecosystem-wide adoption is claimed |

TextPatch/PatchLimits/FilePatchRequest and UTF-8 patch materialization now belong
in the Luban assembly. Their temporary namespace remains `Penghou.IO.Abstractions`
for this coordinated, unpublished migration. Do not combine these candidates
with an older IO assembly containing duplicate patch types. Future namespace/API
changes require a versioned migration entry here.

Complete release work in order: publish qualified IO packages, qualify and publish
Luban from the public dependency feed, then adopt it in Hufu. The initial Luban
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
is being preserved by this preview. Before a stable package, generate and review
a public API baseline, check compatibility across previews, and document any
enum additions and source changes. In particular, append-only enum evolution
does not guarantee that downstream exhaustive switches behave safely.

## Hufu as an optional consumer

The inspected Hufu tree currently uses a project reference to Luban for its
integration project; it has not adopted this package. Its read integration
composes a Local provider and passes it to Luban runtimes. When Hufu chooses to
consume the published package, its migration is to replace that project
reference with an exact `Penghou.Luban` package version while retaining explicit
provider and authority composition. Its Hufu-local Local provider/package use
remains separate from Luban's core package.

Hufu should then run its own integration tests against the package, including
the current `LanguageRuntime` composition call sites and authorization
semantics. Its policy can continue consuming the v1 profile while separately
deciding whether to support opt-in v2 requests. Hufu, its policy store, and
Zhinu durability are not prerequisites for Luban's leaf package release.
