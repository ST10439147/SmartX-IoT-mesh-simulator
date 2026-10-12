namespace SmartX.Api.Services;

/// <summary>
/// What one command did to one actuator. <see cref="Previous"/> is the override that was
/// in force before it - null means the device was running on its own (auto) - and is
/// exactly what Undo puts back.
/// </summary>
public sealed record DeviceChange(string SensorId, bool? Previous, bool Target);

/// <summary>
/// One manual override as the operator issued it. "Shut all valves" is a single command
/// with one change per valve, so a single Undo reverts the whole action.
/// </summary>
public sealed record OverrideCommand(long Id, string Label, DateTimeOffset IssuedAt, IReadOnlyList<DeviceChange> Changes);

/// <summary>
/// Manual override commands and their undo history.
///
/// <para>An override pins an actuator on or off until it is undone; the device reads it
/// through <see cref="TryGetOverride"/> and obeys. Every command issued is pushed onto a
/// <see cref="Stack{T}"/>, and Undo pops the top one - last in, first out - which is the
/// only order in which reverting is safe: each command remembers the state it replaced,
/// so they have to be peeled off newest first.</para>
/// </summary>
// ponytail: the history is unbounded - fine at the rate an operator clicks. Cap it (drop
// the oldest) if commands are ever issued by automation.
// Reference: Microsoft. Stack<T> Class. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.collections.generic.stack-1
public sealed class CommandService
{
    private readonly Stack<OverrideCommand> _history = new();
    private readonly Dictionary<string, bool> _overrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    private long _nextId;

    /// <summary>Undo history, most recent command first - the order Undo will take them in.</summary>
    public IReadOnlyList<OverrideCommand> History
    {
        get { lock (_gate) return _history.ToArray(); }
    }

    /// <summary>Every actuator currently pinned by an override, and the state it is pinned to.</summary>
    public IReadOnlyDictionary<string, bool> Overrides
    {
        get { lock (_gate) return new Dictionary<string, bool>(_overrides, _overrides.Comparer); }
    }

    public bool TryGetOverride(string sensorId, out bool state)
    {
        lock (_gate) return _overrides.TryGetValue(sensorId, out state);
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Pins every target to <paramref name="state"/> and pushes the command onto the undo stack.</summary>
    public OverrideCommand Issue(string label, IEnumerable<string> sensorIds, bool state)
    {
        lock (_gate)
        {
            var changes = new List<DeviceChange>();

            foreach (var id in sensorIds)
            {
                changes.Add(new DeviceChange(id, _overrides.TryGetValue(id, out var previous) ? previous : null, state));
                _overrides[id] = state;
            }

            var command = new OverrideCommand(++_nextId, label, DateTimeOffset.UtcNow, changes);
            _history.Push(command);
            return command;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Pops the most recent command and puts every device it touched back as it was.
    /// False when there is nothing to undo - an empty stack is an answer, not an exception.
    /// </summary>
    public bool TryUndo(out OverrideCommand? undone)
    {
        lock (_gate)
        {
            if (!_history.TryPop(out undone)) return false;

            foreach (var change in undone.Changes)
            {
                if (change.Previous is { } previous) _overrides[change.SensorId] = previous;
                else _overrides.Remove(change.SensorId);
            }

            return true;
        }
    }
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
