# Recall Quality Fixtures

This folder contains scripted multi-turn conversations and probe queries used by `MemoryService.Recall.Tests` to measure recall quality during iteration.

## Layout

- `conversations/*.json` — scripted conversations to ingest
- `probes/*.json` — query → expected facts; the test runs `/recall` and grades

## Conversation format (`conversations/*.json`)

```json
{
  "id": "01_employment_evolution",
  "user_id": "u-fixture-01",
  "turns": [
    {
      "session_id": "s-1",
      "timestamp": "2025-03-01T10:00:00Z",
      "messages": [
        { "role": "user", "content": "I work at Stripe as an engineer." }
      ]
    },
    {
      "session_id": "s-2",
      "timestamp": "2025-03-15T10:00:00Z",
      "messages": [
        { "role": "user", "content": "Quick update — I just joined Notion as a PM." }
      ]
    }
  ]
}
```

## Probe format (`probes/*.json`)

```json
{
  "id": "supersede_employment",
  "user_id": "u-fixture-01",
  "query": "where does the user work?",
  "session_id": "s-probe",
  "max_tokens": 512,
  "expect_substrings": ["notion", "pm"],
  "expect_absent_substrings": ["stripe"]
}
```

The grader runs `/recall` with the probe, lower-cases the resulting context, and asserts:
- every `expect_substrings` appears
- no `expect_absent_substrings` appears

Score for the fixture is `(probes passed) / (total probes)`.

## Probe categories (mapping to §9 grading)

| Category | Example probe | Tests |
|---|---|---|
| Recall quality | `where does the user work` | direct factual recall |
| Fact evolution / supersession | `where does the user work` (after change) | active fact = current, not stale |
| Multi-hop | `which city does the dog Biscuit live in` | edge traversal |
| Noise resistance | `what is the capital of Mongolia` | empty context, no hallucination |
| Opinion arc | `what does the user think about typescript` | mention multiple aspects |

## Running

```bash
dotnet test tests/MemoryService.Recall.Tests/MemoryService.Recall.Tests.csproj
```

The fixture runner is data-driven (xUnit `Theory`); each probe shows up as a separate test row with pass/fail.

## Note on LLM dependency

The recall tests in this folder require a real LLM (extraction quality is what we're measuring). Set `OPENAI_API_KEY` before running. The contract tests in `MemoryService.Contract.Tests` run with a fake LLM and don't need a key.
