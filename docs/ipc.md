# Local IPC contract

Given an absolute project directory, connect to:

- Unix (Linux/macOS): `<project>/.mvec/mvec.sock` (Unix domain stream socket).
- Windows: `\\.\pipe\mvec-<hash>` (byte-mode named pipe). Resolve a direct
  symlink/junction on the root if present, trim a trailing directory separator
  unless it is a volume root, uppercase that absolute path using .NET invariant
  casing, encode as UTF-8, and use the lowercase hexadecimal SHA-256 digest.
  The pipe is scoped to the daemon user.

Use a local project root. Keep Unix socket paths below the operating system's
address length limit (104 bytes including the NUL on macOS). Commands without
`--project` discover the nearest parent with `.mvec/config.toml`.

The CLI and MCP adapter activate the background daemon on demand. Clients
that use IPC directly and need a permanently available endpoint can still
run `mvec serve`. A background endpoint can disappear after idle retirement
or reject a request with `daemon_reloading` before dispatch on a config edit.

Each connection carries one UTF-8 JSON request terminated by a newline and one
UTF-8 JSON reply terminated by a newline. Open separate connections for concurrent
requests. Requests do not include a project ID; the endpoint selects the project.

## Requests

```json
{"method":"search","query":"Where is native IR lowering implemented?","top_k":10}
{"method":"search","query":"Where is native IR lowering implemented?","mode":"exact"}
{"method":"status"}
{"method":"summary","path":"docs/design.md","cache_only":true}
{"method":"summary","path":"docs/design.md"}
```

Successful replies are `{"ok":true,"result":...}`. Unsuccessful replies are
`{"ok":false,"error":{"code":"...","message":"..."}}`. Clients should branch on
`ok` and `error.code`, not parse diagnostic messages.

## Search results

```json
{
  "ok": true,
  "result": {
    "mode": "exact",
    "hits": [
      {
        "path":"src/lower.md",
        "score":0.87,
        "chunk":"The compiler lowers IR here.\n",
        "start_offset":0,
        "end_offset":29,
        "start_line":1,
        "end_line":1
      }
    ]
  }
}
```

Hits are sorted by descending cosine similarity (scores are in [-1, 1]),
one best chunk per file, with root-relative slash-separated paths. `top_k`
defaults to 10 and must be from 1 to 100. Offsets are zero-based UTF-16 code
units of decoded text (excluding BOM), with an exclusive end. Lines are
one-based and inclusive. Locations are from the committed snapshot, which can
be behind the filesystem while the daemon is updating it. ANN may return fewer
files than requested if the probed buckets/candidate limit do not contain enough
distinct paths. An untrained ANN project returns exact hits and reports
`mode:"exact"`; clients may request exact results from a trained index as well.

## Status and summaries

Status reports `files`, `chunks`, active `vectors`, current `mode`, the
native `vec1` build information, `initial_scan_complete`, `busy`,
`pending_paths` and `last_error` when present.

Summary results contain `path`, `document_hash`, `summary_hash`, `summary` and
`cache` (`exact`, `small_change`, `generated`, `superseded`, or `miss`).
A `miss` omits the summary fields. If the document changes while a requested
summary is being generated, the cached result stays associated with the
snapshot used as input. The returned current and summary hashes let the client
decide whether to request a newer summary. An absent current file omits
`document_hash`.

MCP uses newline-delimited JSON-RPC stdio with the `2025-11-25` initialize
handshake and accepts the earlier wire-compatible revisions. Tool calls forward
to the same local endpoint as the CLI.

Presentation is client-side. Interactive CLI output is formatted; redirected
CLI output and default MCP tool content retain their JSON shapes.
`mvec mcp --format compact` returns plain text inside the same MCP content
envelope. Compact search preserves rank order and inclusive source lines, but
omits scores, previews and byte/character offsets. Use JSON for automation
that needs the full result.
