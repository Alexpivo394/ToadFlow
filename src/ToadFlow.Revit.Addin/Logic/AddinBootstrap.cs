using System;
using System.Collections;
using System.Collections.Generic;
using ToadFlow.Contracts.Pipe;

namespace ToadFlow.Revit.Addin.Logic;

/// <summary>
///     Решает, работать ли аддину. Только Revit, который запустил агент, получает <c>TOADFLOW_INSTANCE_ID</c>;
///     Revit, открытый проектировщиком руками на той же машине, аддин не трогает.
/// </summary>
public static class AddinBootstrap
{
    public static Guid? InstanceId(IReadOnlyDictionary<string, string?> environment) =>
        environment.TryGetValue(PipeNames.InstanceIdVariable, out var value) && Guid.TryParse(value, out var id) ? id : null;

    public static Guid? InstanceIdFromProcess()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables()) environment[(string)entry.Key] = entry.Value as string;
        return InstanceId(environment);
    }
}
