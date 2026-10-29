using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Threading;

namespace FbtCal
{
    // Beeps on their own thread: a blocking beep on the VR thread would stall the heartbeat and
    // drop the override for a moment.
    public static class Sound
    {
        [DllImport("kernel32.dll")]
        static extern bool Beep(uint freq, uint ms);

        static readonly BlockingCollection<(uint freq, uint ms)> Queue = new BlockingCollection<(uint, uint)>();

        static Sound()
        {
            new Thread(() =>
            {
                foreach (var (freq, ms) in Queue.GetConsumingEnumerable())
                {
                    if (freq == 0) Thread.Sleep((int)ms);
                    else Beep(freq, ms);
                }
            }) { IsBackground = true, Name = "fbtcal-sound" }.Start();
        }

        public static void Play(params (uint freq, uint ms)[] notes)
        {
            foreach (var n in notes) Queue.Add(n);
        }

        public static void Tick() => Play((600, 60));
        public static void Ready() => Play((700, 120));
        public static void Go() => Play((1200, 300));
        public static void Done() => Play((900, 100), (0, 60), (900, 100));
        public static void On() => Play((900, 120), (1200, 200));     // ideal pose on
        public static void Off() => Play((900, 100), (700, 150));     // back to the real pose
        public static void Problem() => Play((400, 500));
    }
}
