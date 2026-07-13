# Left-Field Feature Ideas for tsky

This document lists unconventional feature ideas that still align with tsky's core purpose: launching and managing desktop tasks quickly.

Status markers:
- `[Implemented]` available in the current app.
- `[Partial]` partially implemented or implemented with a narrower scope.

## 1. Task Weather Routing
Use local weather/time signals to pick task variants automatically.

Example: on rainy weekdays, launch a quiet-focus profile; on Friday afternoons, launch reporting/cleanup tasks.

## 2. Task DNA Mutations
Allow a task to have optional "mutations" that can randomly vary arguments, environment variables, or working directories within safe constraints.

This helps with repetitive workflows and exploratory automation.

## 3. Pre-Flight Risk Scanner
Before running a task, simulate command impact and classify risk levels (safe, caution, dangerous).

Show reasons like "deletes files," "admin command," or "wide wildcard usage." 

## 4. Ritual Chains [Implemented]
Let users create themed launch rituals instead of plain task lists.

Example ritual: "Start Coding" opens IDE, starts local services, restores browser tabs, and posts a timestamped kickoff note.

## 5. Mood-Based Launcher Skins
Change task card palettes, spacing, and typography based on user-selected work mode (deep work, sprint, review, maintenance).

Visual context can reinforce intent and reduce task misclicks.

## 6. Command Replay Time Machine [Implemented]
Track task executions with exact command snapshots, environment, and output summaries.

Users can replay any historical run in one click or diff two runs to debug behavior drift.

## 7. Ambient Failure Guardian
If a task fails repeatedly, automatically suggest alternatives: previous successful command, rollback variant, or guided fix flow.

The app becomes an assistant for recovery, not just execution.

## 8. Task Marketplace Packs [Partial]
Support importable task packs for roles (frontend, ops, data science, release manager).

Users can install curated sets of tasks and adapt them locally.

## 9. "Run It For Me Later" Deferred Queue
Queue tasks to run when system conditions are met: CPU idle, VPN connected, specific Wi-Fi, or outside business hours.

Useful for heavy or noisy automation jobs.

## 10. Narrative Output Tabs [Partial]
Turn raw logs into structured stories: what happened, what changed, what failed, and what to do next.

This can summarize complex command output into human-readable run reports.

## 11. Distributed Task Swarm
Run one logical task across multiple machines (desktop, build server, home lab node) with per-node steps and aggregate logs.

Great for parallel lint/test/build phases.

## 12. Visual DAG Pipeline Composer [Partial]
Let users draw tasks as a directed acyclic graph instead of a flat list.

Each node is a task, edges define dependencies, and branch joins can gate downstream steps.

## 13. Event-Driven Task Triggers
Support triggers from external events: Git push, webhook payload, queue message, file drop, or calendar state.

Tasks become reactive automations instead of manual clicks only.

## 14. Edge-to-Cloud Task Handoff
Start a task locally, then offload heavy stages to cloud runners when CPU/memory thresholds are exceeded.

Keep the same pipeline identity and merged output stream.

## 15. Portable Task Containers
Allow tasks to execute in isolated container images with pinned dependencies.

This minimizes "works on my machine" issues for scripts and CLI tools.

## 16. Task Mesh Networking
Enable peer tsky clients to discover each other and offer shared execution capabilities over a trusted local network.

Example: route GPU workloads to a desktop with a stronger card.

## 17. Dataflow Ports Between Tasks [Implemented]
Introduce explicit input/output ports so one task can pipe artifacts, JSON, or streams directly into another.

This turns task chains into composable data pipelines.

## 18. Policy-as-Code Guardrails
Attach policy files to task groups that enforce conditions like allowed directories, forbidden commands, or max execution time.

Policies are versioned with the task definitions.

## 19. Semantic Task Router
Use intent-based routing where users type natural language ("prep release build") and tsky maps it to the best task graph.

Routing can use history, context, and confidence scoring.

## 20. Multi-Agent Task Co-Pilot
Assign specialized agents to pipeline stages (diagnostics, optimization, reporting) that collaborate during execution.

Agents can annotate logs with suggested fixes in real time.

## 21. GitOps-Synced Task Catalog
Store task and pipeline definitions in a repo, then auto-sync local launcher state from branches/environments.

This enables reviewed task changes and rollback via Git history.

## 22. Chaos Mode for Pipelines
Inject controlled failures, latency, and missing dependencies into selected pipeline stages.

Use it to test resilience and recovery playbooks.

## 23. Cost-Aware Execution Planner
Before running distributed pipelines, estimate compute/network cost and suggest a cheaper execution plan.

Users can choose "fastest," "cheapest," or "balanced" strategies.

## 24. Time-Travel Debug Pipelines [Partial]
Capture deterministic snapshots of task inputs and environment so any failed stage can be replayed exactly.

Ideal for debugging flaky automation.

## 25. Federated Secret Contexts
Reference secrets from multiple providers (Windows Credential Manager, Vault, cloud key stores) through unified aliases.

No raw secret values appear in task definitions.

## 26. Human-in-the-Loop Gates
Insert approval checkpoints in pipelines where a user verifies output before downstream tasks continue.

Useful for deploy, cleanup, or destructive actions.

## 27. Knowledge-Graph Task Discovery
Build a graph linking tasks, tools, repos, outputs, and owners.

Users can ask "what tasks affect release artifacts?" and navigate connected nodes.

## 28. Offline-First Sync and Conflict Merge
Let multiple devices edit task catalogs offline, then reconcile changes with semantic conflict resolution.

Merges can understand DAG structure, not just text diffs.

## 29. Live Pipeline Telemetry Overlay
Expose distributed run metrics (queue time, execution time, retries, resource usage) directly on cards and edges.

This makes bottlenecks obvious at a glance.

## 30. Task Contract Testing
Define contracts for task outputs (schema, file signatures, exit semantics) and validate them automatically between connected tasks.

Pipelines fail early when upstream contracts break.
