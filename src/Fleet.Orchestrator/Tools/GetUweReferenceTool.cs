using System.ComponentModel;
using ModelContextProtocol.Server;

namespace Fleet.Orchestrator.Tools;

[McpServerToolType]
public sealed class GetUweReferenceTool
{
    private const string Reference = """
        # Universal Workflow Engine (UWE) — Reference

        UWE workflows are stored as JSON step trees in the orchestrator DB.
        Each workflow definition has: name, namespace, taskQueue, and a root step.
        The engine deserializes JSON case-insensitively; camelCase is canonical.

        ---

        ## Step Types (16 total)

        All steps support these base fields (optional):
        - `name`: string — human label; also used as key when storing step output in vars
        - `outputVar`: string — variable name to store step result (literal, not a template)
        - `ignoreFailure`: bool — swallow exceptions and continue (default false)

        ---

        ### 1. sequence
        Runs child steps one after another. The root step is always a sequence.
        ```json
        { "type": "sequence", "steps": [ <step>, ... ] }
        ```

        ### 2. parallel
        Runs static branches concurrently, OR fans out over an array dynamically.

        **Static branches** — all steps run concurrently:
        ```json
        { "type": "parallel", "steps": [ <step>, ... ] }
        ```

        **Dynamic fan-out** — one step template executed per item in array:
        ```json
        {
          "type": "parallel",
          "forEach": "{{vars.agent_list}}",
          "itemVar": "currentAgent",
          "step": { "type": "delegate", "target": "{{vars.currentAgent}}", "instruction": "..." }
        }
        ```
        - `forEach`: template expression resolving to an array
        - `itemVar`: variable name bound to the current item inside the step template
        - `step`: single step template (NOT `steps`) executed per item

        ### 3. loop
        Repeats child steps up to `maxIterations` times. Use `branch` + `{"type":"break"}` inside to exit early.
        ```json
        { "type": "loop", "maxIterations": 5, "steps": [ <step>, ... ] }
        ```
        - `maxIterations`: safety cap (default 5). There is no `count` or `while` property.

        ### 4. branch
        Evaluates an expression, then executes the matching case.
        ```json
        {
          "type": "branch",
          "on": "{{vars.decision | extract: 'Decision.*?(approved|rejected|changes_requested)'}}",
          "cases": {
            "approved":  { "type": "delegate", "target": "cto", "instruction": "Merge the PR." },
            "rejected":  { "type": "break" }
          },
          "default": { "type": "delegate", "target": "cto", "instruction": "Request clarification." }
        }
        ```
        - `on` (required): template expression whose string result is matched against case keys
        - `cases` (required): object mapping string values → step definition
        - `default` (optional): step definition executed when no case key matches

        Each case value is a step definition. Use `{"type":"break"}` to exit the enclosing loop,
        or `{"type":"continue"}` to skip to the next iteration.

        ### 5. break
        Exits the innermost enclosing loop immediately. Only valid inside a `loop` step.
        ```json
        { "type": "break" }
        ```

        ### 6. continue
        Skips the remaining steps in the current loop iteration and proceeds to the next. Only valid inside a `loop` step.
        ```json
        { "type": "continue" }
        ```

        ### 7. noop
        Explicit no-op — does nothing and returns null. Useful as a placeholder or default branch case.
        ```json
        { "type": "noop" }
        ```

        ### 8. delegate
        Delegates a task to an agent via RabbitMQ and waits for the response.
        ```json
        {
          "type": "delegate",
          "target": "cto",
          "instruction": "Review the PR at {{vars.pr_url}}.",
          "outputVar": "review_result",
          "timeoutMinutes": 30,
          "retryOnIncomplete": true,
          "maxIncompleteRetries": 3
        }
        ```
        - `target` (required): short agent name (e.g. "cto", "developer") — supports `{{template}}`
        - `instruction` (required): inline instruction text — supports `{{template}}`
        - `timeoutMinutes`: default 30
        - `retryOnIncomplete`: auto-retry if agent returns isIncomplete=true (default true)
        - `maxIncompleteRetries`: max continuation retries (default 3)

        ### 9. delegate_with_escalation
        Same as `delegate` but wraps execution with an escalation pattern.
        On failure, notifies the escalation target and waits for an `escalation-decision` signal.
        Signal payload: `{"Decision": "retry|skip|continue", "UpdatedInstruction": "..."}`
        ```json
        {
          "type": "delegate_with_escalation",
          "target": "developer",
          "instruction": "Implement issue #{{input.IssueNumber}} on a new branch and open a PR.",
          "outputVar": "impl_result",
          "timeoutMinutes": 60
        }
        ```

        ### 10. wait_for_signal
        Pauses workflow until a named Temporal signal arrives.
        Signal payload is stored in `outputVar`.
        ```json
        {
          "type": "wait_for_signal",
          "signalName": "human-review",
          "outputVar": "review_decision",
          "timeoutMinutes": 1440,
          "phase": "human-review",
          "reminderIntervalMinutes": 60,
          "maxReminders": 3,
          "autoCompleteOnTimeout": false,
          "notifyStep": {
            "type": "delegate",
            "target": "cto",
            "instruction": "Reminder: please review."
          }
        }
        ```
        - `signalName` (required): Temporal signal name
        - `timeoutMinutes`: null/omit = wait forever
        - `phase`: sets Phase search attribute while waiting (dashboard visibility)
        - `reminderIntervalMinutes`: how often to fire `notifyStep` while waiting
        - `maxReminders`: max reminder notifications (default 3)
        - `autoCompleteOnTimeout`: if true, returns `{"Decision":"timeout"}` instead of throwing
        - `notifyStep`: a delegate step executed before waiting and on each reminder tick

        ### 11. child_workflow
        Starts a child workflow and waits for it to complete.
        ```json
        {
          "type": "child_workflow",
          "workflowType": "ConsensusReviewWorkflow",
          "taskQueue": "fleet",
          "args": { "Topic": "{{vars.pr_url}}", "Agents": ["cto", "developer", "reviewer"] },
          "outputVar": "consensus_result",
          "timeoutMinutes": 60
        }
        ```
        - `workflowType` (required): workflow type name — supports `{{template}}`
        - `args`: object passed as workflow input; values support template expressions
        - `namespace`: optional namespace override (defaults to current workflow namespace)
        - `taskQueue`: optional task queue override

        ### 12. fire_and_forget
        Starts a child workflow without waiting for the result (ParentClosePolicy = Abandon).
        Same properties as `child_workflow`.
        ```json
        {
          "type": "fire_and_forget",
          "workflowType": "UweDocMaintenanceWorkflow",
          "taskQueue": "fleet",
          "args": { "PrNumber": "{{vars.pr_number}}" }
        }
        ```

        ### 13. cross_namespace_start
        Starts a workflow in a different Temporal namespace (fire-and-forget).
        ```json
        {
          "type": "cross_namespace_start",
          "workflowType": "SomeWorkflow",
          "namespace": "other-namespace",
          "taskQueue": "other-namespace",
          "workflowId": "my-workflow-{{vars.run_id}}",
          "args": { "Key": "{{vars.value}}" }
        }
        ```
        - `namespace` (required): target Temporal namespace
        - `workflowType` (required): workflow type name
        - `taskQueue` (required): task queue in the target namespace
        - `workflowId`: optional; auto-generated UUID if omitted

        ### 14. set_variable
        Evaluates template expressions and stores results in workflow variables. Deterministic, instant, no delegation.
        ```json
        {
          "type": "set_variable",
          "name": "capture_pr_url",
          "vars": {
            "pr_url": "{{vars.impl_result | extract: 'PR_URL: (https://[^\\s]+)'}}",
            "issue_number": "{{input.IssueNumber}}"
          }
        }
        ```
        - `vars` (required): object mapping variable names → template expressions. Each expression is resolved and stored in `vars.*` scope.
        - Replaces fragile delegate-based extraction (no timeout risk, no RabbitMQ round-trip).
        - Stored values are available immediately as `{{vars.<name>}}` in subsequent steps.

        ### 15. set_attribute
        Upserts one or more Temporal search attributes. Values support `{{template}}`.
        ```json
        {
          "type": "set_attribute",
          "attributes": {
            "Phase": "human-review",
            "IssueNumber": "{{vars.issue_number}}"
          }
        }
        ```
        - `attributes` (required): object with string keys and string values (not a single name/value pair)
        - Registered attributes: IssueNumber (Int), PrNumber (Int), Repo (Keyword),
          DocPrs (Keyword), Phase (Keyword), ReviewDate (Keyword)
        - Unknown attribute names are silently skipped.

        ### 16. http_request
        Makes a generic HTTP request to any URL. Used for calling REST endpoints or MCP servers directly.
        ```json
        {
          "type": "http_request",
          "url": "https://api.example.com/data/{{vars.resource_id}}",
          "method": "POST",
          "headers": { "Authorization": "Bearer {{vars.token}}", "Content-Type": "application/json" },
          "body": "{\"key\": \"{{vars.value}}\"}",
          "timeoutSeconds": 30,
          "expectedStatusCodes": [200, 201],
          "outputVar": "response_body"
        }
        ```
        - `url` (required): target URL — supports `{{template}}` interpolation
        - `method`: HTTP method (default "GET")
        - `headers`: request headers dict; values support `{{template}}` interpolation
        - `body`: request body; string values support `{{template}}` interpolation; objects are serialized as JSON
        - `timeoutSeconds`: request timeout in seconds (default 30)
        - `expectedStatusCodes`: acceptable status codes (default [200]); step fails if response is not in list
        - Returns the response body as a string stored in `outputVar`

        ---

        ## Template Engine

        Expressions use `{{expr}}` syntax evaluated at runtime.

        ### Scopes
        | Scope    | Description                                                    |
        |----------|----------------------------------------------------------------|
        | input.*  | Workflow input fields (e.g. `{{input.TargetAgent}}`)           |
        | vars.*   | Variables set by steps via `outputVar`                         |
        | config.* | FleetWorkflowOptions keys (e.g. `{{config.CtoAgent}}`)         |

        ### Filters (pipe syntax)
        | Filter               | Description                                                              |
        |----------------------|--------------------------------------------------------------------------|
        | `\| default:"val"`  | Return fallback if expression is null or empty string                    |
        | `\| extract:'PATTERN'` | Regex match on string; returns first capture group if present, else full match |
        | `\| json`           | Parse the string value as JSON into a JsonElement (deserialize, not serialize) |

        ### Filter examples
        ```
        {{input.Description | default:"No description provided"}}
        {{vars.review_decision | extract:"Decision.*?(approved|rejected)"}}
        {{vars.raw_json_string | json}}
        {{config.CtoAgent | default:"cto"}}
        ```

        ### Full-expression optimization
        A template that is exactly `{{expr}}` (no surrounding text) returns the raw object —
        enabling arrays and objects to be passed between steps without stringification.

        ### Truthy evaluation (branch `on`, loop break conditions)
        Falsy: `false`, `null`, empty string, `"false"`, `"0"`, JsonElement null/undefined.
        Everything else is truthy.

        ### Bracket indexer
        Access a dictionary value by a dynamic key:
        ```
        {{config.agentPerspectives[cto]}}
        ```

        ---

        ## config.* Keys (FleetWorkflowOptions)

        | Key                     | Default                         | Description                                            |
        |-------------------------|---------------------------------|--------------------------------------------------------|
        | escalationTarget        | (env: FLEET_CTO_AGENT)          | Agent notified on workflow failures                    |
        | ctoAgent                | (env: FLEET_CTO_AGENT)          | The user-defined CTO agent — referenced by seed UWE workflows as `{{config.CtoAgent}}` |

        ---

        ## JSON Conventions

        - All step objects must have `"type"` (the discriminator field).
        - `outputVar` is always a literal identifier string — never a template expression.
        - `steps` array: used by `sequence`, `parallel` (static), and `loop`.
        - `args` (not `input`): used by `child_workflow`, `fire_and_forget`, `cross_namespace_start`.
        - `target` (not `agent`): delegate and delegate_with_escalation agent name.
        - `signalName` (not `signal`): wait_for_signal signal identifier.
        - `url` + `method` + `headers` + `body`: http_request fields.
        - `attributes` (dict, not name/value): set_attribute key-value map.
        - `on` + `cases` + `default` (not ordered conditions): branch step structure.
        - `maxIterations` (not count/while): loop iteration cap.
        - Integer search attributes (IssueNumber, PrNumber) must resolve to a parseable integer string.

        ---

        ## Minimal Complete Example

        ```json
        {
          "type": "sequence",
          "steps": [
            { "type": "set_attribute", "attributes": { "Phase": "delegating" } },
            {
              "type": "delegate",
              "target": "{{config.CtoAgent}}",
              "instruction": "Assess the following topic and respond with 'approved' or 'rejected': {{input.Topic}}",
              "outputVar": "assessment",
              "timeoutMinutes": 20
            },
            {
              "type": "branch",
              "on": "{{vars.assessment | extract:'(approved|rejected)'}}",
              "cases": {
                "approved": {
                  "type": "set_attribute",
                  "attributes": { "Phase": "approved" }
                }
              },
              "default": {
                "type": "set_attribute",
                "attributes": { "Phase": "rejected" }
              }
            }
          ]
        }
        ```
        """;

    [McpServerTool(Name = "get_uwe_reference")]
    [Description("Returns the full UWE (Universal Workflow Engine) reference: all 12 step types with exact JSON property names, template engine syntax, scopes, filters, config keys, and JSON conventions. Use this before designing or editing any UWE workflow definition.")]
    public string GetUweReference() => Reference;
}
