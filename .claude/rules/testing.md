---
paths:
  - "tests/**"
---

# Testing rules

- Pick the cheapest layer that can prove the behaviour: Domain (pure) → Application (fake ports) →
  Integration (Testcontainers). Do not reach for Docker to test a pure function.
- Name tests `Method_State_ExpectedResult` or as a sentence (`CannotReadOtherTenantData`).
- Integration tests share one container per collection (`[Collection(PostgresCollection.Name)]`) and
  must not depend on each other's data: seed with unique tenants.
- Never call the real Claude API from unit or integration tests. Use a fake `IChatClient`; real model
  calls belong to the evals project only.
- A rule-like test (architecture, tenant isolation, guardrail) must be proven able to fail once.
