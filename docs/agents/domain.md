# Domain Docs

How engineering skills consume this repository's domain documentation.

## Before exploring, read these

- `CONTEXT.md` at the repository root.
- `CONTEXT-MAP.md` if it exists; it points to context-specific `CONTEXT.md` files.
- Relevant ADRs under `docs/adr/`.
- In a multi-context repository, relevant ADRs under `src/<context>/docs/adr/`.

If these files do not exist, proceed silently. Create them only when domain terms or architectural decisions are actually resolved.

## File structure

This repository uses a single-context layout:

```
/
|-- CONTEXT.md
|-- docs/
|   `-- adr/
`-- src/
```

## Use the glossary's vocabulary

When naming domain concepts in issues, proposals, hypotheses, or tests, use the terminology defined in `CONTEXT.md`.

If a required concept is absent, reconsider whether the term belongs to the project or record the gap for `/domain-modeling`.

## Flag ADR conflicts

If proposed work contradicts an existing ADR, surface the conflict explicitly instead of silently overriding the decision.
