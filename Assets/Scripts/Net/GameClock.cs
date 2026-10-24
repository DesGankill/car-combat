using UnityEngine;

namespace CarCombat
{
    public static class GameClock
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Boot()
        {
            Time.fixedDeltaTime = CarSim.TickDt;
            Application.runInBackground = true;
            Application.targetFrameRate = 60;
        }
    }
}
