namespace TelegramMediaBot.Services.Instagram;

/// <summary>
/// Tracks per-tier failure streaks so persistently broken tiers get skipped
/// instead of adding latency to every request. After 3 consecutive failures a
/// tier cools down for min(2^(n-3), 30) minutes. Health state must never cause
/// a hard failure — the orchestrator ignores cooldowns when every tier is down.
/// </summary>
public sealed class TierHealthTracker
{
    private sealed class State
    {
        public int ConsecutiveFailures;
        public DateTime CooldownUntil = DateTime.MinValue;
    }

    private const int FailuresBeforeCooldown = 3;
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(30);

    private readonly Dictionary<string, State> _states = new();
    private readonly object _lock = new();

    public bool IsAvailable(string tier)
    {
        lock (_lock)
        {
            return !_states.TryGetValue(tier, out var s) || DateTime.UtcNow >= s.CooldownUntil;
        }
    }

    public void RecordSuccess(string tier)
    {
        lock (_lock)
        {
            if (_states.TryGetValue(tier, out var s))
            {
                s.ConsecutiveFailures = 0;
                s.CooldownUntil = DateTime.MinValue;
            }
        }
    }

    public void RecordFailure(string tier)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(tier, out var s))
                _states[tier] = s = new State();

            s.ConsecutiveFailures++;
            if (s.ConsecutiveFailures >= FailuresBeforeCooldown)
            {
                var minutes = Math.Min(
                    Math.Pow(2, s.ConsecutiveFailures - FailuresBeforeCooldown),
                    MaxCooldown.TotalMinutes);
                s.CooldownUntil = DateTime.UtcNow.AddMinutes(minutes);
            }
        }
    }

    public string Describe(string tier)
    {
        lock (_lock)
        {
            if (!_states.TryGetValue(tier, out var s) || s.ConsecutiveFailures == 0) return "healthy";
            var cooling = DateTime.UtcNow < s.CooldownUntil
                ? $", cooling down until {s.CooldownUntil:HH:mm:ss} UTC"
                : "";
            return $"{s.ConsecutiveFailures} consecutive failures{cooling}";
        }
    }
}
