# mvec index ownership and data flow

```text
watcher + recovery scan  -> index worker -> batched document embeddings
                                           |
                                           v
                           SQLite WAL: file version + chunks + live Vec1
                                           ^
                                           |
clients -> local IPC -> query embedding -> committed read snapshot

clients -> summary request -> versioned SQLite cache -> Responses API only on miss
```

- The daemon lock is held for the lifetime of the process. The watcher queues
  project-relative paths, not inferred mutations. A single maintenance worker
  reads their current contents; startup, watcher overflow and the timer reconcile
  against the filesystem. Directory events expand to their descendants.
- The file hash covers original bytes. Chunk hashes cover the decoded text
  actually sent to the provider. The chunk recipe is part of the per-file
  index identity and does not change the embedding cache's identity. Recheck
  file SHA-256 after the provider round trip. A result for an outdated input
  can populate the content cache, but must not replace the path's index.
- Only embeddings referenced by a committed chunk are in the searchable Vec1
  table. The historical content cache is separate so a later rename, copy or
  revert can reuse an embedding. File version, locations, and live vector
  membership change in one transaction. Historical caches currently have no
  eviction policy; deletion removes search results, not the cached contents.
- The maintenance writer and a separate long-lived read connection use
  [SQLite WAL](https://www.sqlite.org/wal.html). The query HTTP call is outside
  any transaction; the committed SQL snapshot begins after it returns. The
  dedicated read connection serializes SQLite calls, not remote requests. A
  file can change immediately after the final hash read (the filesystem and
  SQLite do not share a transaction); subsequent watcher events reconcile it.
- [Vec1](https://sqlite.org/vec1doc.html) supports packed flat exact search
  and trained inverted-file (IVF) search. mvec uses full-precision cosine
  comparison in both modes, with IVF partition pruning for ANN. The initial
  model is trained once in the background when configured and large enough,
  using a bounded sample, and changes to the vectors thereafter are incremental.
  Exact remains the default. [The Faiss vector search library](https://arxiv.org/abs/2401.08281)
  explains the same IVF recall/work trade-off and the additional loss from
  quantization. We do not quantize by default or add a separate ANN service:
  the Vec1 index and its update transactions share the authoritative database.
- The embedding space fingerprint includes provider, endpoint, upstream route,
  document model, dimensions and input formulation. Voyage's
  [document/query roles](https://www.mongodb.com/docs/voyageai/models/text-embeddings/)
  are mapped through OpenRouter's embedding API or sent in Voyage's native
  format. Distinct query models are opt-in for the documented general-purpose
  Voyage 4 space only; code models are not in that family. Embedding spaces are
  never inferred from model names just because dimension counts match.
- Summary cache keys include the summary model/profile and the original SHA-256.
  A path points to the last actual summarized baseline, not the newest version
  observed. This keeps small edits cumulative and makes it possible to return a
  cached summary by hash after a move. The writer uses the regular SQLite
  tables only. It is not invoked by the watcher.


The ChatGPT OAuth session is separate from SQLite, the embedding key, and the
summary cache. The browser return goes to a short-lived loopback listener.
[Sign in with ChatGPT registration](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
uses a per-project stable host ID and issued client ID, PKCE and OIDC signature
validation. Refresh and logout take an exclusive cross-process file lock so a
rotating refresh token cannot be consumed twice. A local atomic replacement
publishes the complete token set after renewal. Only a completed Responses
stream is eligible to write a summary to SQLite. The application's published
binary uses the official endpoints; the test harness injects a local OAuth and
Responses fixture without adding an endpoint override to the CLI or TOML.
