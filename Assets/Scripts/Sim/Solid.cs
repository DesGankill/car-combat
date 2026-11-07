using UnityEngine;

namespace CarCombat
{
    /// <summary>
    /// Каталог снарядов. Клавиши 1, 2, 3… выбирают строку по порядку.
    /// Сервер читает только номер и берёт урон отсюда, а не из пакета.
    /// </summary>
    public static class Solid
    {
        public struct Shell
        {
            public string title;
            public int directDamage;
            public float splashRadius;
            public int splashDamage;
            public Color color;
        }

        public static readonly Shell[] All =
        {
            new Shell
            {
                title = "Бронебойный",
                directDamage = 4,
                splashRadius = 0f,
                splashDamage = 0,
                color = new Color(0.62f, 0.66f, 0.72f)
            },
            new Shell
            {
                title = "Фугасный",
                directDamage = 2,
                splashRadius = 1.15f,
                splashDamage = 1,
                color = new Color(0.86f, 0.42f, 0.14f)
            }
        };

        public static int Count => All.Length;

        public static Shell Of(int index)
        {
            if (index < 0 || index >= All.Length)
                return All[0];
            return All[index];
        }
    }
}
