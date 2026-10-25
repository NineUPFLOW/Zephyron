using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using Zephyron.Core;

namespace Zephyron.Gui;

/// <summary>Что слой должен показывать о сети: от него зависят пакеты по связям.</summary>
public enum FieldMode
{
    /// <summary>Обход выключен или о здоровье ничего не известно: почти покой.</summary>
    Off,

    /// <summary>Запуск: узлы разгораются, пакеты бегут от левого края.</summary>
    Starting,

    /// <summary>Работает: ровный ток пакетов.</summary>
    Running,

    /// <summary>Частично: редкие приглушённые пакеты.</summary>
    Degraded,

    /// <summary>Сбой: как «Выключен», раз в несколько секунд гаснущий пакет.</summary>
    Failed,
}

/// <summary>
/// Фоновый слой «сеть»: дрейфующие узлы, связи, курсор как узел, искры
/// клика и пакеты по связям (NEXUS, спека 8.1).
/// </summary>
/// <remarks>
/// <para>
/// Рисует сам в <see cref="OnRender"/>, как NetChart, и ничего не создаёт в
/// кадре: узлы, пакеты и искры — массивы структур заранее, кисти и
/// перья — замороженные, восемь ступеней альфы на роль. Тема сменилась —
/// цвет ключа изменился, кэш пересобирается на ближайшем кадре.
/// </para>
/// <para>
/// Цикл (подписка на <c>CompositionTarget.Rendering</c>) идёт, только пока
/// слой загружен, виден, окно активно и не свёрнуто, а профиль не Lite.
/// Через 8 с без движения мыши узлы замедляются до нуля за 3 с и цикл
/// снимается: будят <see cref="Pointer"/>, <see cref="Burst"/>, смена
/// <see cref="Mode"/> и <see cref="Motion.Changed"/>. Статические события
/// (<c>Rendering</c>, <c>Motion.Changed</c>) держат подписчика, поэтому
/// подписка живёт ровно между Loaded и Unloaded/Closed.
/// </para>
/// </remarks>
public sealed class ParticleField : FrameworkElement
{
    private const int MaxNodes = 110, MaxPackets = 6, MaxSparks = 8, MaxLines = 150;
    private const double SleepAfter = 8, SleepFade = 3;

    private static readonly bool Shots = Environment.GetEnvironmentVariable("ZEPHYRON_SHOTS") is { Length: > 0 };

    // Роли отрисовки: ключ цвета темы и наибольшая альфа (тёмная / светлая тема).
    private enum Role { Node, Link, Cursor, Spark, SparkText, Packet, PacketStart, PacketWarn, PacketDanger }

    private static readonly string[] Keys = ["Faint", "Text", "Accent", "Accent", "Text", "Accent2", "Accent", "Warn", "Danger"];
    private static readonly double[] MaxDark = [.30, .07, .24, .9, .9, .80, .60, .50, .80];
    private static readonly double[] MaxLight = [.35, .06, .24, .9, .9, .80, .60, .50, .80];

    private struct Packet
    {
        public int From, To, Hop, Hops;
        public double T;
        public Role Role;
    }

    private struct Spark
    {
        public double X, Y, Dx, Dy, Age, Dist;
    }

    private readonly Random _rng;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly double[] _x = new double[MaxNodes], _y = new double[MaxNodes];
    private readonly double[] _vx = new double[MaxNodes], _vy = new double[MaxNodes];
    private readonly double[] _kx = new double[MaxNodes], _ky = new double[MaxNodes];
    private readonly double[] _size = new double[MaxNodes];
    private readonly Packet[] _packets = new Packet[MaxPackets];
    private readonly Spark[] _sparks = new Spark[MaxSparks];
    private readonly int[] _near = new int[6];
    private readonly double[] _nearD = new double[6];

    private readonly Pen?[,] _pens = new Pen?[10, 8];
    private readonly Brush?[,] _brushes = new Brush?[10, 8];
    private readonly Color[] _colors = new Color[10];
    private bool _cacheBuilt, _lightTheme;

    private int _count, _nPackets;
    private double _w, _h;
    private TimeSpan _last, _lastDraw, _lastMove;
    private double _spawnAcc, _failAcc, _glow;
    private bool _subscribed, _hooked, _cursorOn;
    private double _px, _py, _hx, _hy, _cursorA;
    private double _dim = 1;
    private FieldMode _mode;
    private Window? _window;

    public ParticleField() : this(Random.Shared)
    {
    }

    /// <summary>Для проверок: одинаковый seed — одинаковая раскладка узлов.</summary>
    internal ParticleField(int seed) : this(new Random(seed))
    {
    }

    private ParticleField(Random rng)
    {
        _rng = rng;

        for (int i = 0; i < MaxSparks; i++)
            _sparks[i].Age = 1;

        IsHitTestVisible = false;
        Focusable = false;
        SnapsToDevicePixels = false;

        Loaded += (_, _) => Attach();
        Unloaded += (_, _) => Detach();
        IsVisibleChanged += (_, _) => Wake();
        SizeChanged += (_, e) => Resize(e.NewSize.Width, e.NewSize.Height);
    }

    /// <summary>Что показывать о сети; смена будит цикл.</summary>
    public FieldMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value)
                return;

            _mode = value;
            _failAcc = 0;
            _spawnAcc = 0;
            Wake();
        }
    }

    /// <summary>
    /// Скорость, Мбит/с — только если её реально отдали (StatusView); null —
    /// частота пакетов фиксированная. Придуманной скорости не бывает.
    /// </summary>
    public double? Mbps { get; set; }

    /// <summary>Множитель альфы всего слоя: для тем с фоновой картинкой.</summary>
    public double Dim
    {
        get => _dim;
        set
        {
            value = Math.Clamp(value, 0, 1);

            if (value == _dim)
                return;

            _dim = value;
            _cacheBuilt = false;
            InvalidateVisual();
        }
    }

    /// <summary>Подписан ли слой на кадры сейчас (для проверок).</summary>
    internal bool IsRunning => _subscribed;

    /// <summary>Сколько узлов в профиле.</summary>
    internal static int NodeCount(EffectsProfile profile) => profile switch
    {
        EffectsProfile.Full => 110,
        EffectsProfile.Balanced => 50,
        _ => 0,
    };

    internal int ActiveNodes => _count;

    // ---- параметры профиля ------------------------------------------------

    private static double Fps(EffectsProfile p) => p == EffectsProfile.Full ? 30 : 20;

    private static double LinkRadius(EffectsProfile p) => p == EffectsProfile.Full ? 120 : 100;

    // ---- ввод --------------------------------------------------------------

    /// <summary>Курсор над слоем; координаты в системе слоя. Дёшево: вызывается на каждое движение.</summary>
    public void Pointer(Point p)
    {
        if (Motion.Profile == EffectsProfile.Lite || _w <= 0)
            return;

        if (!_cursorOn)
        {
            _hx = p.X;
            _hy = p.Y;
        }

        _px = p.X;
        _py = p.Y;
        _cursorOn = true;
        Wake();
    }

    /// <summary>Курсор ушёл: его связи гаснут за 300 мс.</summary>
    public void Leave() => _cursorOn = false;

    /// <summary>Клик: искры (профиль не «Лёгкий»).</summary>
    public void Burst(Point p)
    {
        if (Motion.Profile == EffectsProfile.Lite || _w <= 0)
            return;

        var profile = Motion.Profile;
        int n = profile == EffectsProfile.Full ? 8 : 5;

        for (int i = 0; i < n; i++)
        {
            double a = (i + _rng.NextDouble() * .8) * (Math.PI * 2 / n);
            _sparks[i] = new Spark
            {
                X = p.X, Y = p.Y, Dx = Math.Cos(a), Dy = Math.Sin(a),
                Dist = 18 + _rng.NextDouble() * 18,
            };
        }

        for (int i = n; i < MaxSparks; i++)
            _sparks[i].Age = 1;

        Wake();
    }

    // ---- жизненный цикл ----------------------------------------------------

    private void Attach()
    {
        if (_hooked)
            return;

        _hooked = true;
        Motion.Changed += OnMotionChanged;
        _window = Window.GetWindow(this);

        if (_window is not null)
        {
            _window.Activated += OnWindowState;
            _window.Deactivated += OnWindowState;
            _window.StateChanged += OnWindowState;
            _window.Closed += OnWindowClosed;
        }

        Resize(ActualWidth, ActualHeight);
        Wake();
    }

    private void Detach()
    {
        if (!_hooked)
            return;

        _hooked = false;
        Motion.Changed -= OnMotionChanged;

        if (_window is not null)
        {
            _window.Activated -= OnWindowState;
            _window.Deactivated -= OnWindowState;
            _window.StateChanged -= OnWindowState;
            _window.Closed -= OnWindowClosed;
            _window = null;
        }

        Stop();
    }

    private void OnWindowClosed(object? sender, EventArgs e) => Detach();

    private void OnWindowState(object? sender, EventArgs e) => Wake();

    private void OnMotionChanged(object? sender, EventArgs e)
    {
        // Событие может прийти с чужого потока (смена уровня отрисовки).
        if (!CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnMotionChanged(sender, e));
            return;
        }

        _cacheBuilt = false;
        Resize(_w, _h);
        InvalidateVisual();
        Wake();
    }

    private bool ShouldRun() =>
        !Shots && _hooked && IsLoaded && IsVisible && _window is { WindowState: not WindowState.Minimized }
        && (ActiveOverride ?? _window.IsActive) && Motion.Profile != EffectsProfile.Lite;

    /// <summary>Для проверок: окно теста не бывает активным по-настоящему.</summary>
    internal bool? ActiveOverride { get; set; }

    /// <summary>Пробуждение: заново отсчитывает покой и запускает цикл, если можно.</summary>
    private void Wake()
    {
        if (!CheckAccess())
            return;

        _lastMove = _clock.Elapsed;

        if (ShouldRun())
            Start();
        else
            Stop();
    }

    private void Start()
    {
        if (_subscribed)
            return;

        _subscribed = true;
        _last = _lastDraw = _clock.Elapsed;
        CompositionTarget.Rendering += OnFrame;
    }

    private void Stop()
    {
        if (!_subscribed)
            return;

        _subscribed = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    // ---- геометрия -----------------------------------------------------------

    private void Resize(double w, double h)
    {
        int want = NodeCount(Motion.Profile);
        double ow = _w, oh = _h;

        // NaN/∞ — как нулевой размер: иначе узлы умножатся на NaN и останутся им навсегда.
        if (!double.IsFinite(w) || !double.IsFinite(h))
            w = h = 0;

        _w = w;
        _h = h;

        if (w <= 0 || h <= 0)
        {
            _count = Math.Min(want, _count);
            return;
        }

        // Узлы переезжают пропорционально, массивы остаются теми же.
        if (ow > 0 && oh > 0)
        {
            for (int i = 0; i < _count; i++)
            {
                _x[i] *= w / ow;
                _y[i] *= h / oh;
            }
        }

        bool fresh = ow <= 0 || oh <= 0;
        int from = fresh ? 0 : Math.Min(_count, want);

        for (int i = from; i < want; i++)
            Spawn(i);

        _count = want;
        _nPackets = 0;
    }

    private void Spawn(int i)
    {
        var p = Motion.Profile;
        double max = p == EffectsProfile.Full ? 12 : 10;
        double speed = 4 + _rng.NextDouble() * (max - 4), a = _rng.NextDouble() * Math.PI * 2;

        _x[i] = _rng.NextDouble() * _w;
        _y[i] = _rng.NextDouble() * _h;
        _vx[i] = Math.Cos(a) * speed;
        _vy[i] = Math.Sin(a) * speed;
        _kx[i] = _ky[i] = 0;
        _size[i] = .9 + _rng.NextDouble() * .9;
    }

    // ---- кадр: расчёт ----------------------------------------------------------

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var profile = Motion.Profile;
        double period = 1000 / Fps(profile) * .9;

        // Кадры монитора идут чаще: лишние пропускаем, фаза — по реальному времени.
        if ((now - _lastDraw).TotalMilliseconds < period)
            return;

        double dt = Math.Min(.1, (now - _last).TotalSeconds);
        _last = _lastDraw = now;

        double idle = (now - _lastMove).TotalSeconds;
        double calm = idle < SleepAfter ? 1 : Math.Max(0, 1 - (idle - SleepAfter) / SleepFade);

        if (calm <= 0)
        {
            // Кадр покоя уже нарисован; всё мимолётное гасим, чтобы не застыло.
            _nPackets = 0;

            for (int i = 0; i < MaxSparks; i++)
                _sparks[i].Age = 1;

            Stop();
            InvalidateVisual();
            return;
        }

        Step(dt, calm, profile);
        InvalidateVisual();
    }

    private void Step(double dt, double calm, EffectsProfile profile)
    {
        // 0 — «почти покой», 1 — полная яркость; за 600 мс (спека 8.1).
        double target = _mode is FieldMode.Off or FieldMode.Failed ? 0 : 1;
        _glow += Math.Clamp(target - _glow, -dt / .6, dt / .6);

        double drift = (.6 + .4 * _glow) * calm;
        double decay = Math.Exp(-dt / .4);
        bool full = profile == EffectsProfile.Full;
        double repelR = full ? 60 : 45;

        // Курсор: появляется быстро, гаснет за 300 мс.
        _cursorA = Math.Clamp(_cursorA + (_cursorOn ? dt / .12 : -dt / .3), 0, 1);

        double follow = 1 - Math.Exp(-dt * 30);
        _hx += (_px - _hx) * follow;
        _hy += (_py - _hy) * follow;

        for (int i = 0; i < _count; i++)
        {
            if (_cursorA > 0)
            {
                double dx = _x[i] - _hx, dy = _y[i] - _hy, d2 = dx * dx + dy * dy;

                if (d2 < repelR * repelR && d2 > .01)
                {
                    double d = Math.Sqrt(d2), push = (1 - d / repelR) * 40 * _cursorA;
                    _kx[i] = Math.Clamp(_kx[i] + dx / d * push * dt * 8, -40, 40);
                    _ky[i] = Math.Clamp(_ky[i] + dy / d * push * dt * 8, -40, 40);
                }
            }

            _kx[i] *= decay;
            _ky[i] *= decay;
            _x[i] = Wrap(_x[i] + (_vx[i] * drift + _kx[i]) * dt, _w);
            _y[i] = Wrap(_y[i] + (_vy[i] * drift + _ky[i]) * dt, _h);
        }

        StepSparks(dt, full);
        StepPackets(dt, profile);
    }

    private static double Wrap(double v, double size) => v < 0 ? v + size : v >= size ? v - size : v;

    private void StepSparks(double dt, bool full)
    {
        double life = full ? .45 : .35;

        for (int i = 0; i < MaxSparks; i++)
            if (_sparks[i].Age < 1)
                _sparks[i].Age += dt / life;
    }

    private void StepPackets(double dt, EffectsProfile profile)
    {
        int cap = profile == EffectsProfile.Full ? 6 : 3;
        double link = LinkRadius(profile);

        for (int i = 0; i < _nPackets; i++)
        {
            ref var k = ref _packets[i];
            double dx = _x[k.To] - _x[k.From], dy = _y[k.To] - _y[k.From];
            double len = Math.Max(20, Math.Sqrt(dx * dx + dy * dy));
            k.T += 90 * dt / len;

            if (k.T < 1)
                continue;

            // Дошёл: либо дальше по связям (затухающий), либо исчез.
            int next = ++k.Hop < k.Hops ? Neighbor(k.To, k.From, link) : -1;

            if (next < 0)
            {
                _packets[i--] = _packets[--_nPackets];
                continue;
            }

            k.From = k.To;
            k.To = next;
            k.T = 0;
        }

        double rate = 0;

        switch (_mode)
        {
            case FieldMode.Starting:
                rate = 1.5;
                break;
            case FieldMode.Running:
                rate = Mbps is { } m
                    ? .8 + 2.2 * Math.Clamp(Math.Log10(1 + Math.Max(0, m)) / 3, 0, 1)
                    : .8;
                break;
            case FieldMode.Degraded:
                rate = .4;
                break;
            case FieldMode.Failed:
                _failAcc += dt;

                if (_failAcc >= 4)
                {
                    _failAcc = 0;
                    Launch(Role.PacketDanger, 2, cap, link);
                }

                break;
        }

        _spawnAcc += rate * dt;

        while (_spawnAcc >= 1)
        {
            _spawnAcc -= 1;

            var role = _mode switch
            {
                FieldMode.Starting => Role.PacketStart,
                FieldMode.Degraded => Role.PacketWarn,
                _ => Role.Packet,
            };

            Launch(role, 1, cap, link);
        }
    }

    /// <summary>Сосед в радиусе связи, не равный <paramref name="skip"/>; -1, если нет.</summary>
    private int Neighbor(int node, int skip, double link)
    {
        int found = -1, seen = 0;
        double r2 = link * link;

        for (int j = 0; j < _count; j++)
        {
            if (j == node || j == skip)
                continue;

            double dx = _x[j] - _x[node], dy = _y[j] - _y[node];

            // Случайный из подходящих (резервуарная выборка), без списка.
            if (dx * dx + dy * dy < r2 && _rng.Next(++seen) == 0)
                found = j;
        }

        return found;
    }

    private void Launch(Role role, int hops, int cap, double link)
    {
        if (_nPackets >= cap || _count < 2)
            return;

        int from = _rng.Next(_count);

        // Запуск идёт слева направо: берём узел у левого края, если нашёлся.
        if (role == Role.PacketStart)
        {
            for (int tries = 0; tries < 8 && _x[from] > _w * .15; tries++)
                from = _rng.Next(_count);
        }

        int to = Neighbor(from, -1, link);

        if (to < 0)
            return;

        _packets[_nPackets++] = new Packet { From = from, To = to, Hops = hops, Role = role };
    }

    // ---- кадр: рисование ---------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var profile = Motion.Profile;

        if (Shots || profile == EffectsProfile.Lite || _count == 0 || _w <= 0)
            return;

        EnsureCache();

        double nodeF = .733 + .267 * _glow, linkF = .571 + .429 * _glow;
        double link = LinkRadius(profile), r2 = link * link;
        int lines = 0;

        // Узлы.
        int nodeLevel = Level(nodeF);

        for (int i = 0; i < _count; i++)
            dc.DrawEllipse(_brushes[(int)Role.Node, nodeLevel], null, new Point(_x[i], _y[i]), _size[i], _size[i]);

        // Связи между узлами: пары, в радиусе связи.
        for (int i = 0; i < _count && lines < MaxLines; i++)
        {
            for (int j = i + 1; j < _count; j++)
            {
                double dx = _x[i] - _x[j], dy = _y[i] - _y[j], d2 = dx * dx + dy * dy;

                if (d2 >= r2)
                    continue;

                double f = (1 - Math.Sqrt(d2) / link) * linkF;

                if (f < .06)
                    continue;

                dc.DrawLine(_pens[(int)Role.Link, Level(f)]!, new Point(_x[i], _y[i]), new Point(_x[j], _y[j]));

                if (++lines >= MaxLines)
                    break;
            }
        }

        if (_cursorA > 0)
            DrawCursor(dc, profile);

        DrawPackets(dc);
        DrawSparks(dc);
    }

    private static int Level(double f) => Math.Clamp((int)(f * 8), 0, 7);

    private void DrawCursor(DrawingContext dc, EffectsProfile profile)
    {
        bool full = profile == EffectsProfile.Full;
        int cap = full ? 6 : 4;
        double range = full ? 140 : 110, r2 = range * range;
        int n = 0;

        // N ближайших вставкой в маленький отсортированный массив.
        for (int i = 0; i < _count; i++)
        {
            double dx = _x[i] - _hx, dy = _y[i] - _hy, d2 = dx * dx + dy * dy;

            if (d2 >= r2 || (n == cap && d2 >= _nearD[n - 1]))
                continue;

            int k = n < cap ? n++ : n - 1;

            while (k > 0 && _nearD[k - 1] > d2)
            {
                _nearD[k] = _nearD[k - 1];
                _near[k] = _near[k - 1];
                k--;
            }

            _nearD[k] = d2;
            _near[k] = i;
        }

        var head = new Point(_hx, _hy);

        for (int k = 0; k < n; k++)
        {
            double f = (1 - Math.Sqrt(_nearD[k]) / range) * _cursorA;

            if (f >= .06)
                dc.DrawLine(_pens[(int)Role.Cursor, Level(f)]!, head, new Point(_x[_near[k]], _y[_near[k]]));
        }

        dc.DrawEllipse(_brushes[(int)Role.Cursor, Level(_cursorA)], null, head, 2, 2);
    }

    private void DrawPackets(DrawingContext dc)
    {
        for (int i = 0; i < _nPackets; i++)
        {
            ref readonly var k = ref _packets[i];
            double dx = _x[k.To] - _x[k.From], dy = _y[k.To] - _y[k.From];
            double len = Math.Max(1, Math.Sqrt(dx * dx + dy * dy)), ux = dx / len, uy = dy / len;
            double x = _x[k.From] + dx * k.T, y = _y[k.From] + dy * k.T;

            // Затухающий пакет гаснет за все свои прыжки.
            double f = k.Hops > 1 ? 1 - (k.Hop + k.T) / k.Hops : 1;
            var head = new Point(x, y);

            // Хвост 6 px: два отрезка, дальний вдвое бледнее.
            var pen = _pens[(int)k.Role, Level(f * .9)]!;
            var dim = _pens[(int)k.Role, Level(f * .45)]!;
            dc.DrawLine(pen, head, new Point(x - ux * 3, y - uy * 3));
            dc.DrawLine(dim, new Point(x - ux * 3, y - uy * 3), new Point(x - ux * 6, y - uy * 6));
            dc.DrawEllipse(_brushes[(int)k.Role, Level(f)], null, head, 1.6, 1.6);
        }
    }

    private void DrawSparks(DrawingContext dc)
    {
        for (int i = 0; i < MaxSparks; i++)
        {
            ref readonly var s = ref _sparks[i];

            if (s.Age >= 1)
                continue;

            double e = 1 - Math.Pow(1 - s.Age, 3), d = s.Dist * e;
            double x = s.X + s.Dx * d, y = s.Y + s.Dy * d;
            var role = s.Age < .5 ? Role.Spark : Role.SparkText;

            dc.DrawLine(_pens[(int)role, Level(1 - s.Age)]!, new Point(x, y), new Point(x - s.Dx * 3, y - s.Dy * 3));
        }
    }

    // ---- кисти и перья --------------------------------------------------------------

    private Color Resolve(string key)
    {
        if (TryFindResource(key) is SolidColorBrush b)
            return b.Color;

        return Colors.Gray;
    }

    /// <summary>Пересобирает кэш, если тема сменила цвет любого из ключей.</summary>
    private void EnsureCache()
    {
        bool stale = !_cacheBuilt;

        for (int r = 0; r < Keys.Length; r++)
        {
            var c = Resolve(Keys[r]);

            if (c != _colors[r])
            {
                _colors[r] = c;
                stale = true;
            }
        }

        if (!stale)
            return;

        // Тёмный текст — светлая тема.
        var text = _colors[(int)Role.Link];
        _lightTheme = (text.R * 299 + text.G * 587 + text.B * 114) / 1000 < 128;
        var max = _lightTheme ? MaxLight : MaxDark;

        for (int r = 0; r < Keys.Length; r++)
        {
            for (int l = 0; l < 8; l++)
            {
                double a = max[r] * _dim * (l + 1) / 8;
                var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(a * 255), _colors[r].R, _colors[r].G, _colors[r].B));
                brush.Freeze();

                double thickness = (Role)r == Role.Spark || (Role)r == Role.SparkText ? 1.2 : 1;
                var pen = new Pen(brush, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                pen.Freeze();

                _brushes[r, l] = brush;
                _pens[r, l] = pen;
            }
        }

        _cacheBuilt = true;
    }
}
