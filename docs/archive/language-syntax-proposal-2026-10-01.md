Luban Language Syntax Specification
Status: Proposed
Project: Penghou.Luban
Scope: Surface language syntax only
Related: Hufu authority model, Fuwen workflows, Zhinu execution
Design goal: A concise, familiar, shell-like language for expressing bounded typed effects without becoming a general-purpose shell.
1. Purpose
Luban provides a compact textual syntax for common agent operations such as:
ls src
cat src/Foo.cs
fd src "**/*.cs"
grep "Authority" src
patch src/Foo.cs ...
git st
git diff

The syntax is intended primarily for agents, but should remain understandable and convenient for humans.
Luban source does not execute directly.
Luban source
    ↓
parser
    ↓
typed Luban IR
    ↓
validation
    ↓
Hufu authority evaluation
    ↓
effect execution

Aliases such as cat, ls, rm, and grep are syntax sugar. They MUST compile to the same canonical typed operations as their long-form equivalents.
The language should feel familiar to users of PowerShell, Bash and command-line tools while deliberately excluding arbitrary shell execution.
2. Core principles
Concise
Common operations should require very little syntax.
Preferred:
cat src/Foo.cs 100:160

rather than:
Files.ReadRange {
    path: "src/Foo.cs",
    startLine: 100,
    endLine: 160
}

Both compile to the same IR.
Familiar
Use conventions already understood by developers and models:
|
ls
cat
grep
rm
mv
cp
*
**
--option

Do not invent unusual syntax where an established convention is sufficient.
Typed underneath
Pipelines transport structured values, not stdout strings.
fd src "**/*.cs"

produces conceptually:
Stream<FileRef>

and:
fd src "**/*.cs" | grep "Authority"

becomes:
Stream<FileRef>
    → SearchText
    → Stream<SearchMatch>

Finite effects
The language can invoke only registered Luban effects.
Unknown commands fail.
pwsh ...
bash ...
cmd ...
python ...

do not fall through to an operating system shell.
No hidden escalation
Syntax cannot create authority.
Every effect compiles to an authority requirement known before execution.
Control flow stays elsewhere
Luban is not a replacement for Fuwen.
Workflow branching, durable loops, retries and fan-out belong to Fuwen/Zhinu.
3. Command form
The basic syntax is:
command [arguments] [options]

Examples:
ls src

cat src/Foo.cs

fd src "**/*.cs"

grep "Authority" src

rm src/Old.cs

mv src/A.cs src/B.cs

Commands SHOULD have a canonical name and MAY have aliases.
For example:
list    alias ls
read    alias cat
find    alias fd
search  alias grep
delete  alias rm
copy    alias cp
move    alias mv

The canonical operation represented in the IR is independent of the alias used.
Thus:
cat src/Foo.cs

and:
read src/Foo.cs

produce identical IR.
4. Pipelines
The pipe operator is:
|

Example:
fd src "**/*.cs" | grep "Authority"

Unlike a traditional shell, Luban pipelines do not pass arbitrary text.
Each command has defined input and output types.
Conceptually:
fd
    : Directory → Stream<FileRef>

grep
    : Stream<FileRef> → Stream<SearchMatch>

take
    : Stream<T> → Stream<T>

Therefore:
fd src "**/*.cs"
    | grep "Authority"
    | take 20

is statically understandable before execution.
Invalid pipelines should fail during validation.
For example:
git st | patch Foo.cs

should fail if patch cannot consume the preceding result type.
5. Pipeline formatting
Both single-line and multiline forms are valid.
fd src "**/*.cs" | grep "Authority" | take 20

and:
fd src "**/*.cs"
    | grep "Authority"
    | take 20

are equivalent.
A leading | on continuation lines is recommended because it keeps long pipelines readable.
6. Paths
Relative paths are preferred.
cat src/Foo.cs

The current workspace is resolved by the host.
Explicit workspace references MAY later be supported:
cat workspace.main:src/Foo.cs

but the common case should stay concise.
Paths containing whitespace require quotes:
cat "src/My Component/Foo.cs"

Both / and platform-native separators may be accepted at the parser boundary, but paths SHOULD be normalized into a host-independent IR representation.
For example:
src\Foo.cs

and:
src/Foo.cs

may normalize to the same logical workspace path.
7. Globs
Luban supports standard glob-style patterns:
*
**
?

Examples:
fd src "*.cs"

fd src "**/*.cs"

fd . "**/*Tests.cs"

Globs are data passed to the effect implementation. They are not shell expansion.
This distinction matters.
The shell does not expand:
**/*.cs

before Luban receives it.
Luban itself owns the glob semantics.
8. Quoting
Double quotes are the normal string syntax:
grep "Authority Envelope" src

Single quotes MAY also be supported for literals where escaping behavior differs:
grep 'Authority "Envelope"' src

The grammar should avoid implementing shell-style interpolation.
For V1:
"$HOME/foo"

is simply text.
There is no environment-variable expansion.
9. Reading files
Entire file:
cat src/Foo.cs

Line range:
cat src/Foo.cs 100:160

From a line to end:
cat src/Foo.cs 100:

Beginning through a line:
cat src/Foo.cs :160

A single line MAY use:
cat src/Foo.cs 120

Conceptually:
100:160

means a source range, not string parsing performed by the filesystem handler.
The parser creates a typed range.
10. Directory listing
Basic:
ls

or:
ls src

Options could include:
ls src --files
ls src --dirs
ls src --hidden

Recursive traversal should not be implied by ls.
Use fd for recursive discovery.
This avoids accidental massive results.
11. Finding files
Basic:
fd src "**/*.cs"

From the current workspace:
fd "**/*.cs"

Useful options:
fd src "**/*.cs" --max 100

fd src "**/*" --type file

fd src "**/*" --type dir

Result type:
Stream<FileRef>

A FileRef should contain structured metadata rather than only a string path.
Conceptually:
path
name
type
size?
modified?

12. Searching content
Direct search:
grep "Authority" src

With file selection:
grep "Authority" src --include "**/*.cs"

Pipeline form:
fd src "**/*.cs" | grep "Authority"

Useful options:
--case-sensitive
--ignore-case
--regex
--max 50
--before 2
--after 2

Default search mode SHOULD be literal text, not regular expression.
Regex must be explicitly requested:
grep "Authority[A-Za-z]+" src --regex

This avoids accidentally interpreting model-generated text as executable pattern syntax.
13. Search result type
grep returns structured matches:
SearchMatch {
    path
    line
    column
    text
    before[]
    after[]
}

Therefore:
grep "TODO" src | take 10

does not require parsing lines of console output.
14. Pure pipeline transformations
Luban MAY provide a small collection of pure transformations.
Initial candidates:
take
skip
where
select
sort
unique
count

These do not perform external effects.
Examples:
fd src "**/*.cs" | take 20

fd src "**/*.cs" | where size > 10kb

fd src "**/*.cs" | select path

grep "TODO" src | select path | unique

These operate entirely over structured values already produced by effects.
15. where
where supports a deliberately small expression language.
Examples:
fd src "**/*" | where size > 1mb

grep "TODO" src | where line > 100

git st | where state == modified

Initial operators:
==
!=
>
>=
<
<=
contains
starts-with
ends-with

Boolean composition may later support:
and
or
not

The expression language MUST remain pure.
It cannot invoke effects.
16. % alias
% MAY be provided, but it should not mean arbitrary ForEach-Object execution as it does in PowerShell.
It may instead be an alias for projection:
fd src "**/*.cs" | % path

equivalent to:
fd src "**/*.cs" | select path

Or:
grep "TODO" src | % { path, line }

if a safe record projection syntax is introduced.
What MUST NOT be allowed:
fd src "**/*.cs" | % {
    rm $_
}

Effects inside arbitrary iterator bodies would turn Luban into a scripting language.
Fan-out belongs to Fuwen.
17. File creation and replacement
Create or replace:
write src/New.cs {
    ...
}

The body is literal file content.
For example:
write src/Hello.cs {
namespace Demo;

public sealed class Hello
{
}
}

For large or delimiter-sensitive content, an explicit heredoc-style syntax may be preferable later.
Example:
write src/Hello.cs <<'EOF'
namespace Demo;

public sealed class Hello
{
}
EOF

The chosen syntax MUST avoid accidental interpolation.
18. Patching
Patching should be a first-class operation.
Possible syntax:
patch src/Foo.cs --hash abc123 {
    ...
}

The body represents a supported patch format.
For example:
patch src/Foo.cs --hash abc123 {
@@ -42,3 +42,3 @@
-old value
+new value
}

The important semantic property is:
expectedHash = abc123

If the file has changed:
PreconditionFailed

No partial write occurs.
The --hash requirement may later be automatically supplied from earlier reads by workflow tooling, but it remains part of the typed IR.
19. Text replacement
For simple replacements:
replace src/Foo.cs "old" "new"

Optional expected count:
replace src/Foo.cs "old" "new" --count 1

This should fail rather than silently replacing an unexpected number of occurrences when --count is specified.
This makes agent edits safer.
20. Copy, move and delete
cp src/A.cs src/B.cs

mv src/A.cs src/B.cs

rm src/Old.cs

Recursive deletion should require explicit syntax:
rm old-folder --recursive

or:
rm old-folder -r

There should be no implicit recursive delete of a directory.
A host policy may prohibit recursive deletion entirely.
21. Directories
Create:
mkdir src/NewFolder

Potential remove:
rmdir src/OldFolder

rmdir should initially require the directory to be empty.
Recursive deletion belongs to the explicitly dangerous rm -r form if supported.
22. Git command namespace
Git should use a namespace:
git st
git diff
git log
git show
git branches

Canonical equivalents:
git status
git diff
git log
git show
git branches

Mutation examples later:
git add src/Foo.cs
git commit "Implement authority checks"
git checkout feature/foo

Remote effects:
git fetch
git pull
git push

must remain separately identifiable in IR and Hufu authority.
git in Luban does not mean "execute the installed Git CLI with the following string."
It maps to typed Git effects.
23. Developer tool namespaces
The same pattern may eventually support:
dotnet build
dotnet test
dotnet restore

These are still typed commands.
For example:
dotnet test tests/Hufu.Tests.csproj --configuration Release

compiles into something conceptually like:
DotNet.Test {
    project = ...
    configuration = Release
}

not:
Process.Run("dotnet test ...")

Unknown dotnet subcommands should be rejected unless explicitly registered.
24. Options
Long options use:
--name value

or for flags:
--hidden

Selected common short aliases MAY exist:
-r
-n
-i

but they should be limited.
Luban should favor readability over reproducing every Unix CLI abbreviation.
For example:
grep "foo" src --ignore-case --max 20

is clearer to an agent and easier to audit than a dense cluster of short flags.
25. Named arguments
Named arguments MAY later support:
grep query="Authority" root=src max=20

but positional arguments should remain the normal compact form for obvious commands.
Preferred:
grep "Authority" src --max 20

rather than:
grep query="Authority" root="src" max=20

The language exists partly to reduce token overhead.
26. Literals
Initial literal types:
string
integer
boolean
size
duration
path
range
glob

Examples:
20
true
false
10kb
1mb
30s
5m
100:160
"**/*.cs"

The parser should produce typed values rather than leaving all arguments as strings.
27. Comments
Use #:
# Inspect the authority implementation
fd src "**/*.cs" | grep "Authority"

Inline comments may be allowed:
git st   # inspect working tree

Comments never enter effect arguments.
28. Statement separator
Newline is the preferred statement separator.
Example:
git st
git diff

Semicolon MAY be supported:
git st; git diff

but semicolon does not imply shell semantics.
Each statement is independently parsed into Luban operations.
&& and || should not exist initially.
Conditional execution belongs to Fuwen.
29. No shell operators
The following MUST NOT have traditional shell meaning:
>
>>
<
2>
&&
||
&
$()
`...`

For example:
cat Foo.cs > Copy.cs

should be invalid.
The explicit equivalent is:
cp Foo.cs Copy.cs

or a typed write pipeline if one is introduced later.
This prevents hidden effects from appearing through shell syntax.
30. No environment expansion
These should not expand:
$HOME
%USERPROFILE%
${TOKEN}

If environment access is eventually required, it must be explicit:
env get DOTNET_ROOT

and separately authorized where appropriate.
This prevents credentials or host state from leaking simply because a model generated familiar shell syntax.
31. No command substitution
Forbidden:
cat $(fd ...)

or:
cat `find ...`

Structured pipelines should provide the necessary composition.
For example:
fd src "**/*.cs" | grep "Authority"

No subprocess-style substitution is needed.
32. Variables
General mutable variables SHOULD NOT exist in the initial language.
Avoid:
$x = fd ...

because this begins turning Luban into a general scripting language.
Pipeline values provide short-lived composition.
Durable values belong to Fuwen workflow state.
A later immutable binding syntax could be considered only if strong use cases emerge.
33. Loops
Luban MUST NOT initially provide general loops.
Forbidden:
for ...
while ...
foreach ...

and:
% { effect }

Instead:
Luban:
    fd src "**/*.cs"

Fuwen:
    fan-out over files

Luban:
    process one file

This preserves durable orchestration and bounded authority.
34. Conditionals
General:
if
else
switch

should not initially exist.
Pure filtering is allowed:
| where ...

Workflow decisions belong to Fuwen.
This distinction should remain explicit:
where
    filters data

if
    changes workflow control flow

Only the first belongs in Luban.
35. Effect composition versus workflow composition
Valid Luban:
fd src "**/*.cs"
    | grep "TODO"
    | take 20

This is a single structured dataflow.
Not Luban:
for every TODO:
    ask model to fix it
    run tests
    retry three times

That is a Fuwen workflow.
A useful rule is:
Luban composes data and bounded effects. Fuwen composes work.

36. Multiple effects in one script
A Luban document MAY contain several statements:
git st
fd src "**/*.cs" | grep "Authority"
cat src/Hufu.cs 100:180

However, each statement remains individually representable in the IR.
The parser does not turn the entire document into an opaque script effect.
This is important for Hufu.
Authority can be calculated for each individual effect before execution.
37. Parse before execute
The entire submitted Luban statement or document SHOULD be parsed and validated before the first effect executes.
For example:
cat src/Foo.cs
unknown-command blah
rm src/Old.cs

should preferably fail validation before reading Foo.cs.
This prevents partially executing malformed agent output.
Hosts MAY support streaming/interactive execution later, but strict workflow execution should use parse-first semantics.
38. Authority extraction
Every parsed effect has a deterministic authority projection.
Example:
cat src/Foo.cs

becomes:
Files.Read(
    workspace.main,
    src/Foo.cs)

and therefore:
requires workspace.read:
    workspace.main/src/Foo.cs

Likewise:
rm src/Foo.cs

requires:
workspace.delete:
    workspace.main/src/Foo.cs

Aliases cannot affect authority semantics.
39. Pipelines and authority
Pure transformations add no external authority.
For example:
fd src "**/*.cs"
    | where size > 1kb
    | select path
    | take 20

requires only the authority needed by fd.
But:
fd src "**/*.cs"
    | grep "Authority"

requires both directory traversal and file-content reading.
The compiler can derive the complete requirement before execution.
40. Error syntax and model feedback
Errors should be structured but can render concisely.
Example:
Error LBN1004: unknown command 'pwsh'

with machine-readable details:
code: UnknownCommand
command: pwsh

Authority failure:
Error HUF2003: write denied: src/secrets/key.txt

Precondition failure:
Error LBN3011: src/Foo.cs changed since it was read
expected: abc123
actual:   def456

The human-readable representation should be useful to a model without requiring it to interpret arbitrary stderr.
41. Unknown command handling
There is no executable fallback.
This:
curl https://example.com

does not invoke system curl unless curl is explicitly a registered Luban command.
Unknown command:
UnknownCommand("curl")

A future typed HTTP effect might instead use:
http get https://example.com

with explicit authority semantics.
42. Namespaces
Commands should use namespaces where collisions or conceptual grouping help:
git status
git diff

dotnet build
dotnet test

http get
http download

Filesystem commands remain top-level because they dominate normal agent interaction:
ls
cat
fd
grep
patch
rm
mv
cp

Potential canonical forms could still internally be:
files.list
files.read
files.find
files.search

without forcing users to type them.
43. Canonical form
Every valid Luban expression SHOULD have a canonical representation.
For example:
cat src\Foo.cs 0100:0160

could canonicalize to:
read src/Foo.cs 100:160

Canonical form is useful for:
hashing
audit evidence
workflow identity
replay
testing
equivalence

The original source MAY still be retained for diagnostics.
44. IR independence
The syntax MUST NOT become the durable execution format.
Store or hash the typed IR/canonical semantic representation where execution identity matters.
This protects durable workflows from cosmetic syntax changes such as:
cat

versus:
read

Aliases should not create different workflow semantics.
45. Grammar sketch
A rough initial grammar could look like:
document
    := statement*

statement
    := pipeline newline?

pipeline
    := invocation ('|' transform_or_invocation)*

invocation
    := command argument* option*

command
    := identifier
     | identifier identifier

argument
    := string
     | number
     | boolean
     | path
     | range
     | glob
     | block

option
    := '--' identifier argument?
     | short_option

range
    := integer
     | integer? ':' integer?

block
    := '{' block_content '}'

transform_or_invocation
    := invocation

The actual parser should distinguish typed commands from generic identifiers using the command catalogue.
46. Command catalogue
Syntax recognition and command semantics should be driven by a trusted catalogue.
Conceptually:
CommandDescriptor
    canonicalName
    aliases[]
    inputType
    outputType
    arguments[]
    options[]
    effectKind
    authorityMapper

Example:
canonical:
    files.read

aliases:
    read
    cat

input:
    None

arguments:
    path
    range?

output:
    FileContent

This makes the language extensible without creating arbitrary execution.
47. Extension rules
Third-party effects MAY register additional Luban commands.
For example:
neo4j query ...

or:
nuget search ...

but registration must include:
typed arguments
typed results
authority mapping
risk classification
validation rules

A plugin cannot register:
exec-anything <string>

while claiming to be a bounded effect.
Hosts remain free to reject extension commands.
48. Versioning
A Luban document or compiled form should carry a language version.
Potential source directive:
#!luban 1

This MAY be omitted for interactive usage where the host supplies the version.
Durable workflows must know the syntax/semantic version under which the source was compiled.
Breaking aliases or semantics require a language version change.
49. Suggested V1 syntax
The first useful language surface could be deliberately small:
ls [path]

fd [root] pattern

cat path [range]

grep query [root]
    [--include pattern]
    [--exclude pattern]
    [--max n]
    [--regex]

stat path
exists path

write path { content }

patch path [--hash value] { patch }

replace path old new [--count n]

cp source destination
mv source destination
rm path
mkdir path

git st
git diff

take n
skip n
where expression
select fields
sort field
unique
count

Pipeline support:
fd src "**/*.cs"
    | grep "Authority"
    | take 20

That is already enough to replace a substantial amount of PowerShell/Bash usage by coding agents.
50. Explicit V1 exclusions
The following syntax should intentionally produce errors:
pwsh ...
powershell ...
bash ...
cmd ...
sh ...

exec ...
run ...

for ...
foreach ...
while ...

if ...
else ...

&&
||
$()
>

eval ...

Not because these concepts can never exist anywhere in Penghou, but because they violate Luban's initial execution model.
51. Example session
A model needs to understand Hufu authority handling.
It can issue:
fd src "**/*.cs" | grep "AuthorityEnvelope" | take 20

Then:
cat src/Hufu/AuthorityEnvelope.cs

Then:
cat src/Hufu/AuthorityEvaluator.cs 80:180

After deciding on a modification:
patch src/Hufu/AuthorityEvaluator.cs --hash f35a17 {
@@ ...
}

Then inspect:
git diff

No PowerShell.
No shell quoting.
No output parsing.
No arbitrary process execution.
Every external effect was known before execution.
52. Example with Fuwen
Luban should not try to express:
find every failing file, modify each concurrently,
then review and retry failures

Instead:
Fuwen
    ↓
Luban: fd src "**/*.cs"
    ↓
Zhinu FanOut
    ↓
activity
    ↓
Luban: cat ...
    ↓
model decision
    ↓
Luban: patch ...

This is the intended separation.
53. Design rule
The most important syntax rule should be:
Luban may be concise like a shell, but every expression must remain statically reducible to a finite set of typed effects and pure transformations.

If a proposed language feature makes it impossible to determine what kinds of external effects may happen before execution, it probably does not belong in Luban.
That is the line that keeps:
fd src "**/*.cs" | grep "Authority" | take 10

while rejecting the gradual slide toward:
foreach (...) {
    Invoke-Expression(...)
}

The visual familiarity is useful. The unrestricted semantics are not.