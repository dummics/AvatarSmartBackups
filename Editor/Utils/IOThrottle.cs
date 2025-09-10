#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace AvatarSmartBackup
{
    internal static class IOThrottle
    {
        // Copia stream -> stream con throttle (MB/s). 0 = illimitato.
        public static void CopyStreamThrottled(Stream src, Stream dst, int bufferBytes, int maxMBps, CancellationToken ct)
        {
            byte[] buffer = new byte[bufferBytes];
            var sw = Stopwatch.StartNew();
            long total = 0;
            double maxBps = maxMBps <= 0 ? double.PositiveInfinity : maxMBps * 1024.0 * 1024.0;

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int r = src.Read(buffer, 0, buffer.Length);
                if (r <= 0) break;
                dst.Write(buffer, 0, r);
                total += r;

                if (maxMBps > 0)
                {
                    double elapsed = sw.Elapsed.TotalSeconds;
                    if (elapsed > 0)
                    {
                        double bps = total / elapsed;
                        if (bps > maxBps)
                        {
                            // tempo desiderato per scrivere 'total' a maxBps
                            double desired = total / maxBps;
                            int sleepMs = (int)Math.Max(1, (desired - elapsed) * 1000.0);
                            Thread.Sleep(sleepMs);
                        }
                    }
                }
            }
        }
    }
}
#endif

