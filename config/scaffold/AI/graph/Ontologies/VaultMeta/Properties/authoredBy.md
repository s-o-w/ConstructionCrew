---
type: owl:DatatypeProperty
label: authored by
comment: Which role/persona actually wrote this note - GC, a named Foreman, a Worker, or the Boss directly. Free-text identifier ("GC", "Foreman:<Jobsite>", "Worker:<Jobsite>/worker-<id>", "Boss"), not a linked concept, since these are agent instances rather than a fixed taxonomy.
range: xsd:string
---
# authoredBy
Cardinality: single. Used on any note a ConstructionCrew role writes into the Vault.
