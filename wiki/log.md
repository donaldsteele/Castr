# Wiki Log

Append-only chronological record of operations on the wiki. Each entry begins with `## [YYYY-MM-DD] <op> | <description>` so it's parseable with `grep "^## \[" log.md | tail -N`.

Operations:
- `ingest` — a source was processed into the wiki.
- `query` — a question was answered against the wiki (typically only logged when the answer was filed back as synthesis).
- `lint` — a health check was run.
- `schema` — the schema was modified.
- `shard` — an index was sharded.

---

## [2026-07-24] schema | Added tag taxonomy (decision, spike-result, protocol, security, platform-quirk, milestone, open-question, contested) during `/wiki:init`.

## [2026-07-24] ingest | raw/2026-07-24-castr-project-plan.md — the approved M0 project plan. Created: castr-project-plan (source), castr-project (entity), wire-protocol, repair-protocol, security-model, tech-stack (concepts), roadmap (synthesis). No typed graph edges added (no evidenced person/org actor relationships in this source; `chose`/`proposed` predicates require a person/org subject and none is naturally introduced by a design document) — mentions edges only.
   graph: +7 nodes, +0 typed edges (mentions-only)

## [2026-07-24] ingest | M0 spike results (hands-on validation + web research, not a raw file — filed directly as synthesis). Created: adr-0001-ed25519-library, adr-0002-mobile-discovery. Updated (str_replace): tech-stack.md (Open section resolved), roadmap.md (M0 status, open items). Decision consequence: solution retargeted net8.0 → net10.0 LTS; .NET 10 SDK installed via winget.

## [2026-07-24] ingest | M0 closeout: QA subagent review (no defects found across build/tests, project references, git hygiene, LICENSE, wiki consistency, ADR fact-checks, CI, GUI placeholder) and push to github.com:donaldsteele/Castr (main). Updated (str_replace): roadmap.md (M0 marked complete, open items pruned).
