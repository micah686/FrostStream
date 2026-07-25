# Media access — Phase 8 verification

Phase 8 closes the media-access rollout with executable coverage and final documentation.

- `AccessPolicyDenyEvaluatorTests` verifies each deny axis, inclusive age thresholds, multi-policy
  unioning, bypass behavior, unrated media, and policy validation.
- `OpenFgaAxis1FlowTests` runs the production OpenFGA provisioner and services against a live
  OpenFGA container. It covers policy-to-bundle grants, effective endpoint discovery, scoped
  endpoint denial, and tuple removal when a policy is deleted.
- `WatchStateConsumerServiceTests` uses migrated PostgreSQL to verify persisted policy evaluation
  across media GUID, provider, and age axes, plus clean removal of all deny behavior after policy
  deletion.
- Legacy admin Svelte routes redirect to `/admin/access-control`; the old access-control API
  surface is omitted entirely in single-user mode and verified by the WebAPI HTTP integration test.

`MEDIA_ACCESS.MD` is the canonical design document for this feature. The phase notes record
implementation detail only.
