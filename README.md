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
# Set OPENROUTER_API_KEY in your shell before running index commands.
mvec search "Where is native IR lowering implemented?" --top-k 10
mvec status
```

Run commands from the project (or anywhere with `--project DIR`). The first
search, status or summary request launches the project's daemon if needed.
On a new index, search waits for the first searchable data; results may still
be partial until `mvec status` reports `initial_scan_complete`. The daemon
exits after 15 minutes without client activity by default. It reopens the
committed index and reconciles offline changes on the next request.

The background process inherits the environment of the command that starts
it. Set embedding credentials in the shell or MCP client's environment
before the first index command. Diagnostics from the latest background run
are in `.mvec/daemon.log`. For continuous indexing or foreground diagnostics,
you can still run `mvec serve` explicitly. Use local storage: only the daemon
opens the SQLite database, which uses WAL shared memory.

### Output formats

Interactive `search` defaults to ranked, highlighted `path:line[-line]`
locations. Add `--format text` for scores and short previews. Redirected
output still defaults to full JSON, so existing scripts work unchanged.
Use `--format json` to request that format explicitly or `--format compact`
for plain, uncolored locations. Terminal styling honors
[`NO_COLOR`](https://no-color.org/) and `--color auto|never|always`.

## Configuration

`mvec init` creates `.mvec/config.toml` without replacing existing settings.
The next index command picks up edits automatically in background mode.
If you run `mvec serve` explicitly, restart it to apply edits. Keep `.mvec/`
out of version control.

| Section | Setting | Meaning |
| --- | --- | --- |
| `daemon` | `idle_seconds` | Optional background idle timeout, 900 seconds by default. An active request is allowed to finish. |
| `embedding` | `provider`, `model`, `dimensions` | Provider, document embedding model and output dimensions. The default is OpenRouter with `mongodb/voyage-code-4` (1024 dimensions). |
| | `query_model` | Optional, defaults to the document model. Distinct models are accepted only within the compatible general-purpose Voyage 4 family, at the same dimension. |
| | `route`, `key_env` | OpenRouter upstream slug (pinned). Secrets come from `OPENROUTER_API_KEY` or `VOYAGE_API_KEY`; override the variable name with `key_env`. |
| | `batch_size`, `endpoint` | Maximum inputs per embedding request. An optional HTTPS endpoint overrides the provider URL; loopback HTTP is supported for local testing. |
| `search` | `mode`, `train_min_vectors` | `exact` (default) or `ann`. An ANN project uses exact results until its first trained index is committed. |
| | `probe`, `candidates` | Fraction of ANN buckets to probe and candidate multiplier. Larger values trade CPU for recall; request `--mode exact` to bypass ANN. |
| `index` | `chunk_chars`, `max_file_bytes`, `reconcile_seconds` | Target characters per chunk, per-file byte limit, recovery scan interval. File watcher updates do not wait for this interval. |
| | `exclude` | Extra basename patterns (`*` and `?`) matched against any path component. |
| `summary` | `enabled`, `model` | Off by default. Set an account-available ChatGPT model slug; see `mvec models`. |
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

mvec uses [Sign in with ChatGPT](https://developers.openai.com/siwc/token-sharing-open-source)
directly. There is no Codex or OpenAI API key requirement. From the project,
log in as the OS user who runs the daemon, then list your account's models:

```sh
mvec login
mvec models
```

The browser asks you to grant mvec permission to use your ChatGPT plan. Set
the chosen model slug in `.mvec/config.toml`:

```toml
[summary]
enabled = true
model = "gpt-6.1-sol" # Choose a slug printed by mvec models.
```

```sh
mvec summary docs/design.md               # Generates if missing/stale
mvec summary docs/design.md --cache-only   # Never uses the ChatGPT session
mvec logout                              # Revoke the project session
```

The project keeps one linked account. `mvec login` reauthorizes that
registration; `mvec logout` removes its session while retaining the
registration and summary cache. Credentials under `.mvec/` are not portable:
they are user-protected on Windows and owner-only on Unix. Do not share the
project's state directory. Generation sends the requested file to OpenAI's
[Responses API](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
with the granted subscription scope. Embedding credentials are separate; there
is no fallback to bill an API key. Manage app usage in
[ChatGPT Settings → Usage](https://chatgpt.com/settings/usage).

For an SSH host, forward a fixed loopback port (e.g. `ssh -L 1455:127.0.0.1:1455 user@host`),
run `mvec login --no-browser --port 1455` on the host, and open the printed URL
on the machine that initiated the SSH session.

An identical SHA-256 content version returns the cached text. Small edits
reuse the previous summary without advancing its baseline; repeated small
edits can eventually trigger regeneration. Results report both the current
`document_hash` and the version that was actually summarized (`summary_hash`).

## Clients

Use `mvec mcp --project /path/to/project` as an MCP stdio server command.
It exposes `mvec_search`, `mvec_summary` and `mvec_status`. Tool calls start
the daemon as needed; a parked MCP connection does not keep it alive. See the
[IPC contract](docs/ipc.md) for SDKs and direct clients.
Add `--format compact` to the MCP server arguments to use plain-text tool
results (locations only for search) instead of the default JSON result body.

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
