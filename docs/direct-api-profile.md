# Direct read API authorization profile

The direct `FileEffectRuntime` supports Windows Read/Find/SearchText through the
shared Local reader. For new AI tool integration, use `LanguageToolRuntime` and
its typed language, capability description and JSON result envelope. The direct
API preserves its existing requests/results and recursive basename-pattern
semantics, with explicit authorization phases and bounded release checks.

## Host contract

`IEffectAuthorizer` remains required; Hufu is optional. Every
`EffectAuthorizationRequest` binds invocation, effect kind, semantic request
digest, workspace and path. Its additive fields identify:

- `Phase`: Admission, ResourceAccess, or Release.
- `Action`: the concrete metadata, listing or content action when applicable.
- `ResourceRequestIdentity`: the exact concrete shared-provider request identity
  for resource checks.

Admission and whole-operation Release use the original selector and no concrete
action/identity. ResourceAccess and per-resource Release retain the concrete
action and request identity. A policy should use these fields rather than infer
the action from callback order. Existing authorizers that consider only effect
kind/path remain source compatible, but need to use the new fields to express
separate metadata/content/release rights.

Successful results are withheld until whole-operation Release and every
permitted resource dependency pass. Private dependencies include metadata,
listings and reads, including no-match/invalid-text observations and upstream
metadata that contributed to partial results. They are not published to the
caller. Unknown/throwing/unavailable decisions fail closed; no partial value is
returned on release denial or failure.

## Bounds and cancellation

`FileEffectRuntimeOptions` defaults to a 30-second operation timeout and 100,000
release dependencies. Hosts can lower both within those ceilings. The deadline
covers admission, provider work and final release, and cancellation-aware waits
prevent a non-cooperating authorizer from keeping the tool call pending forever.
Caller cancellation propagates; deadline expiry returns `DeadlineExceeded` with
no value. An abandoned authorizer call is not used as permission if it finishes
later. These are cooperative runtime/I/O bounds, not hard interruption of host code.

Invocation IDs and workspace IDs require valid Unicode scalars, at most 256
UTF-16 code units and at most 1024 strict UTF-8 bytes, without control characters.
Invalid invocation IDs are rejected before authorization or target I/O.

Oversized individual search files are skipped and mark results truncated;
aggregate scan/match/output limits still stop the search. Denied reads are
omitted from the authorized view. No complete filesystem coverage or snapshot
is implied by a successful result.

The direct API and language API retain distinct request digest formats and
pattern semantics. The current changes do not replace captured plan admission,
provide a mutation host, or add arbitrary process execution. See the
[shared I/O profile](shared-io-integration.md) for provider guarantees.
