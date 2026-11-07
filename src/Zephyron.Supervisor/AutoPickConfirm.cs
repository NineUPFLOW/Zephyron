namespace Zephyron.Supervisor;

/// <summary>
/// Подтверждение победителя автоподбора: три удачные пробы подряд.
/// </summary>
/// <remarks>
/// Способ, открывший имя один-два раза, бывает случайным (DPI пропускает через раз).
/// Срыв на любой пробе отклоняет кандидата — берётся следующий. Подтверждается
/// только победитель, а не каждая проба прогона, чтобы подбор не замедлился.
/// </remarks>
public static class AutoPickConfirm
{
    public const int Need = 3;

    /// <summary>Пауза между пробами: за секунду успевает смениться состояние у DPI.</summary>
    public static readonly TimeSpan Pause = TimeSpan.FromSeconds(1);

    /// <param name="Passed">Сколько проб подряд прошло до срыва (или до конца).</param>
    public sealed record Result(int Passed, int Need)
    {
        public bool Confirmed => Passed >= Need;

        /// <summary>«подтверждено 3/3» либо «сорвалось на 2/3» (номер сорвавшейся пробы).</summary>
        public string Text => Confirmed ? $"подтверждено {Need}/{Need}" : $"сорвалось на {Passed + 1}/{Need}";
    }

    /// <summary>Гоняет пробу до срыва либо до <paramref name="need"/> удач подряд.</summary>
    /// <param name="probe">Одна проба; <c>true</c> — открылось.</param>
    /// <param name="pause">Ожидание между пробами (не перед первой и не после последней).</param>
    public static async Task<Result> RunAsync(Func<Task<bool>> probe, Func<Task> pause, int need = Need)
    {
        int passed = 0;

        while (passed < need)
        {
            if (passed > 0)
                await pause();

            if (!await probe())
                break;

            passed++;
        }

        return new Result(passed, need);
    }
}
