# opTImus development

This is an independent Windows application, not a component of REA. Read README.md and docs/architecture.md before changing contracts.

- Keep Domain independent of Windows, UI, databases and inference engines. Both Desktop and CLI use the same Application workflows.
- Preserve observed facts, source IDs, paths, units, timestamps and explicit unknowns. Consumers must not parse display strings. Partial or unavailable collection is not a health conclusion.
- The current 0.9 scope adds instability investigation, bounded restart/memory events, hardware/firmware inventory, explicit MemTest86 HTML import, selected CDB dump analysis and explicit official-page GET. Follow docs/scope-0.9.md plus the previous scopes and shared tool-runner contract. Bind CLI/UI queries to named contracts; catalog order must never select an operation. Windows integrity verification requires an already elevated context; the app does not elevate itself. Process/connection termination, task/service/protection changes, cleaning, repair, update installation and driver installation remain disabled.
- No generic shell/script tool for the assistant. No automatic cleaning, repair, driver installation, disabling Windows protection or external data upload.
- Configuration/setup must show exact paths first and be additive. Use an isolated development SDK/cache under .tools; never change global PATH or install unrelated software.
- Preserve existing reports and unknown databases. Bind SQLite identity and schema before writing. Do not replace sessions or silently downgrade schemas.
- Only clean resources demonstrably created by the operation; retain failed cleanup paths and primary errors. Publish new files without overwriting existing destinations.
- Do not commit .tools, build output, local diagnostic data, models, dumps, credentials, certificates or local planning artifacts. Do not select a license for the user's code without their decision.
- Use scripts/dev.ps1 for restore/build/test/run. Check SDK in global.json and package locks. Relevant integration tests are preferred to redundant unit tests.
- Record checks actually run and their limits in docs/validation.md. A fixture is not a physical-provider test, local publishing is not a signed installer, and local code is not a remote GitHub update.
- Use Conventional Commit subjects when commits are requested. Do not infer push, public release or deployment authority from a local implementation request.
