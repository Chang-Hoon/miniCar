// .NET Framework 4.8 에는 없는 API 보충

global using System;
global using System.Collections.Generic;
global using System.Drawing;
global using System.IO;
global using System.Linq;
global using System.Threading;
global using System.Windows.Forms;

namespace System.Runtime.CompilerServices
{
    /// <summary>record / init 접근자용 (C# 9+ 기능을 net48에서 쓰기 위함)</summary>
    internal static class IsExternalInit { }
}

namespace miniCar
{
    internal static class MathX
    {
        public static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
        public static double Clamp(double v, double min, double max) => v < min ? min : v > max ? max : v;
    }
}
