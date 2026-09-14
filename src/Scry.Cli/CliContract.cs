using System.Text.Json;
using Scry.Contracts;

internal static class CliContract
{
    internal static readonly IReadOnlyList<string> AdapterOperations =
    [
        "wpf.snapshot",
        "wpf.wait",
        "wpf.assert",
        "wpf.screenshot",
        "winforms.snapshot",
        "winforms.wait",
        "winforms.assert",
        "winforms.screenshot"
    ];

    /// <summary>
    /// Options every target-addressed command accepts, as opposed to the per-operation request
    /// fields. Kept here rather than only in the help text so <c>scry schema</c> describes them,
    /// since that is what agents are pointed at as the authority.
    /// </summary>
    private static readonly IReadOnlyList<CliField> CommonOptions =
    [
        F("--target", "string", false, "Target ID or alias; mutually exclusive with --descriptor."),
        F("--descriptor", "string", false, "Path to a target descriptor; mutually exclusive with --target."),
        F("--session", "string", false, "Resume a persistent session instead of using an ephemeral one."),
        F("--correlation", "string", false, "Correlation ID echoed back on the response."),
        F("--request", "file|-", false, "JSON request object from a file, or - for stdin."),
        F("--input", "file|-", false, "Compatible spelling of --request."),
        F("--source", "file|-", false, "C# source from a file, or - for stdin. Execution commands only."),
        F(
            "--timeout",
            "integer",
            false,
            "Seconds to wait for a response before failing with connection_failed; 0 waits " +
            "indefinitely. Defaults to 60. A marshalled submission that never observes its " +
            "cancellation token cannot be ended by timeoutMilliseconds, so this is the only " +
            "bound on such a request.")
    ];

    private static readonly IReadOnlyList<CliExitCode> ExitCodes =
    [
        new(0, "success", "The command completed successfully."),
        new(2, "usage_error", "Arguments, options, input, or JSON were invalid."),
        new(3, "target_error", "The target or descriptor could not be resolved or read."),
        new(4, "connection_error", "Connection, authentication, or protocol negotiation failed."),
        new(5, "operation_error", "The target accepted the request but the operation failed."),
        new(6, "partial_failure", "At least one scenario or batch command failed."),
        new(70, "internal_error", "The CLI encountered an unexpected failure.")
    ];

    internal static readonly IReadOnlyList<CliCommand> Commands =
    [
        Local(
            "discover",
            "List live Scry targets visible to the current user.",
            "scry discover",
            "scry discover",
            [],
            "Discovery result with protocolVersion and a deterministic targets array."),
        Local(
            "attach",
            "Inject the endpoint into a running process that does not reference Scry.",
            "scry attach <pid|process-name> [--alias <name>] [--adapters wpf|winforms|none]",
            "scry attach 1234 --adapters wpf",
            [
                new CliField(
                    "<pid|process-name>",
                    "string",
                    true,
                    "Target process ID, or a process name that matches exactly one process."),
                new CliField(
                    "--alias",
                    "string",
                    false,
                    "Alias to publish for the injected endpoint, for later --target use."),
                new CliField(
                    "--adapters",
                    "wpf|winforms|none",
                    false,
                    "Desktop adapter to wire inside the target. Defaults to none, which leaves " +
                    "the endpoint framework-neutral: no wpf.*/winforms.* operations and no " +
                    "\"marshal\": \"ui\", so submissions cannot touch a DependencyObject or Control.")
            ],
            "Attach result with the target descriptor path and a real protocol handshake; never the capability token."),
        Local(
            "schema",
            "Emit the machine-readable CLI command and response contract.",
            "scry schema",
            "scry schema",
            [],
            "CLI contract catalog."),
        Target(
            "capabilities",
            "capabilities",
            "Report protocol, runtime, and registered operation capabilities.",
            [],
            "Capability description."),
        Target(
            "roots",
            "roots",
            "List registered roots and their descriptions.",
            [],
            "Root descriptions."),
        Target(
            "inspect",
            "inspect",
            "Inspect the members of a root or external reference.",
            [
                F("root", "string", false, "Registered root name; mutually exclusive with reference."),
                F("reference", "ExternalReference", false, "Leased reference; mutually exclusive with root."),
                F("includeNonPublic", "boolean", false, "Explicitly include non-public members.")
            ],
            "Bounded member inspection."),
        Target(
            "get",
            "get",
            "Read a member from a root or external reference.",
            [
                F("root", "string", false, "Registered root name; mutually exclusive with reference."),
                F("reference", "ExternalReference", false, "Leased reference; mutually exclusive with root."),
                F("member", "string", true, "Member name."),
                F("includeNonPublic", "boolean", false, "Explicitly include non-public members."),
                F("asReference", "boolean", false, "Lease a boxed value result for later operations.")
            ],
            "RemoteValue."),
        Target(
            "set",
            "set",
            "Set a field or property on a root or external reference.",
            [
                F("root", "string", false, "Registered root name; mutually exclusive with reference."),
                F("reference", "ExternalReference", false, "Leased reference; mutually exclusive with root."),
                F("member", "string", true, "Member name."),
                F("value", "any", true, "JSON value, bounded value, or ExternalReference."),
                F("includeNonPublic", "boolean", false, "Explicitly include non-public members."),
                F("asReference", "boolean", false, "Lease a boxed value result for later operations.")
            ],
            "RemoteValue containing the assigned value."),
        Target(
            "invoke",
            "invoke",
            "Invoke a member or registered structured operation.",
            [
                F("root", "string", false, "Registered root name for member invocation."),
                F("reference", "ExternalReference", false, "Leased reference for member invocation."),
                F("member", "string", false, "Member name."),
                F("registeredOperation", "string", false, "Registered operation name."),
                F("arguments", "object|array", false, "Named structured arguments or positional arguments."),
                F("includeNonPublic", "boolean", false, "Explicitly include non-public members."),
                F("asReference", "boolean", false, "Lease a boxed value result for later operations.")
            ],
            "RemoteValue."),
        Target(
            "enumerate",
            "enumerate",
            "Read a bounded page from an enumerable root or reference.",
            [
                F("root", "string", false, "Registered root name; mutually exclusive with reference."),
                F("reference", "ExternalReference", false, "Leased reference; mutually exclusive with root."),
                F("offset", "integer", false, "Zero-based item offset."),
                F("limit", "integer", false, "Page size from 1 through 1000."),
                F("asReferences", "boolean", false, "Lease boxed value-type items.")
            ],
            "Ordered RemoteValue page with hasMore."),
        Target(
            "release",
            "release",
            "Release one or more leased handles.",
            [
                F("handleId", "string", false, "One handle ID."),
                F("handleIds", "string[]", false, "Multiple handle IDs.")
            ],
            "Released handle count."),
        Execution(
            "evaluate",
            "Evaluate a C# expression/script and return its value.",
            "scry evaluate --target app --source expression.csx",
            "ExecutionResult."),
        Execution(
            "execute",
            "Execute an async C# statement body and return its value.",
            "Get-Content statements.csx | scry execute --target app",
            "ExecutionResult."),
        Target(
            "wait",
            "wait",
            "Poll a C# condition until it holds or the timeout elapses. Framework-neutral: works in console, service and worker targets that have no UI tree, and can assert on view-model state that the wpf.*/winforms.* conditions cannot see.",
            [
                F("source", "string", true, "C# expression to evaluate on each attempt."),
                F("operator", "isTrue|equals|notEquals|contains|isNull|isNotNull", false, "Comparison applied to the result; defaults to isTrue."),
                F("expected", "string|number|boolean|null", false, "Operand for equals, notEquals and contains."),
                F("timeoutMilliseconds", "number", false, "Total budget; defaults to 5000."),
                F("pollIntervalMilliseconds", "number", false, "Delay between attempts; defaults to 100."),
                F("imports", "string[]", false, "Extra namespaces for the expression."),
                F("references", "string[]", false, "Extra assembly references for the expression."),
                F("marshal", "ui", false, "Run each evaluation on the host UI thread. Marshals the evaluations, not the polling loop, so waiting never occupies the UI thread between attempts.")
            ],
            "ConditionResult with satisfied, attempts, elapsedMilliseconds, value and description. A timeout returns satisfied=false rather than failing; use assert when a miss should fail."),
        Target(
            "assert",
            "assert",
            "Evaluate a C# condition once and fail the request when it does not hold.",
            [
                F("source", "string", true, "C# expression to evaluate."),
                F("operator", "isTrue|equals|notEquals|contains|isNull|isNotNull", false, "Comparison applied to the result; defaults to isTrue."),
                F("expected", "string|number|boolean|null", false, "Operand for equals, notEquals and contains."),
                F("imports", "string[]", false, "Extra namespaces for the expression."),
                F("references", "string[]", false, "Extra assembly references for the expression."),
                F("marshal", "ui", false, "Run the evaluation on the host UI thread.")
            ],
            "ConditionResult on success; an assertion_failed error with the comparison description otherwise."),
        Target(
            "load-assembly",
            "load-assembly",
            "Load an assembly explicitly into the target.",
            [
                F("path", "string", true, "Absolute assembly path in the target environment."),
                F("loadPolicy", "default|isolated", false, "Load-context policy; defaults to default.")
            ],
            "LoadAssemblyResult."),
        Target(
            "list-assemblies",
            "list-assemblies",
            "List assemblies loaded in the target.",
            [],
            "ListAssembliesResult."),
        Target(
            "find-types",
            "find-types",
            "Find loaded types using bounded filters.",
            [
                F("query", "string", false, "Case-insensitive name query."),
                F("assembly", "string", false, "Assembly selector."),
                F("loadContext", "string", false, "Load-context selector."),
                F("namespace", "string", false, "Namespace selector."),
                F("includeNonPublic", "boolean", false, "Include non-public types."),
                F("limit", "integer", false, "Bounded result count.")
            ],
            "FindTypesResult."),
        Target(
            "describe-type",
            "describe-type",
            "Describe one loaded type and its bounded members.",
            [
                F("type", "string", true, "Full type name."),
                F("assembly", "string", false, "Assembly selector."),
                F("loadContext", "string", false, "Load-context selector."),
                F("includeNonPublic", "boolean", false, "Include non-public members.")
            ],
            "TypeDescription."),
        Job(
            "jobs start",
            "job.start",
            "Start an endpoint-owned long-running operation.",
            [
                F("operation", "string", true, "Protocol operation to run."),
                F("payload", "object", true, "Operation payload."),
                F("correlationId", "string", false, "Correlation ID for the job itself.")
            ],
            "JobSnapshot containing a resumable JobHandle."),
        Job(
            "jobs status",
            "job.status",
            "Read the current state of a job.",
            [F("job", "JobHandle", true, "Target/session/job-qualified handle.")],
            "JobSnapshot."),
        Job(
            "jobs wait",
            "job.wait",
            "Wait up to a bounded timeout for a job to complete.",
            [
                F("job", "JobHandle", true, "Target/session/job-qualified handle."),
                F("timeoutMilliseconds", "integer", false, "Wait timeout from 0 through 300000.")
            ],
            "JobWaitResult; timedOut does not change job state."),
        Job(
            "jobs cancel",
            "job.cancel",
            "Request cooperative job cancellation.",
            [F("job", "JobHandle", true, "Target/session/job-qualified handle.")],
            "Cancellation acknowledgement."),
        Job(
            "jobs logs",
            "job.logs",
            "Read a bounded cursor page of retained job logs.",
            [
                F("job", "JobHandle", true, "Target/session/job-qualified handle."),
                F("cursor", "integer", false, "First requested log cursor."),
                F("limit", "integer", false, "Page size from 1 through 1000.")
            ],
            "JobLogResult."),
        Scenario(
            "scenario",
            "Run ordered target commands sequentially or concurrently.",
            "scry scenario --input scenario.json"),
        Scenario(
            "batch",
            "Alias for scenario.",
            "scry batch --input scenario.json"),
        Adapter(
            "wpf.snapshot",
            "Capture a bounded WPF visual or logical tree projection.",
            [
                F("root", "string", false, "Registered WPF root."),
                F("tree", "visual|logical", false, "Tree kind; defaults to visual.")
            ],
            "WpfSnapshot."),
        Adapter(
            "wpf.wait",
            "Wait for a WPF projection condition.",
            WpfConditionFields(includeTiming: true),
            "WpfWaitResult."),
        Adapter(
            "wpf.assert",
            "Assert a WPF projection condition immediately.",
            WpfConditionFields(includeTiming: false),
            "WpfWaitResult."),
        Adapter(
            "wpf.screenshot",
            "Capture a bounded WPF root screenshot.",
            [
                F("root", "string", false, "Registered WPF root. Defaults to the application main window, or to the only root when there is exactly one; required only when the target has several roots and no main window."),
                F("path", "string", false, "Projected node path within the root."),
                F("tree", "visual|logical", false, "Tree kind used to resolve path.")
            ],
            "WpfScreenshotResult."),
        Adapter(
            "winforms.snapshot",
            "Capture a bounded WinForms control projection.",
            [F("root", "string", false, "Registered WinForms root.")],
            "WinFormsSnapshot."),
        Adapter(
            "winforms.wait",
            "Wait for a WinForms projection condition.",
            WinFormsConditionFields(includeTiming: true),
            "WinFormsWaitResult."),
        Adapter(
            "winforms.assert",
            "Assert a WinForms projection condition immediately.",
            WinFormsConditionFields(includeTiming: false),
            "WinFormsWaitResult."),
        Adapter(
            "winforms.screenshot",
            "Capture a bounded WinForms root screenshot.",
            [
                F("root", "string", false, "Registered WinForms root. Defaults to the only root when there is exactly one; required when the target has several."),
                F("path", "string", false, "Projected control path within the root.")
            ],
            "WinFormsScreenshot.")
    ];

    internal static bool IsEndpointOperation(string operation) =>
        ProtocolConstants.CoreCapabilities.Contains(operation, StringComparer.Ordinal) ||
        AdapterOperations.Contains(operation, StringComparer.Ordinal);

    internal static (string Operation, JsonElement Payload) PrepareRequest(
        string operation,
        JsonElement payload)
    {
        if (AdapterOperations.Contains(operation, StringComparer.Ordinal))
        {
            return (
                "invoke",
                JsonSerializer.SerializeToElement(
                    new { registeredOperation = operation, arguments = payload },
                    ScryJson.Options));
        }

        if (operation == "job.start" &&
            payload.TryGetProperty("operation", out var jobOperation) &&
            jobOperation.ValueKind == JsonValueKind.String &&
            AdapterOperations.Contains(jobOperation.GetString()!, StringComparer.Ordinal) &&
            payload.TryGetProperty("payload", out var jobPayload) &&
            jobPayload.ValueKind == JsonValueKind.Object)
        {
            var correlationId = payload.TryGetProperty("correlationId", out var correlation) &&
                correlation.ValueKind == JsonValueKind.String
                    ? correlation.GetString()
                    : null;
            return (
                operation,
                JsonSerializer.SerializeToElement(
                    new
                    {
                        operation = "invoke",
                        payload = new
                        {
                            registeredOperation = jobOperation.GetString(),
                            arguments = jobPayload
                        },
                        correlationId
                    },
                    ScryJson.Options));
        }

        return (operation, payload);
    }

    internal static ProtocolResponse NormalizeResponse(
        string requestedOperation,
        ProtocolResponse response)
    {
        if (!response.Success ||
            !AdapterOperations.Contains(requestedOperation, StringComparer.Ordinal) ||
            response.Result is not { ValueKind: JsonValueKind.Object } result ||
            !result.TryGetProperty("value", out var remoteValue) ||
            remoteValue.ValueKind != JsonValueKind.Object ||
            !remoteValue.TryGetProperty("kind", out var kind) ||
            kind.GetString() != "scalar" ||
            !remoteValue.TryGetProperty("type", out var type) ||
            type.GetString() != typeof(JsonElement).FullName ||
            !remoteValue.TryGetProperty("value", out var value))
        {
            return response;
        }

        return response with { Result = value.Clone() };
    }

    internal static bool TryGetHelpTopic(string[] args, out string? topic)
    {
        topic = null;
        if (args.Length == 0)
        {
            return false;
        }

        if (args[0] is "-h" or "--help")
        {
            return true;
        }

        if (args[0] == "help")
        {
            topic = NormalizeTopic(args[1..]);
            return true;
        }

        var helpIndex = Array.FindIndex(args, argument => argument is "-h" or "--help");
        if (helpIndex < 0)
        {
            return false;
        }

        if (helpIndex != args.Length - 1)
        {
            throw new CliUsageException("--help must be the final argument.");
        }

        topic = NormalizeTopic(args[..helpIndex]);
        return true;
    }

    internal static void WriteHelp(string? topic, TextWriter writer)
    {
        if (string.IsNullOrWhiteSpace(topic))
        {
            writer.WriteLine(
                """
                Usage:
                  scry discover
                  scry attach <pid|process-name> [--alias <name>] [--adapters wpf|winforms|none]
                  scry schema
                  scry <command> (--target <id-or-alias> | --descriptor <path>) [options]
                  scry jobs <start|status|wait|cancel|logs> (--target <id-or-alias> | --descriptor <path>) [options]
                  scry <scenario|batch> --input <file|->
                  scry help [command]

                Commands:
                  Discovery:  discover, attach, capabilities, roots
                  Objects:    inspect, get, set, invoke, enumerate, release
                  Execution:  evaluate, execute
                  Validation: wait, assert
                  Assemblies: load-assembly, list-assemblies, find-types, describe-type
                  Jobs:       jobs start, jobs status, jobs wait, jobs cancel, jobs logs
                  Flows:      scenario, batch
                  WPF:        wpf.snapshot, wpf.wait, wpf.assert, wpf.screenshot
                  WinForms:   winforms.snapshot, winforms.wait, winforms.assert, winforms.screenshot
                  Contract:   schema

                Common target options:
                  --target <id-or-alias> | --descriptor <path>
                  --session <id>          Resume a persistent session.
                  --correlation <id>      Echo an agent-supplied correlation ID.
                  --request <file|->      Read a JSON request object from a file or stdin.
                  --input <file|->        Compatible spelling of --request.
                  --timeout <seconds>     Give up if the target does not respond; 0 waits
                                          indefinitely. Defaults to 60 seconds.

                C# source is accepted through --source <file|-> or redirected stdin. Use --request
                for execution settings plus source. --json is retained for non-sensitive,
                non-execution compatibility; prefer files or stdin for agent workflows.

                Exit codes: 0 success, 2 usage/JSON, 3 target, 4 connection/protocol,
                5 target operation, 6 scenario partial failure, 70 unexpected CLI failure.
                Target responses include operationId and correlationId. Tokens are read only from
                local descriptors and are never accepted as command-line arguments.

                Examples:
                  scry discover
                  scry roots --target my-app --correlation probe-1
                  scry inspect --target my-app --request inspect.json
                  Get-Content request.json | scry invoke --target my-app
                  scry help jobs wait
                  scry schema
                """);
            return;
        }

        var definition = Find(topic)
            ?? throw new CliUsageException($"Unknown help topic '{topic}'.");
        writer.WriteLine($"Usage: {definition.Usage}");
        writer.WriteLine();
        writer.WriteLine(definition.Summary);
        writer.WriteLine();
        writer.WriteLine($"Input: {definition.Input}");
        if (definition.Fields.Count > 0)
        {
            writer.WriteLine("Request fields:");
            foreach (var field in definition.Fields)
            {
                writer.WriteLine(
                    $"  {field.Name} ({field.Type}, {(field.Required ? "required" : "optional")}): " +
                    field.Description);
            }
        }

        writer.WriteLine($"Result: {definition.Result}");
        writer.WriteLine();
        writer.WriteLine($"Example: {definition.Example}");
        if (definition.Kind is "target" or "execution" or "job" or "adapter")
        {
            writer.WriteLine(
                "Target selection: specify exactly one of --target <id-or-alias> or --descriptor <path>.");
            writer.WriteLine(
                "The ProtocolResponse includes success, sessionId, operationId, correlationId, and result or error.");
        }
    }

    internal static void WriteSchema(TextWriter writer)
    {
        var schema = new
        {
            schemaVersion = 1,
            protocolVersion = ProtocolConstants.Version,
            command = "scry",
            inputContract = new
            {
                targetSelection = "Exactly one of --target or --descriptor for target operations.",
                request = "JSON object from --request <file|->, --input <file|->, or redirected stdin.",
                source = "C# only from --source <file|->, an execution request file, or redirected stdin.",
                inlineJson = "Compatibility only for non-execution, non-sensitive payloads.",
                secrets = "Capability tokens are read from local descriptors; no token option exists."
            },
            responseShapes = new
            {
                protocolResponse = new
                {
                    fields = new[]
                    {
                        F("protocolVersion", "integer", true, "Negotiated protocol version."),
                        F("requestId", "string", true, "Client request identifier."),
                        F("success", "boolean", true, "Whether the target operation succeeded."),
                        F("sessionId", "string", false, "Target session identifier."),
                        F("operationId", "string", false, "Target-generated operation identifier."),
                        F("correlationId", "string", false, "Echoed correlation ID or operation ID."),
                        F("result", "any", false, "Operation-specific result when successful."),
                        F("error", "ProtocolError", false, "Structured target error when unsuccessful.")
                    }
                },
                cliError = new
                {
                    fields = new[]
                    {
                        F("success", "boolean", true, "Always false."),
                        F("error.code", "string", true, "Stable machine-readable error code."),
                        F("error.message", "string", true, "Actionable error message.")
                    }
                },
                scenarioResult = new
                {
                    fields = new[]
                    {
                        F("protocolVersion", "integer", true, "Protocol version."),
                        F("mode", "sequential|concurrent", true, "Normalized execution mode."),
                        F("success", "boolean", true, "True only if every command succeeded."),
                        F("results", "ScenarioCommandResult[]", true, "Ordered per-command responses or CLI errors.")
                    }
                }
            },
            exitCodes = ExitCodes,
            // Options accepted by every target-addressed command. Previously these appeared only
            // in the human help text, which left an agent told to trust `scry schema` unable to
            // discover them at all.
            commonOptions = CommonOptions,
            commands = Commands.Select(command => new
            {
                command = command.Path,
                operation = command.Operation,
                wireOperation = command.Kind == "adapter" ? "invoke" : command.Operation,
                kind = command.Kind,
                summary = command.Summary,
                usage = command.Usage,
                targetSelector = command.Kind is "target" or "execution" or "job" or "adapter"
                    ? "required"
                    : "none",
                input = command.Input,
                request = new { type = "object", fields = command.Fields },
                result = new
                {
                    envelope = command.Kind is "target" or "execution" or "job" or "adapter"
                        ? "protocolResponse"
                        : command.Path is "scenario" or "batch"
                            ? "scenarioResult"
                            : "localResult",
                    shape = command.Result
                },
                example = command.Example
            })
        };
        writer.WriteLine(JsonSerializer.Serialize(schema, ScryJson.Options));
    }

    private static CliCommand? Find(string topic) =>
        Commands.FirstOrDefault(command =>
            string.Equals(command.Path, topic, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(command.Operation, topic, StringComparison.OrdinalIgnoreCase));

    private static string? NormalizeTopic(IEnumerable<string> arguments)
    {
        var parts = arguments.ToArray();
        if (parts.Length == 0)
        {
            return null;
        }

        if (parts.Length == 1 && parts[0] == "jobs")
        {
            return null;
        }

        return parts.Length >= 2 && parts[0] == "jobs"
            ? $"jobs {parts[1]}"
            : parts[0];
    }

    private static CliCommand Local(
        string path,
        string summary,
        string usage,
        string example,
        IReadOnlyList<CliField> fields,
        string result) =>
        new(path, null, "local", summary, usage, "No request payload.", fields, result, example);

    private static CliCommand Target(
        string path,
        string operation,
        string summary,
        IReadOnlyList<CliField> fields,
        string result) =>
        new(
            path,
            operation,
            "target",
            summary,
            $"scry {path} (--target <id-or-alias> | --descriptor <path>) [--request <file|->]",
            "JSON object from --request/--input, redirected stdin, or compatibility --json.",
            fields,
            result,
            $"scry {path} --target my-app --request {path}.json");

    private static CliCommand Execution(
        string path,
        string summary,
        string example,
        string result) =>
        new(
            path,
            path,
            "execution",
            summary,
            $"scry {path} (--target <id-or-alias> | --descriptor <path>) " +
            "[--source <file|-> | --request <file|->]",
            "C# from --source or stdin, or an ExecutionRequest JSON object from --request.",
            [
                F("source", "string", true, "C# source."),
                F("imports", "string[]", false, "Additional allowed namespaces."),
                F("references", "string[]", false, "Already-loaded compatible assembly names."),
                F("timeoutMilliseconds", "integer", false, "Cooperative target timeout."),
                F("marshal", "ui", false, "Run the submission on the host UI thread, which is what lets it touch a DependencyObject or a Control. Occupies that thread for the whole submission, so keep it short; timeoutMilliseconds cannot interrupt work already running there.")
            ],
            result,
            example);

    private static CliCommand Job(
        string path,
        string operation,
        string summary,
        IReadOnlyList<CliField> fields,
        string result) =>
        new(
            path,
            operation,
            "job",
            summary,
            $"scry {path} (--target <id-or-alias> | --descriptor <path>) --request <file|->",
            "JSON object from --request/--input or redirected stdin.",
            fields,
            result,
            $"scry {path} --target my-app --request {operation}.json");

    private static CliCommand Scenario(string path, string summary, string example) =>
        new(
            path,
            null,
            "scenario",
            summary,
            $"scry {path} --input <file|->",
            "ScenarioRequest JSON object from --input, --request-compatible stdin, or compatibility --json.",
            [
                F("mode", "sequential|concurrent", true, "Execution mode."),
                F("commands", "ScenarioCommand[]", true, "Ordered commands, each with one target or descriptor.")
            ],
            "ScenarioResult.",
            example);

    private static CliCommand Adapter(
        string operation,
        string summary,
        IReadOnlyList<CliField> fields,
        string result) =>
        new(
            operation,
            operation,
            "adapter",
            summary,
            $"scry {operation} (--target <id-or-alias> | --descriptor <path>) --request <file|->",
            "JSON object from --request/--input or redirected stdin.",
            fields,
            result,
            $"scry {operation} --target desktop-app --request {operation}.json");

    private static IReadOnlyList<CliField> WpfConditionFields(bool includeTiming)
    {
        var fields = new List<CliField>
        {
            F("root", "string", false, "Registered WPF root."),
            F("tree", "visual|logical", false, "Tree kind; defaults to visual."),
            F("path", "string", false, "Projected node path."),
            F("name", "string", false, "WPF element name."),
            F("automationId", "string", false, "Automation ID."),
            F(
                "state",
                "string",
                false,
                "exists, notExists, visible, enabled, loaded, focused, textEquals, " +
                "dataContextTypeEquals, or hasBindingError."),
            F("expected", "string", false, "Expected value for value-bearing states.")
        };
        if (includeTiming)
        {
            fields.Add(F("timeoutMilliseconds", "integer", false, "Bounded wait timeout."));
            fields.Add(F("pollIntervalMilliseconds", "integer", false, "Polling interval."));
        }

        return fields;
    }

    private static IReadOnlyList<CliField> WinFormsConditionFields(bool includeTiming)
    {
        var fields = new List<CliField>
        {
            F("root", "string", false, "Registered WinForms root."),
            F("path", "string", false, "Projected control path."),
            F("name", "string", false, "Control name."),
            F(
                "state",
                "string",
                false,
                "exists, notExists, visible, enabled, focused, or textEquals."),
            F("expected", "string", false, "Expected value for value-bearing states.")
        };
        if (includeTiming)
        {
            fields.Add(F("timeoutMilliseconds", "integer", false, "Bounded wait timeout."));
            fields.Add(F("pollIntervalMilliseconds", "integer", false, "Polling interval."));
        }

        return fields;
    }

    private static CliField F(string name, string type, bool required, string description) =>
        new(name, type, required, description);
}

internal sealed record CliCommand(
    string Path,
    string? Operation,
    string Kind,
    string Summary,
    string Usage,
    string Input,
    IReadOnlyList<CliField> Fields,
    string Result,
    string Example);

internal sealed record CliField(
    string Name,
    string Type,
    bool Required,
    string Description);

internal sealed record CliExitCode(int Code, string Name, string Meaning);

internal sealed class CliUsageException(string message) : Exception(message);
