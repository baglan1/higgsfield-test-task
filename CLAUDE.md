# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A Dockerized HTTP memory service for an AI agent (Higgsfield engineering challenge). Ingests conversation turns, extracts structured memories with an LLM, handles fact evolution / supersession, and answers `/recall` queries under a token budget. Built in .NET 10. Read `README.md` for the full architecture and `CHANGELOG.md` for iteration history.

## Build / test

```bash
dotnet build MemoryService.slnx              # solution uses .slnx, NOT .sln
dotnet test  MemoryService.slnx              # all tests (Testcontainers Postgres needed for most)

# single test project
dotnet test tests/MemoryService.Recall.Tests/MemoryService.Recall.Tests.csproj

# single test class or method
dotnet test --filter FullyQualifiedName~MultiHopExpanderTests
dotnet test --filter FullyQualifiedName~MultiHopExpanderTests.Hop2_score_is_seed_times_decay_squared_not_compounded

# end-to-end stack
docker compose up -d
until curl -sf http://localhost:8080/health; do sleep 1; done
```

## EF Core migrations

Migrations live in `src/MemoryService.Infrastructure/Migrations/`. The Infrastructure project hosts both the DbContext and the `IDesignTimeDbContextFactory`, so it's both `--project` AND `--startup-project` for `dotnet ef`:

```bash
dotnet ef migrations add <Name> \
  --project src/MemoryService.Infrastructure/MemoryService.Infrastructure.csproj \
  --startup-project src/MemoryService.Infrastructure/MemoryService.Infrastructure.csproj \
  --output-dir Migrations

dotnet ef migrations script \
  --project src/MemoryService.Infrastructure/MemoryService.Infrastructure.csproj \
  --startup-project src/MemoryService.Infrastructure/MemoryService.Infrastructure.csproj
```

`Database.MigrateAsync()` runs at app startup with a 30 s retry loop ([Program.cs](src/MemoryService.Api/Program.cs)).

**Important migration quirks:**
- `HasPostgresEnum<T>` MUST use `name:` named-arg (`HasPostgresEnum<MemoryType>(name: "memory_type")`) — positional arg lands in `schema`, creating a separate `memory_type.memory_type` schema instead of `public.memory_type`.
- The embedding dimension (`1536`) is baked into the migration via `MemoryDbContext.EmbeddingDim`. Switching to a non-1536 model (e.g. Ollama nomic-embed-text at 768) requires either a new migration or an `ALTER COLUMN`.
- Two pieces are emitted via raw `migrationBuilder.Sql(...)` because EF can't model them: the `tsvector` generated stored column + GIN index, and the COALESCE-based unique partial index `memories_active_triple`.

## Repo conventions (these matter)

- **Each csproj is self-contained.** No `Directory.Build.props`, no `Directory.Build.targets`, no `Directory.Packages.props`. All `TargetFramework`, `Nullable`, `ImplicitUsings`, `RootNamespace`, `NoWarn`, and every `PackageReference Version` are declared inline in the project file. Do not introduce shared MSBuild files.
- **Solution file is `.slnx`** (XML format), not `.sln`. Requires .NET 9+ SDK; `global.json` pins to `10.0.103`.
- **NU1903 is suppressed in Infrastructure and test projects.** EF Core Design pulls a transitively-vulnerable `System.Security.Cryptography.Xml`. `Microsoft.EntityFrameworkCore.Design` is marked `PrivateAssets="all"` so it doesn't ship to runtime.
- **Don't run `docker compose build` to verify changes.** A successful `dotnet build MemoryService.slnx` is the verification of record. The Dockerfile is just `dotnet publish` on the same files.

## Architecture (the parts that span files)

Five `src/` projects, with a one-way dependency tree:

```
Core        ← domain entities, MemoryType enum, MemoryServiceOptions
Infrastructure ← MemoryDbContext, EF migrations, design-time factory
Llm          ← IChatLlm, IEmbedder, lazy provider wrappers, MemoryExtractor, SupersessionJudge, prompts + JSON schemas
Recall       ← QueryRewriter, HybridRetriever, MultiHopExpander, ContextAssembler, RecallPipeline, SearchService, TokenCounter, Rrf
Api          ← Program.cs, endpoints, middleware, TurnService, DTOs
```

### LLM provider plumbing (Llm)

`IChatLlm` and `IEmbedder` are the abstractions. Three providers: `OpenAi` (default), `Anthropic`, `Ollama`. **Construction is lazy** ([LazyProviders.cs](src/MemoryService.Llm/LazyProviders.cs)) — DI resolves successfully even when no API key is configured; the actual provider client is built on first call. This is what lets `/health` and cold-session `/recall` succeed without any LLM key.

OpenAI uses **structured outputs** (`ChatResponseFormat.CreateJsonSchemaFormat(..., jsonSchemaIsStrict: true)`) when callers pass a `jsonSchema`. The schemas are defined as constants in [Prompts.cs](src/MemoryService.Llm/Extraction/Prompts.cs) (`ExtractorSchema`, `JudgeSchema`) and [QueryRewriter.cs](src/MemoryService.Recall/QueryRewriter.cs) (`Schema`). They follow OpenAI's strict-mode rules: `additionalProperties: false`, every property in `required`, nullable fields typed `["string","null"]`. Anthropic and Ollama ignore the schema (fall back to prompt-level "return JSON").

### Memory data model (Core + Infrastructure)

Every memory has subject/predicate/object/aspect/stance/text plus a vector embedding. The unique partial index `memories_active_triple` enforces "at most one active row per `(user_id, subject, predicate, aspect)`". Supersession works via:
- `active = false` on the old row
- new row with `supersedes = old.id`
- everything in one transaction in [TurnService.ApplyCandidateAsync](src/MemoryService.Api/Services/TurnService.cs)

**`aspect`/`stance` are how opinion arcs work uniformly with facts.** "I love TS" (aspect=null) and "TS generics annoy me" (aspect=generics) COEXIST — different aspects. "I love TS" → "TS is fine" on the same aspect UPDATE. One mechanism, no separate `opinion_history` table.

### `/turns` pipeline ([TurnService](src/MemoryService.Api/Services/TurnService.cs))

Synchronous with a 55 s soft deadline (eval gives 60 s):
1. Persist session + turn (one transaction)
2. Run `MemoryExtractor` LLM call → JSON candidates
3. Embed all candidates in a single batch
4. Per candidate: similar-memory lookup → `SupersessionJudge` → ADD / UPDATE / DEDUP / COEXIST
5. Apply with collision safety net (unique partial index)
6. Insert any `derived_edges` into `memory_edges`

Extraction failures are caught and logged at WARNING; the turn is still persisted.

### `/recall` pipeline ([RecallPipeline](src/MemoryService.Recall/RecallPipeline.cs))

```
cold-session check → query rewrite (gated on ≤3 words OR pronouns) → for each sub-query:
  vector + lexical retrieve → RRF fuse
cross-subquery average → multi-hop edge expansion (2 hops) → noise floor filter
→ tier-budget assembly (Microsoft.ML.Tokenizers cl100k_base) → markdown + citations
```

Tier weights: A 30% (stable user facts) / B 50% (query-relevant) / C 20% (recent turns). Spillover redistributes A → B → C.

### `/recall` vs `/search`

- `/recall` is automatic, called by the agent runtime per turn. Returns formatted markdown prose for prompt injection. Does query rewriting and multi-hop expansion.
- `/search` is an explicit agent tool call. Returns structured ranked results. No rewriting (the agent typed its query deliberately), no multi-hop. Faster path.

### Multi-hop expansion ([MultiHopExpander](src/MemoryService.Recall/MultiHopExpander.cs))

BFS over `memory_edges`, decay = `seed_score × 0.6^hop`. Two regression-tested invariants:
- Score decay does NOT compound across hops (the `frontier` carries the original seed score, not a decayed score).
- Original seeds never appear in the output, even when reachable from another seed at hop ≥ 2.

If you change the algorithm, run `tests/MemoryService.Recall.Tests/MultiHopExpanderTests.cs` — both regressions are pinned there.

## Scoring and design choices to know

- **`Confidence`** is self-reported by the LLM extractor, clamped to `[0, 1]`. It's not calibrated. Used as a Tier-A inclusion gate (≥ 0.7).
- **`MemoryEdge.Relation`** is open-vocabulary text. The multi-hop expander does NOT filter by relation name — only existence of an edge matters.
- **No EF navigation properties on `MemoryEdge`** — DB-level FK + cascade is sufficient. Edge access is set-based (`Where(e => srcIds.Contains(e.SrcMemoryId))`), not graph-walk-by-property.
- **RRF fuses by rank, not score** — vector cosine and `ts_rank` magnitudes are not comparable; using ranks sidesteps calibration.
- **Test fixtures use Testcontainers Postgres** (`pgvector/pgvector:pg16`). The Postgres container is one-per-class via xUnit `IClassFixture`, with `ResetGraphAsync()` between tests.
