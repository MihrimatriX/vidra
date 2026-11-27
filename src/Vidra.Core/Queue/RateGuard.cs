using Vidra.Core.Models;

namespace Vidra.Core.Queue;

/// <summary>Tek bir başarısız denemeden sonra ne yapılacağı.</summary>
public enum RetryDecision
{
    /// <summary>Kendiliğinden denenmez: iş "Hata"da kalır.</summary>
    Fail,
    /// <summary>Yalnızca bu iş bir süre sonra yeniden denenir (ağ, 403).</summary>
    RetryJob,
    /// <summary>YouTube sınırı: tüm kuyruk soğumaya girer, iş soğuma bitince yeniden denenir.</summary>
    Cooldown,
}

/// <summary>Arayüzün tek bakışta göstereceği durum.</summary>
public sealed record RateGuardState(
    DateTimeOffset? CooldownUntil,
    ErrorKind? CooldownReason,
    int Strikes,
    int StartsLastHour,
    int HourlyBudget,
    bool Recovering)
{
    public bool InCooldown(DateTimeOffset now) => CooldownUntil is { } u && u > now;
    public bool NearBudget => StartsLastHour >= HourlyBudget * 8 / 10;
}

/// <summary>
/// YouTube sınırlarına karşı kuyruk geneli koruma (circuit breaker).
/// <list type="bullet">
/// <item>429 / bot doğrulaması / "try again later": aynı IP'den istek atmayı sürdürmek sınırı uzatır, bu yüzden
/// yeni iş başlatılmaz; bekleme 5 → 15 → 45 → 60 dk, ±%20 rastgele sapmayla.</item>
/// <item>Soğumadan sonra eşzamanlılık bir süre 1'e iner; ardışık başarılı işlerle normale döner.</item>
/// <item>Son 60 dakikada başlatılan iş sayılır: yt-dlp wiki'sine göre misafir için ~300, hesapla ~2000 video/saat.
/// Sınıra yaklaşınca nazik mod kendiliğinden "Güvenli"ye çıkar.</item>
/// </list>
/// Saat enjekte edilir: testler zamanı ileri sarabilir.
/// </summary>
public sealed class RateGuard(TimeProvider? clock = null, Random? random = null)
{
    public static readonly TimeSpan[] CooldownSteps =
        [TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(45), TimeSpan.FromMinutes(60)];

    /// <summary>İş başına ağ/403 yeniden deneme beklemeleri; sayısı en fazla deneme sayısıdır.</summary>
    public static readonly TimeSpan[] JobRetrySteps = [TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)];

    /// <summary>Soğumadan sonra eşzamanlılığın normale dönmesi için gereken ardışık başarılı iş.</summary>
    public const int RecoverAfter = 3;

    public const int GuestBudget = 300;
    public const int AccountBudget = 2000;

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly Random _random = random ?? Random.Shared;
    private readonly Lock _gate = new();
    private readonly Queue<DateTimeOffset> _starts = new();
    private DateTimeOffset? _until;
    private ErrorKind? _reason;
    private int _strikes;
    private int _successes;
    private bool _recovering;

    public event EventHandler? Changed;

    public DateTimeOffset Now => _clock.GetLocalNow();

    /// <summary>Bir işin kaçıncı kez denendiğine göre kendiliğinden yeniden denenip denenmeyeceği.</summary>
    public static RetryDecision Decide(ErrorKind kind, int autoRetries) => kind switch
    {
        // Bot doğrulaması çoğunlukla oturum/IP güveniyle ilgili: bir kez bekleyip denemek yeter, sonra kullanıcıya bırak
        ErrorKind.BotCheck when autoRetries < 1 => RetryDecision.Cooldown,
        ErrorKind.RateLimited when autoRetries < CooldownSteps.Length - 1 => RetryDecision.Cooldown,
        ErrorKind.Network or ErrorKind.Forbidden when autoRetries < JobRetrySteps.Length => RetryDecision.RetryJob,
        _ => RetryDecision.Fail,
    };

    public static TimeSpan JobRetryDelay(int autoRetries) => JobRetrySteps[Math.Clamp(autoRetries, 0, JobRetrySteps.Length - 1)];

    /// <summary>
    /// Sınır algılandı: soğumayı başlatır (ya da süren soğumayı olduğu gibi bırakır) ve bitiş zamanını döndürür.
    /// Aynı anda çalışan birkaç iş birlikte 429 alırsa bu tek bir "vuruş" sayılır.
    /// </summary>
    public DateTimeOffset Trip(ErrorKind reason)
    {
        DateTimeOffset until;
        lock (_gate)
        {
            var now = Now;
            if (_until is { } u && u > now) return u;
            var step = CooldownSteps[Math.Min(_strikes, CooldownSteps.Length - 1)];
            var jitter = 0.8 + _random.NextDouble() * 0.4;
            until = now + TimeSpan.FromSeconds(Math.Round(step.TotalSeconds * jitter));
            _until = until;
            _reason = reason;
            _strikes++;
            _successes = 0;
            _recovering = true;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return until;
    }

    /// <summary>Kullanıcı "Şimdi dene" dedi: soğuma biter, vuruş sayısı korunur (tekrar sınır gelirse daha uzun beklenir).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_until is null) return;
            _until = null;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordStart()
    {
        lock (_gate)
        {
            _starts.Enqueue(Now);
            Trim(Now);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void RecordSuccess()
    {
        var changed = false;
        lock (_gate)
        {
            if (!_recovering) return;
            if (++_successes >= RecoverAfter)
            {
                _recovering = false;
                _strikes = 0;
                _successes = 0;
                _reason = null;
                changed = true;
            }
        }
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
    }

    public bool InCooldown
    {
        get
        {
            lock (_gate) return _until is { } u && u > Now;
        }
    }

    public DateTimeOffset? CooldownUntil
    {
        get
        {
            lock (_gate) return _until is { } u && u > Now ? u : null;
        }
    }

    public RateGuardState State(bool signedIn)
    {
        lock (_gate)
        {
            var now = Now;
            Trim(now);
            var active = _until is { } u && u > now;
            return new(active ? _until : null, active || _recovering ? _reason : null, _strikes, _starts.Count,
                signedIn ? AccountBudget : GuestBudget, _recovering);
        }
    }

    /// <summary>Ayardaki eşzamanlılığın o anki üst sınırı.</summary>
    public int EffectiveConcurrency(AppSettings s)
    {
        var limit = Math.Max(1, s.Concurrency);
        var state = State(s.Cookies is not NoCookies);
        if (state.Recovering || state.NearBudget) return 1;
        // Oturumla yoğun indirme hesabı işaretletebilir: Güvenli modda aynı anda en fazla 2 iş
        if (s.EffectivePolite == PoliteLevel.Safe && s.Cookies is not NoCookies) return Math.Min(limit, 2);
        return limit;
    }

    /// <summary>Saatlik bütçeye yaklaşıldıysa ya da soğumadan yeni çıkıldıysa nazik mod en az "Güvenli" olur.</summary>
    public PoliteLevel EffectivePolite(AppSettings s)
    {
        var level = s.EffectivePolite;
        if (level == PoliteLevel.Off) return level;
        var state = State(s.Cookies is not NoCookies);
        return state.Recovering || state.NearBudget ? PoliteLevel.Safe : level;
    }

    private void Trim(DateTimeOffset now)
    {
        while (_starts.Count > 0 && now - _starts.Peek() > TimeSpan.FromHours(1)) _starts.Dequeue();
    }
}
