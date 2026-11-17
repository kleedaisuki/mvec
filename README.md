# mvec

mvec is a project-local semantic index daemon. It watches project files,
embeds changed text in the background, and lets CLI, MCP and other clients
query one committed SQLite/Vec1 index. It does not parse code into symbols or
run agent workflows. Document summaries are opt-in and generated only on request.

## Quick start

Download the native distribution from the
[Actions run](https://github.com/kleedaisuki/mvec/actions/workflows/native.yml)
for your platform. Keep the Vec1 and SQLite libraries alongside the executable.
On Unix, restore the executable bit if your archive extractor drops it
(`chmod +x mvec`). On Windows use `mvec.exe`.

```sh
cd /path/to/project
mvec init
# Export OPENROUTER_API_KEY in the environment that starts the daemon.
mvec serve
```

From another terminal (either inside the project or using `--project DIR`):

```sh
mvec status
mvec search "Where is native IR lowering implemented?" --top-k 10
mvec summary docs/design.md --cache-only
```

Run `serve` under your existing service manager, or in a terminal and stop it
with Ctrl+C. A restart reopens the committed index and reconciles offline
changes. Use local storage: SQLite WAL uses shared memory and is not designed
for clients sharing the database over a network filesystem. Only the daemon
opens the database.

## Configuration

`mvec init` creates `.mvec/config.toml` without replacing existing settings.
Restart the daemon after editing it. Keep `.mvec/` out of version control.

| Section | Setting | Meaning |
| --- | --- | --- |
| `embedding` | `provider`, `model`, `dimensions` | Provider, document embedding model and output dimensions. The default is OpenRouter with `mongodb/voyage-code-4` (1024 dimensions). |
| | `query_model` | Optional, defaults to the document model. Distinct models are accepted only within the compatible general-purpose Voyage 4 family, at the same dimension. |
| | `route`, `key_env` | OpenRouter upstream slug (pinned). Secrets come from `OPENROUTER_API_KEY` or `VOYAGE_API_KEY`; override the variable name with `key_env`. |
| | `batch_size`, `endpoint` | Maximum inputs per embedding request. An optional HTTPS endpoint overrides the provider URL; loopback HTTP is supported for local testing. |
| `search` | `mode`, `train_min_vectors` | `exact` (default) or `ann`. An ANN project uses exact results until its first trained index is committed. |
| | `probe`, `candidates` | Fraction of ANN buckets to probe and candidate multiplier. Larger values trade CPU for recall; request `--mode exact` to bypass ANN. |
| `index` | `chunk_chars`, `max_file_bytes`, `reconcile_seconds` | Target characters per chunk, per-file byte limit, recovery scan interval. File watcher updates do not wait for this interval. |
| | `exclude` | Extra basename patterns (`*` and `?`) matched against any path component. |
| `summary` | `enabled`, `model`, `codex_path` | Off by default. Optional model and executable path for the separately authenticated Codex CLI. |
| | `change_threshold`, `max_chars` | Fraction of changed text that triggers regeneration on request, and summary input size limit. |

For direct Voyage, replace the embedding section with:

```toml
[embedding]
provider = "voyage"
model = "voyage-4-large"
query_model = "voyage-4-lite"
dimensions = 1024
batch_size = 32
```

The general-purpose Voyage 4 models share a vector space; the separately
trained `voyage-code-4` does **not** share it. Use the same code model on both
sides for code search. Changing the document model, provider, endpoint, route,
or dimensions invalidates the stored embedding space; changing only a
compatible query model does not. See [MongoDB's compatibility rules](https://www.mongodb.com/docs/vector-search/query/aggregation-stages/vector-search-stage/).

The daemon indexes UTF-8 and BOM-identified UTF-16 text, skipping binaries,
symlinks/reparse points, and the default exclusions `.mvec`, `.git`, `.hg`,
`.svn`, `.temp`, `node_modules`, `bin`, `obj`, `.env`, `.env.*`, and
`*.env`. The daemon sends the selected chunks and user queries to the configured
embedding provider. The index does not interpret `.gitignore`; configure
`index.exclude` for other paths you do not want sent. Index, cached embeddings,
and cached summary source are local project data under `.mvec/`.

### On-demand summaries

Install the [Codex CLI](https://learn.chatgpt.com/docs/developer-commands) and
run `codex login` as the user running mvec, using ChatGPT account login. Set
`[summary].enabled = true` and restart mvec. mvec invokes the official CLI with
the requested document on stdin; it does not read or store OAuth credentials.
The shell, plugins, and web search are disabled for this invocation. If Windows
has only an npm command shim on PATH, set `codex_path` to the native
`codex.exe` or put that executable on PATH.

```sh
mvec summary docs/design.md               # Generates if missing/stale
mvec summary docs/design.md --cache-only   # Never invokes Codex
```

An identical SHA-256 content version returns the cached text. Small edits
reuse the previous summary without advancing its baseline; repeated small
edits can eventually trigger regeneration. Results report both the current
`document_hash` and the version that was actually summarized (`summary_hash`).

## Clients

Use `mvec mcp --project /path/to/project` as an MCP stdio server command, with
the daemon already running. It exposes `mvec_search`, `mvec_summary` and
`mvec_status`. See the [IPC contract](docs/ipc.md) for SDKs and direct clients.

## Build and run the journey

Install .NET 10 SDK, CMake, and the platform compiler/linker for
[Native AOT](https://learn.microsoft.com/dotnet/core/deploying/native-aot/)
(Clang on Linux/macOS; Visual Studio C++ tools on Windows).
Build on the deployment OS and architecture. No CUDA/BLAS installation is
needed; Vec1 uses CPU SIMD on supported hardware.

```sh
dotnet publish src/Mvec/Mvec.csproj -c Release -o .temp/publish
dotnet run --project tests/Mvec.Journey -c Release -- "$PWD/.temp/publish/mvec" "$PWD/.temp/j"
```

On Windows pass the absolute path to `mvec.exe`. The journey runs the
published daemon and clients against loopback providers: no accounts, keys,
or paid requests are needed. GitHub Actions runs the same journey on Linux,
Windows and macOS. See [architecture notes](docs/architecture.md).
