# LLM Dataflow Task Type Technical Spec

Status: Draft
Revision: 0.1
Date: 2026-11-02
Scope: Add a first-class LLM task type that participates in Tsky task dataflow as a structured-output producer.

## 1. Executive Summary

This spec introduces a new first-class task type for LLM-backed, schema-constrained data transformation in task chains and group flows.

The intent is to treat the LLM as a data-plane participant, not a control-plane orchestrator:

- The task graph, execution order, and scheduling remain under existing Tsky control flow.
- The LLM receives resolved task inputs and returns structured JSON constrained by an output schema.
- The validated JSON is mapped to Tsky output ports and then consumed by downstream tasks through existing dataflow bindings.

This replaces ad hoc script-based LLM calls with a consistent, typed, import/export-friendly task model.

## 2. Problem Statement

Current behavior supports command-oriented tasks (exe, cmd, powershell, python) where AI integration is typically done via custom scripts.

Gaps:

- No first-class task semantics for LLM invocation.
- No standardized output schema contract for model responses.
- No built-in mapping from model response fields to output ports.
- No shared validation/error semantics for structured AI outputs.

Consequence: AI-assisted decision steps are possible, but they are fragile, script-specific, and not natively represented in task configuration.

## 3. Goals

- Add a first-class task type for LLM calls within the existing task flow engine.
- Keep existing control plane unchanged (selection, run-chain, scheduler, history, remote dispatch model boundaries).
- Make output constraints primary through a required response schema.
- Support deterministic dataflow publication by validating and mapping model output into task outputs.
- Preserve import/export and validation behavior using explicit schema fields in tasks.json.
- Keep tools capability separate from output schema so flow logic stays data-driven.

## 4. Non-Goals (v1)

- No autonomous planning or task graph mutation by the model.
- No model-driven tool execution protocol in v1.
- No replacement of existing command task type.
- No requirement to support every provider-specific advanced feature.
- No mandatory remote-agent execution path for LLM tasks in v1 (local desktop execution is primary).

## 5. Conceptual Model

### 5.1 Control Plane vs Data Plane

Control plane (existing):

- User actions, task selection, chain execution order, scheduler policy, retries, cancellation, and run history.

Data plane (new LLM task behavior):

- Consume resolved inputs.
- Produce schema-validated structured output.
- Publish output ports for downstream bindings.

The model does not choose which task runs next. It only computes data for the flow.

### 5.2 Task Type

Add a task discriminator:

- taskType: command | llm

Behavior:

- command keeps existing semantics.
- llm uses new LLM invocation + schema validation path.

Default:

- Missing taskType is treated as command for backward compatibility.

## 6. Data Model Changes

## 6.1 LauncherTask Additions

Add to LauncherTask:

- TaskType: string = "command"
- Llm: LlmTaskDefinition? (null for command tasks)

Introduce model:

- LlmTaskDefinition
  - provider: LlmProviderDefinition
  - prompts: LlmPromptDefinition
  - response: LlmResponseDefinition
  - execution: LlmExecutionDefinition

- LlmProviderDefinition
  - providerType: string
  - model: string
  - endpoint: string?
  - apiKeyEnvVar: string?
  - headers: map<string,string>? (non-secret metadata only)

- LlmPromptDefinition
  - systemTemplate: string?
  - userTemplate: string (required)
  - promptInputs: map<string,string>? (optional derived prompt fields)

- LlmResponseDefinition
  - schemaName: string
  - jsonSchema: object (required; subset defined in section 6.3)
  - outputBindings: map<string,string> (required)
  - strictValidation: bool = true

- LlmExecutionDefinition
  - temperature: number = 0
  - maxTokens: int?
  - timeoutSeconds: int = 60
  - retryCount: int = 0
  - failOnSchemaMismatch: bool = true
  - saveRawResponseAsOutput: string? (optional output name)

## 6.2 tasks.json Schema Evolution

Update docs/tasks.schema.json to support taskType union:

- schemaVersion increment to 2.
- task.taskType optional with enum command|llm.
- If taskType == command:
  - commandLine and runner remain required.
- If taskType == llm:
  - llm block required.
  - commandLine and runner optional/ignored.

Compatibility strategy:

- Existing schemaVersion 1 files continue to load as command tasks.
- Saving with any llm task writes schemaVersion 2.

## 6.3 JSON Schema Subset for LLM Outputs

For v1, support a constrained, deterministic subset:

- type: object
- properties
- required
- additionalProperties
- enum
- items (for arrays)
- primitive types: string, number, integer, boolean, null

Disallow or defer in v1:

- oneOf/anyOf/allOf/not
- complex refs across documents
- recursive schema

Rationale: reduce runtime complexity and avoid ambiguous validation behavior.

## 7. Output Binding and Dataflow Rules

## 7.1 Binding Semantics

LlmResponseDefinition.outputBindings maps Tsky output port names to schema paths.

Example:

- decision -> $.decision
- confidence -> $.confidence
- reason -> $.reason

Rules:

- Every bound path must exist in jsonSchema.
- Bound values are serialized to string for output ports using invariant formatting.
- Arrays/objects are serialized as compact JSON text.

## 7.2 Integration with Existing Dataflow

After successful LLM execution:

- Produce output map identical to current output publication model.
- Register outputs in DataflowContext using existing RegisterTaskOutputs path.
- Downstream tasks consume outputs via existing inputBindings:
  - port:decision
  - output:TaskName.decision

No change to chain order, graph edges semantics, or scheduler trigger semantics.

## 8. Prompt Construction

## 8.1 Input Sources

Prompt templates may reference:

- resolved task inputs via ${input:name}
- resolved ports via ${port:name}
- prior outputs via ${output:TaskName.output}

The same placeholder resolver principles should be reused where possible.

## 8.2 Response Contract Injection

Runtime appends a deterministic schema contract section to the effective prompt payload:

- required output JSON schema
- strict instruction to return JSON only
- no markdown fences
- no tool invocation text

This makes output constraints explicit regardless of provider prompt style.

## 8.3 Tool Separation Principle

If provider supports tools/function-calls, tool definitions are optional provider metadata and not used as output contract.

Schema remains the sole contract for dataflow outputs.

If tool calls are returned in v1 response payload, runtime rejects them unless explicitly converted by a future extension.

## 9. Runtime Execution Flow

For taskType == llm:

1. Resolve task/group/dataflow placeholders using existing resolver path.
2. Build effective prompt payload from templates + resolved values + schema contract.
3. Invoke provider adapter with timeout and retry policy.
4. Parse model response as JSON.
5. Validate JSON against LlmResponseDefinition.jsonSchema.
6. Materialize output ports from outputBindings.
7. Publish outputs and append structured output logs/history markers.

Failure behavior:

- Provider/network failure: task fails.
- Invalid JSON: task fails (unless future fallback policy configured).
- Schema validation failure: task fails when failOnSchemaMismatch=true.

## 10. UI and Authoring Changes

Task form/editor additions:

- Task Type selector: Command, LLM.
- LLM section shown when Task Type is LLM:
  - Provider Type, Model, Endpoint, API key env var.
  - System/User prompt templates.
  - JSON schema editor pane.
  - Output bindings table (output port name -> schema path).
  - Execution options (temperature, timeout, retries, max tokens).

Validation in UI before save:

- llm.userTemplate required.
- llm.response.jsonSchema required and syntactically valid.
- outputBindings must reference valid schema paths.
- no duplicate output binding keys.

## 11. Logging, History, and Observability

Add run markers for llm tasks:

- LLM_REQUEST model=<model> provider=<providerType>
- LLM_RESPONSE_VALIDATION success|failure
- LLM_OUTPUTS published=<count>

History snapshot additions:

- TaskType
- LLM provider/model metadata (non-secret)
- Schema validation outcome

Do not log:

- API keys
- full sensitive prompts when marked sensitive (future extension)

Optional:

- If saveRawResponseAsOutput set, publish raw response JSON as a normal output value.

## 12. Security and Privacy

- Secrets must be referenced via environment variables, not persisted inline in tasks.json.
- Redact known secret patterns from output logs where feasible.
- Respect existing output capture limits by applying max output sizes to raw response and bound fields.
- Enforce timeout and cancellation support equivalent to command tasks.

## 13. Remote Agent and Scheduler Considerations

v1 recommendation:

- LLM tasks execute locally on desktop.
- Scheduler can trigger them like any other task.
- Remote agent dispatch of llm tasks is not required in v1 unless process.v1-compatible packaging is added.

Future option:

- Add executorId llm.v1 in TAP RunStep command for native remote execution.
- Preserve same schema-output contract so desktop and agent behavior remain isomorphic.

## 14. Backward Compatibility

- Existing command tasks are unchanged.
- Missing taskType defaults to command.
- Existing inputs/inputBindings/outputs fields remain valid.
- Import/export should preserve unknown llm fields in roundtrip scenarios.

## 15. Example Task Configuration

```json
{
  "schemaVersion": 2,
  "groups": [
    {
      "name": "Pipeline",
      "tasks": [
        {
          "name": "Classify Release Risk",
          "group": "Pipeline",
          "taskType": "llm",
          "isEnabled": true,
          "inputs": {
            "changeSummary": "",
            "incidentCount": "0"
          },
          "inputBindings": {
            "changeSummary": "port:changeSummary",
            "incidentCount": "port:incidentCount"
          },
          "outputs": {
            "riskLevel": "",
            "approvalRecommendation": "",
            "reason": ""
          },
          "llm": {
            "provider": {
              "providerType": "openai-chat",
              "model": "gpt-5-mini",
              "apiKeyEnvVar": "OPENAI_API_KEY"
            },
            "prompts": {
              "systemTemplate": "You are a strict risk classifier.",
              "userTemplate": "Classify release risk for this change. Change: ${input:changeSummary}. Incidents: ${input:incidentCount}."
            },
            "response": {
              "schemaName": "ReleaseRiskV1",
              "jsonSchema": {
                "type": "object",
                "additionalProperties": false,
                "required": ["riskLevel", "approvalRecommendation", "reason"],
                "properties": {
                  "riskLevel": {
                    "type": "string",
                    "enum": ["low", "medium", "high"]
                  },
                  "approvalRecommendation": {
                    "type": "string",
                    "enum": ["approve", "manual-review", "block"]
                  },
                  "reason": {
                    "type": "string"
                  }
                }
              },
              "outputBindings": {
                "riskLevel": "$.riskLevel",
                "approvalRecommendation": "$.approvalRecommendation",
                "reason": "$.reason"
              },
              "strictValidation": true
            },
            "execution": {
              "temperature": 0,
              "timeoutSeconds": 45,
              "retryCount": 1,
              "failOnSchemaMismatch": true,
              "saveRawResponseAsOutput": "llmRawResponse"
            }
          }
        }
      ]
    }
  ]
}
```

## 16. Implementation Plan

Phase 1: Model + schema

- Extend LauncherTask model with taskType and llm block.
- Update task validation in TaskConfiguration.
- Update docs/tasks.schema.json to v2 with conditional validation.

Phase 2: Runtime

- Add LLM task executor service and provider abstraction.
- Implement JSON parsing + schema validator + output binding materialization.
- Integrate with existing output publication and DataflowContext.

Phase 3: UI

- Add Task Type selector and LLM authoring fields in task dialogs.
- Add schema/binding validation UX.

Phase 4: Tests

- Unit tests:
  - Schema validation (valid/invalid llm task definitions).
  - Prompt placeholder resolution.
  - Response schema validation and output binding mapping.
- Integration tests:
  - Chain execution where llm output feeds downstream command task.
  - Scheduler-triggered llm task execution.

## 17. Acceptance Criteria

- User can create and save an llm task in tasks.json.
- Running an llm task produces outputs bound from schema-validated JSON.
- Downstream tasks can consume llm-produced outputs through existing binding syntax.
- Invalid model response JSON or schema mismatch produces clear task failure diagnostics.
- Existing command tasks run unchanged.

## 18. Open Questions

- Should v1 support multiple provider adapters, or start with one adapter and a generic interface?
- Should schema-path binding use JSONPath ($.x) or JSON Pointer (/x) as canonical format?
- Do we need per-field type coercion controls when mapping outputs to string ports?
- Should raw prompt/response persistence be opt-in for privacy reasons?
- Should remote agent support for llm tasks be part of v1.1 via llm.v1 executor?
