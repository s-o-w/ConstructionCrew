---
type: owl:ObjectProperty
label: references
comment: A relationship a note's body summarizes or draws on but never expresses as an inline wiki link. Hand-picked, not a mirror of every wiki link in the body; inline wiki links already carry the ordinary edges.
range: owl:Thing
---
# references
Cardinality: multi. Used on any note. Permanent notes may spell this key `related` in frontmatter; the VaultMeta context maps `related` to the same `vm:references` predicate as an alias, so both keys land in one edge.
