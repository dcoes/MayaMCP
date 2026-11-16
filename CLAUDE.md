# MayaMCP — project instructions

Fork of [PatrickPalmer/MayaMCP](https://github.com/PatrickPalmer/MayaMCP), pushed to
`dcoes/MayaMCP` (`origin`), upstream tracked separately (`upstream`). An MCP server that lets an
AI client drive Autodesk Maya through Maya's Command Port.

This project's own working agreements win here, including where they disagree with anything in
the workspace root `CLAUDE.md` or `studio/`.

## What this actually is

Read `README.md` § Developer Notes before touching `src/`. The mechanism, briefly: tool code is
plain Maya **Python**, sent over Maya's default Command Port — which only runs **MEL** — by
wrapping the Python in a MEL call to the Python interpreter. Multi-line Python over that port
can't return a result directly, so each tool call opens two connections: one to run the code, one
to read back `_mcp_maya_results`. Everything sent is scoped under `_mcp_maya_*` to avoid colliding
with whatever else lives in Maya's global namespace.

**Adding a tool means adding a Maya-Python file under `src/mayatools/`.** It does not require
touching the MCP server itself or writing any MCP decorators/boilerplate — the server discovers
tool signatures at startup. If a task looks like it needs server-side wiring, re-read the
Developer Notes; it probably doesn't.

`src/mayatools/thirdparty/` is for external scripts the user drops in — treat it as vendored,
don't refactor its contents into the project's own style.

## MEL — general conventions

MEL surfaces here even though this server's own tools are Python, because Maya's command port
speaks MEL, and other Maya work in this workspace (rigging/pipeline scripts) is plain `.mel`. Keep
these in mind whenever MEL is being written or debugged, not just in this repo:

- **Variables are typed and the type is part of the declaration**: `string $x;`, `int $i;`,
  `float $f[];`. There is no implicit conversion between string and numeric the way there is in
  more forgiving scripting languages — a bare `$x = 5` after `string $x;` is an error, not a
  coercion.
- **Global variables must be redeclared `global` in every scope that uses them**, including inside
  procedures, with the exact same type — mismatched types on a redeclare fail silently in ways
  that are hard to trace.
- **Procedures declared in a `.mel` file are registered globally in Maya's session once the file is
  sourced**, and persist until Maya restarts or the proc is manually deleted. Re-sourcing a file
  during iteration redefines the proc but stray old procs from earlier experiments can linger and
  shadow new work — when debugging "changes aren't taking effect," check for a stale global proc
  before assuming the file wasn't sourced.
- **Wrap scene-mutating operations in `undoInfo -openChunk` / `-closeChunk`.** Without it, a
  multi-step script leaves the user several undos deep with no single "undo my script" action —
  the kind of thing that's invisible in testing and painful in real use.
- **String quoting inside `eval()` / command strings is the most common source of MEL bugs.**
  Double-quoted strings inside a string being built for `eval` need careful escaping; prefer
  building commands with `` `...` `` (backquote/format) substitution or MEL's `format` command over
  manual string concatenation where practical.
- **`select` is stateful and additive by default** (`-add` semantics unless `-clear` is used) — a
  proc that queries or operates on "the current selection" without first clearing/restoring it can
  leak selection state into whatever the user runs next in their own Maya session.

## Security note

The command-port bridge means anything sent to Maya executes with full scene/file-system access
under Maya's process. Maya's own confirmation dialog ("Allow All") is the only gate — this server
does not sandbox tool code. Do not add a tool that shells out or touches the filesystem outside
the Maya scene without calling that out explicitly to the user first.
