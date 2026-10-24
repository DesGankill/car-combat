using System;

namespace CarCombat
{
    /// <summary>
    /// Аналог NetEmulation.PktLag и PktLoss.
    /// Задержка — в одну сторону, и на отправку команды, и на применение ответа сервера.
    /// На listen-сервере свои пакеты не задерживаются: хост и так локальный.
    /// </summary>
    public static class NetEmulation
    {
        public static int OneWayLagMs;
        public static int LossPercent;
        static readonly Random Rng = new Random(12345);

        public static bool ShouldDrop()
        {
            if (LossPercent <= 0)
                return false;
            return Rng.Next(100) < LossPercent;
        }

        public static void Reset(int lagMs, int lossPercent)
        {
            OneWayLagMs = lagMs < 0 ? 0 : lagMs;
            LossPercent = lossPercent < 0 ? 0 : lossPercent > 100 ? 100 : lossPercent;
        }
    }
}
