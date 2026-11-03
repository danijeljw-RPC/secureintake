namespace SecureIntake.Domain.Common;

/// <summary>Immutable allowlist of transitions. Anything not listed is rejected.</summary>
public sealed class StateMachine<TState> where TState : struct, Enum
{
    private readonly HashSet<(TState From, TState To)> _allowed;
    private readonly string _codePrefix;

    public StateMachine(string codePrefix, params (TState From, TState To)[] transitions)
    {
        _codePrefix = codePrefix;
        _allowed = [.. transitions];
    }

    public bool CanTransition(TState from, TState to) => _allowed.Contains((from, to));

    public void EnsureTransition(TState from, TState to)
    {
        if (!CanTransition(from, to))
        {
            throw new DomainException($"{_codePrefix}.invalid_transition", $"Cannot move {_codePrefix} from {from} to {to}.");
        }
    }

    public IEnumerable<TState> NextStates(TState from) => _allowed.Where(t => t.From.Equals(from)).Select(t => t.To);
}
