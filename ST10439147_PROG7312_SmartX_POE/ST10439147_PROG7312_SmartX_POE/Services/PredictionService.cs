using SmartX.Shared.Telemetry;

namespace SmartX.Api.Services;

public enum OperatorActionKind
{
    Search,
    Command
}

/// <summary>
/// One thing the operator did, and the faults that were open when they did it.
/// Each entry in <see cref="Contexts"/> is an "after event X" half of a prediction pair,
/// written as <c>Category|AlertKind</c> (e.g. <c>SoilMoisture|ThresholdBreach</c>) so a
/// breach on any moisture probe counts as the same event. Empty when nothing was wrong.
/// </summary>
public sealed record OperatorAction(
    DateTimeOffset At,
    OperatorActionKind Kind,
    string Target,
    IReadOnlyList<string> Contexts)
{
    /// <summary>The "the operator did Y" half of the pair: <c>Search:pump</c>, <c>Command:Switch Borehole Pump on</c>.</summary>
    public string FollowUp => $"{Kind}:{Target}";
}

/// <summary>
/// The predictive action engine. Every search and command lands here with the alert
/// context of the moment it was made.
///
/// <para>Learning is a dictionary of counters: event X maps to a second dictionary of
/// follow-up Y to the number of times the operator did Y after X. Recording an action is
/// two O(1) lookups and an increment; ranking an event's follow-ups sorts only that
/// event's row, which holds the handful of things operators actually do in response to it.</para>
/// </summary>
// ponytail: the log keeps the last MaxLog actions only - enough to inspect what was learned.
// The counters are not capped and outlive the log. Persist both if predictions have to survive a restart.
// ponytail: an action counts every time it is made, so searching "pump" twice during one alert
// counts twice. Count once per alert id if repeat clicks start skewing the ranking.
// Reference: Microsoft. Dictionary<TKey,TValue> Class. Microsoft Learn.
//   https://learn.microsoft.com/dotnet/api/system.collections.generic.dictionary-2
public sealed class PredictionService(TelemetryStore store, AnomalyEngine anomalies)
{
    public const int MaxLog = 500;

    /// <summary>How long a suggestion stays up after its alert was raised.</summary>
    public static readonly TimeSpan SuggestionLifetime = TimeSpan.FromMinutes(2);

    /// <summary>
    /// A follow-up has to have happened this many times after an event before it is
    /// suggested. One search could be a coincidence; two is the start of a habit.
    /// </summary>
    public const int MinOccurrences = 2;

    private readonly Queue<OperatorAction> _log = new();
    private Suggestion? _latest;
    private readonly Dictionary<string, Dictionary<string, int>> _pairs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>Logged actions, newest first.</summary>
    public IReadOnlyList<OperatorAction> Log
    {
        get { lock (_gate) return _log.Reverse().ToList(); }
    }

    /// <summary>Every event seen so far with its follow-ups ranked, for inspecting what has been learned.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<RankedFollowUp>> Pairs
    {
        get
        {
            lock (_gate)
                return _pairs.Keys.Order().ToDictionary(x => x, x => RankLocked(x, int.MaxValue));
        }
    }

    /// <summary>The <paramref name="top"/> follow-ups to event X, most frequent first. Empty for an event never seen.</summary>
    public IReadOnlyList<RankedFollowUp> Rank(string context, int top = 3)
    {
        lock (_gate) return RankLocked(context, top);
    }

    // Ties go to the follow-up's name so the order is stable between calls.
    private IReadOnlyList<RankedFollowUp> RankLocked(string context, int top)
        => _pairs.TryGetValue(context, out var row)
            ? row.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal)
                 .Take(top).Select(p => new RankedFollowUp(p.Key, p.Value)).ToList()
            : [];
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>Logs one action against every fault that is open right now.</summary>
    public OperatorAction Record(OperatorActionKind kind, string target)
    {
        // "Pump" and "pump " are the same search; command labels are generated, so already stable.
        target = kind == OperatorActionKind.Search ? target.Trim().ToLowerInvariant() : target;
        var action = new OperatorAction(DateTimeOffset.UtcNow, kind, target, OpenContexts());

        lock (_gate)
        {
            _log.Enqueue(action);
            if (_log.Count > MaxLog) _log.Dequeue();

            // "After event X, the operator did Y": one more for each open X. Nothing open, no pair.
            foreach (var context in action.Contexts)
            {
                if (!_pairs.TryGetValue(context, out var row)) _pairs[context] = row = new(StringComparer.OrdinalIgnoreCase);
                row[action.FollowUp] = row.GetValueOrDefault(action.FollowUp) + 1;
            }
        }

        return action;
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// Event X has happened again: the follow-ups the operator has made a habit of after it,
    /// or null when there is nothing worth suggesting yet. The result is also kept as
    /// <see cref="Current"/> so a dashboard that opens mid-alert still gets it.
    /// </summary>
    public Suggestion? Suggest(Alert alert)
    {
        if (!Actionable(alert) || !store.TryGet(alert.SensorId, out var sensor)) return null;

        var context = ContextKey(sensor, alert);
        var habits = Rank(context).Where(f => f.Count >= MinOccurrences).ToList();
        if (habits.Count == 0) return null;

        var suggestion = new Suggestion(alert.Id, context, sensor.DisplayName, alert.RaisedAt, habits);
        lock (_gate) _latest = suggestion;
        return suggestion;
    }

    /// <summary>The last suggestion made, while its alert is still recent enough to be driving the operator.</summary>
    public Suggestion? Current
    {
        get
        {
            lock (_gate) return _latest is { } s && s.RaisedAt >= DateTimeOffset.UtcNow - SuggestionLifetime ? s : null;
        }
    }
    //----------------------------------------------------------------------------------------------------------------------//
    /// <summary>
    /// The faults still going on: nodes in the disconnect set, and spikes and breaches in
    /// the open-error set. "Most recent alert" is the wrong question on a busy mesh - a
    /// one-reading spike elsewhere lands seconds after the moisture alert and closes again
    /// at once, while the moisture problem the operator is reacting to is still open.
    /// Kinds the operator has suppressed are left out: they have said those are noise.
    /// </summary>
    private IReadOnlyList<string> OpenContexts()
    {
        var suppressed = anomalies.Ranked()
            .Where(a => a.Suppressed)
            .Select(a => $"{a.SensorId}|{a.Kind}")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var open = anomalies.Disconnected.Select(id => $"{id}|{AlertKind.Silence}").Concat(anomalies.ActiveErrors);

        var contexts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in open.Where(k => !suppressed.Contains(k)))
        {
            var bar = key.IndexOf('|');
            if (store.TryGet(key[..bar], out var sensor)) contexts.Add($"{sensor.Category}|{key[(bar + 1)..]}");
        }

        return contexts.ToList();
    }

    private static bool Actionable(Alert alert) => !alert.Suppressed && alert.Kind != AlertKind.StateChange;

    private static string ContextKey(SensorRegistration sensor, Alert alert) => $"{sensor.Category}|{alert.Kind}";
}
//---------------------------DDDDDDDDDDDoooooooooooooo END OF FILE DDDDDDDDDDDoooooooooooooo---------------------------//
