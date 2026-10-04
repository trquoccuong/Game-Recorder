# Odx Platform

Behavioral guidelines to reduce common LLM coding mistakes. Merge with project-specific instructions as needed.

**Tradeoff:** These guidelines bias toward caution over speed. For trivial tasks, use judgment.

## 1. Think Before Coding

**Don't assume. Don't hide confusion. Surface tradeoffs.**

Before implementing:
- State your assumptions explicitly. If uncertain, ask.
- If multiple interpretations exist, present them - don't pick silently.
- If a simpler approach exists, say so. Push back when warranted.
- If something is unclear, stop. Name what's confusing. Ask.

## 2. Simplicity First

**Minimum code that solves the problem. Nothing speculative.**

- No features beyond what was asked.
- No abstractions for single-use code.
- No "flexibility" or "configurability" that wasn't requested.
- No error handling for impossible scenarios.
- If you write 200 lines and it could be 50, rewrite it.

Ask yourself: "Would a senior engineer say this is overcomplicated?" If yes, simplify.

## 3. Surgical Changes

**Touch only what you must. Clean up only your own mess.**

When editing existing code:
- Don't "improve" adjacent code, comments, or formatting.
- Don't refactor things that aren't broken.
- Match existing style, even if you'd do it differently.
- If you notice unrelated dead code, mention it - don't delete it.

When your changes create orphans:
- Remove imports/variables/functions that YOUR changes made unused.
- Don't remove pre-existing dead code unless asked.

The test: Every changed line should trace directly to the user's request.

## 4. Goal-Driven Execution

**Define success criteria. Loop until verified.**

Transform tasks into verifiable goals:
- "Add validation" → "Write tests for invalid inputs, then make them pass"
- "Fix the bug" → "Write a test that reproduces it, then make it pass"
- "Refactor X" → "Ensure tests pass before and after"

For multi-step tasks, state a brief plan:
```
1. [Step] → verify: [check]
2. [Step] → verify: [check]
3. [Step] → verify: [check]
```

Strong success criteria let you loop independently. Weak criteria ("make it work") require constant clarification.

AI Data Marketplace — connects Clients with Experts through Partner Orgs, with Odx orchestrating everything.

## Directory structure

```
core/
  Odx_SA_v1.5.md          # Main SA (source of truth)
  Odx_SA_v1.4_presentation.md  # Stakeholder presentation
  ADR/                        # 53 ADRs — every architecture decision
  specs/                      # 8 spec files (data model, API, workflow...)
  requirement/                # Structured requirements (REQ-NNN-slug.md)
  diagrams/                   # Mermaid diagrams
  ideal/                      # Open ideas, not yet decided
  reference/                  # Reference material
  reference/Odx_BRD_v2.0.md           # Business Requirements
  .claude/skills/             # Claude skills (create-requirement, create-adr)
```

## Working rules

1. **The SA must be clean** — no ambiguity, no "maybe", no "should". Every sentence is a clear decision.
2. **Every architecture change → create/update an ADR** — format: `ADR-XX-short-name.md`. Never change the SA without an ADR.
3. **Raw requirement → `core/requirement/`** — format: `REQ-NNN-slug.md`. Analyse raw input into a structured requirement before creating an ADR / updating the SA.
4. **Open ideas → `core/ideal/`** — if not yet decided, keep it out of the SA. Park it in `ideal/` for later discussion.
5. **Specs must stay in sync with the SA** — when updating the SA, update the corresponding spec. Keep the spec → SA back-link on the correct version.
6. **Presentation stays in sync with the SA** — `Odx_SA_v1.4_presentation.md` must match the SA after every update.

## Skills (`.claude/skills/`)

| Skill | Description | Trigger |
|-------|-------------|---------|
| `create-requirement` | Raw input → structured requirement (REQ-NNN) | User provides raw requirements to analyse |
| `create-adr` | Create an ADR with options considered, decision, impact analysis | User wants to make a new architecture decision |

## Language

- Docs/comments: English
- Code/tech terms: English
