namespace tsky;

public sealed class DataflowGraphService
{
    public sealed class ConnectorTooltipItem
    {
        public required bool Resolved { get; init; }

        public string? SourceTaskName { get; init; }

        public string? SourceOutputName { get; init; }

        public required string TargetTaskName { get; init; }

        public required string TargetInputName { get; init; }

        public required string BindingReference { get; init; }

        public required string StatusDetail { get; init; }
    }

    public sealed class ConnectorRelationItem
    {
        public required bool Resolved { get; init; }

        public string? SourceTaskName { get; init; }

        public required string TargetTaskName { get; init; }
    }

    public sealed class ConnectorComputationItem
    {
        public required int TargetTaskIndex { get; init; }

        public required string TargetTaskName { get; init; }

        public required string TargetInputName { get; init; }

        public required string BindingReference { get; init; }

        public int? SourceTaskIndex { get; init; }

        public string? SourceTaskName { get; init; }

        public string? SourceOutputName { get; init; }

        public required bool Resolved { get; init; }

        public required string StatusDetail { get; init; }
    }

    public bool TryParseBindingPortReference(string reference, out string portName)
    {
        portName = string.Empty;
        if (string.IsNullOrWhiteSpace(reference) || !reference.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        portName = reference[5..].Trim();
        return !string.IsNullOrWhiteSpace(portName);
    }

    public bool TryParseBindingTaskOutputReference(string reference, out string taskName, out string outputName)
    {
        taskName = string.Empty;
        outputName = string.Empty;

        if (string.IsNullOrWhiteSpace(reference)
            || reference.StartsWith("port:", StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith("input:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalizedReference = reference.StartsWith("output:", StringComparison.OrdinalIgnoreCase)
            ? reference[7..].Trim()
            : reference;

        var dotIndex = normalizedReference.IndexOf('.');
        if (dotIndex <= 0 || dotIndex >= normalizedReference.Length - 1)
        {
            return false;
        }

        taskName = normalizedReference[..dotIndex].Trim();
        outputName = normalizedReference[(dotIndex + 1)..].Trim();
        return !string.IsNullOrWhiteSpace(taskName) && !string.IsNullOrWhiteSpace(outputName);
    }

    public bool ValidateInputBindingsAgainstPreviousTasks(
        IReadOnlyList<LauncherTask> tasks,
        IReadOnlyList<int> visibleTaskIndexes,
        int taskIndex,
        IReadOnlyDictionary<string, string> bindings,
        out string error)
    {
        error = string.Empty;
        var position = -1;
        for (var i = 0; i < visibleTaskIndexes.Count; i++)
        {
            if (visibleTaskIndexes[i] == taskIndex)
            {
                position = i;
                break;
            }
        }

        if (position < 0)
        {
            return true;
        }

        var previousTasks = visibleTaskIndexes
            .Take(position)
            .Where(index => index >= 0 && index < tasks.Count)
            .Select(index => tasks[index])
            .ToList();

        foreach (var pair in bindings)
        {
            var binding = pair.Value;
            if (string.IsNullOrWhiteSpace(binding))
            {
                continue;
            }

            if (binding.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
            {
                var portName = binding[5..].Trim();
                var found = previousTasks.Any(task => task.Outputs.ContainsKey(portName));
                if (!found)
                {
                    error = $"Input '{pair.Key}' binding '{binding}' does not match any previous output port.";
                    return false;
                }

                continue;
            }

            if (TryParseBindingTaskOutputReference(binding, out var sourceTaskName, out var sourceOutputName))
            {
                var matchingTasks = previousTasks
                    .Where(task => string.Equals(task.Name.Trim(), sourceTaskName, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (matchingTasks.Count > 1)
                {
                    error = $"Input '{pair.Key}' binding '{binding}' is ambiguous because multiple previous tasks are named '{sourceTaskName}'. Rename tasks or use unique task names.";
                    return false;
                }

                var found = matchingTasks.Any(task => task.Outputs.ContainsKey(sourceOutputName));
                if (!found)
                {
                    error = $"Input '{pair.Key}' binding '{binding}' does not match any previous task output.";
                    return false;
                }

                continue;
            }

            error = $"Input '{pair.Key}' binding '{binding}' uses an unsupported format. Use 'port:OutputName' or 'TaskName.OutputName'.";
            return false;
        }

        return true;
    }

    public HashSet<int> ComputeInvalidPortBindingTaskIndexes(IReadOnlyList<LauncherTask> tasks, IReadOnlyList<int> visibleTaskIndexes)
    {
        var invalid = new HashSet<int>();
        var seenPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenTaskOutputs = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var taskIndex in visibleTaskIndexes)
        {
            if (taskIndex < 0 || taskIndex >= tasks.Count)
            {
                continue;
            }

            var task = tasks[taskIndex];
            var taskName = task.Name.Trim();
            var outputNames = new HashSet<string>(
                task.Outputs.Keys.Where(key => !string.IsNullOrWhiteSpace(key)).Select(key => key.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var isInvalid = false;
            foreach (var binding in task.InputBindings.Where(pair => !string.IsNullOrWhiteSpace(pair.Key)))
            {
                var reference = binding.Value?.Trim() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                if (reference.StartsWith("port:", StringComparison.OrdinalIgnoreCase))
                {
                    var portName = reference[5..].Trim();
                    if (!seenPorts.Contains(portName))
                    {
                        isInvalid = true;
                        break;
                    }

                    continue;
                }

                if (TryParseBindingTaskOutputReference(reference, out var sourceTaskName, out var sourceOutputName))
                {
                    var matchingNames = seenTaskOutputs.Keys
                        .Where(name => string.Equals(name, sourceTaskName, StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (matchingNames.Count != 1)
                    {
                        isInvalid = true;
                        break;
                    }

                    if (!seenTaskOutputs.TryGetValue(matchingNames[0], out var sourceOutputs) || !sourceOutputs.Contains(sourceOutputName))
                    {
                        isInvalid = true;
                        break;
                    }

                    continue;
                }

                isInvalid = true;
                break;
            }

            if (isInvalid)
            {
                invalid.Add(taskIndex);
            }

            foreach (var outputName in outputNames)
            {
                seenPorts.Add(outputName);
            }

            seenTaskOutputs[taskName] = outputNames;
        }

        return invalid;
    }

    public List<ConnectorComputationItem> BuildConnectorComputations(IReadOnlyList<LauncherTask> tasks, IReadOnlyList<int> visibleTaskIndexes)
    {
        var selectedGroupTasks = visibleTaskIndexes
            .Where(index => index >= 0 && index < tasks.Count)
            .Select(index => new { Index = index, Task = tasks[index] })
            .ToList();

        var taskByName = selectedGroupTasks
            .Where(item => !string.IsNullOrWhiteSpace(item.Task.Name))
            .GroupBy(item => item.Task.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        var connectors = new List<ConnectorComputationItem>();

        for (var targetOrder = 0; targetOrder < selectedGroupTasks.Count; targetOrder++)
        {
            var target = selectedGroupTasks[targetOrder];

            var bindingEntries = target.Task.InputBindings
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
                .Select(pair => new
                {
                    InputName = pair.Key.Trim(),
                    Reference = pair.Value?.Trim() ?? string.Empty
                })
                .ToList();

            foreach (var binding in bindingEntries)
            {
                string? sourceTaskName = null;
                string? sourceOutputName = null;
                int? sourceTaskIndex = null;
                var resolved = false;
                var status = string.Empty;

                if (TryParseBindingTaskOutputReference(binding.Reference, out var explicitSourceTaskName, out var explicitSourceOutputName))
                {
                    sourceTaskName = explicitSourceTaskName;
                    sourceOutputName = explicitSourceOutputName;

                    if (taskByName.TryGetValue(sourceTaskName, out var sourceTaskInfo))
                    {
                        sourceTaskIndex = sourceTaskInfo.Index;
                        resolved = sourceTaskInfo.Task.Outputs.ContainsKey(sourceOutputName);
                        status = resolved
                            ? "Binding resolved."
                            : $"Source task '{sourceTaskName}' does not declare output '{sourceOutputName}'.";
                    }
                    else
                    {
                        status = "Binding source task could not be matched in this group.";
                    }
                }
                else if (TryParseBindingPortReference(binding.Reference, out var boundPortName))
                {
                    sourceOutputName = boundPortName;

                    for (var sourceOrder = targetOrder - 1; sourceOrder >= 0; sourceOrder--)
                    {
                        var candidate = selectedGroupTasks[sourceOrder];
                        if (!candidate.Task.Outputs.ContainsKey(boundPortName))
                        {
                            continue;
                        }

                        sourceTaskName = candidate.Task.Name;
                        sourceTaskIndex = candidate.Index;
                        resolved = true;
                        status = $"Port binding resolved from nearest upstream output '{boundPortName}'.";
                        break;
                    }

                    if (!resolved)
                    {
                        status = $"No upstream task output found for port '{boundPortName}'.";
                    }
                }
                else
                {
                    status = string.IsNullOrWhiteSpace(binding.Reference)
                        ? "Binding reference is empty."
                        : "Binding could not be matched to a task output in this group.";
                }

                connectors.Add(new ConnectorComputationItem
                {
                    TargetTaskIndex = target.Index,
                    TargetTaskName = target.Task.Name,
                    TargetInputName = binding.InputName,
                    BindingReference = binding.Reference,
                    SourceTaskIndex = sourceTaskIndex,
                    SourceTaskName = sourceTaskName,
                    SourceOutputName = sourceOutputName,
                    Resolved = resolved,
                    StatusDetail = status
                });
            }
        }

        return connectors;
    }

    public HashSet<int> ComputeUpstreamAndDownstreamConnectorIndexes(IReadOnlyList<ConnectorRelationItem> connectors, int focalIndex)
    {
        var result = new HashSet<int>();
        if (focalIndex < 0 || focalIndex >= connectors.Count)
        {
            return result;
        }

        var focal = connectors[focalIndex];

        var upstreamFrontier = new Queue<string>();
        var upstreamVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(focal.SourceTaskName))
        {
            upstreamFrontier.Enqueue(focal.SourceTaskName);
            upstreamVisited.Add(focal.SourceTaskName);
        }

        while (upstreamFrontier.Count > 0)
        {
            var taskName = upstreamFrontier.Dequeue();

            for (var i = 0; i < connectors.Count; i++)
            {
                if (i == focalIndex)
                {
                    continue;
                }

                var connector = connectors[i];
                if (!connector.Resolved || !string.Equals(connector.TargetTaskName, taskName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(i);

                if (!string.IsNullOrWhiteSpace(connector.SourceTaskName) && upstreamVisited.Add(connector.SourceTaskName))
                {
                    upstreamFrontier.Enqueue(connector.SourceTaskName);
                }
            }
        }

        var downstreamFrontier = new Queue<string>();
        var downstreamVisited = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            focal.TargetTaskName
        };

        downstreamFrontier.Enqueue(focal.TargetTaskName);

        while (downstreamFrontier.Count > 0)
        {
            var taskName = downstreamFrontier.Dequeue();

            for (var i = 0; i < connectors.Count; i++)
            {
                if (i == focalIndex)
                {
                    continue;
                }

                var connector = connectors[i];
                if (!connector.Resolved
                    || string.IsNullOrWhiteSpace(connector.SourceTaskName)
                    || !string.Equals(connector.SourceTaskName, taskName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                result.Add(i);

                if (downstreamVisited.Add(connector.TargetTaskName))
                {
                    downstreamFrontier.Enqueue(connector.TargetTaskName);
                }
            }
        }

        return result;
    }

    public string BuildConnectorToolTipText(ConnectorTooltipItem connector)
    {
        var source = string.IsNullOrWhiteSpace(connector.SourceTaskName)
            ? "(unresolved source)"
            : $"{connector.SourceTaskName}.{connector.SourceOutputName}";

        var statusLine = connector.Resolved ? "Resolved binding" : "Unresolved binding";

        return string.Join(Environment.NewLine,
        [
            statusLine,
            $"From: {source}",
            $"To: {connector.TargetTaskName}.{connector.TargetInputName}",
            $"Reference: {connector.BindingReference}",
            $"Detail: {connector.StatusDetail}"
        ]);
    }
}
