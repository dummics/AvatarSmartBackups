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
        // Migliorato per reliability: throttling più preciso e meno aggressivo
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
                    if (elapsed > 0.1) // Only throttle after meaningful time elapsed
                    {
                        double bps = total / elapsed;
                        if (bps > maxBps)
                        {
                            // Conservative throttling: use smaller sleep increments for smoother operation
                            double desired = total / maxBps;
                            int sleepMs = (int)Math.Max(1, Math.Min(100, (desired - elapsed) * 1000.0)); // Cap sleep at 100ms
                            
                            if (sleepMs > 0)
                            {
                                System.Threading.Thread.Sleep(sleepMs);
                                System.Threading.Thread.Yield(); // Allow other threads to run
                            }
                        }
                    }
                }
                
                // Periodic yield for UI responsiveness during large file operations
                if (total % (2 * 1024 * 1024) == 0) // Every 2MB
                {
                    System.Threading.Thread.Yield();
                }
            }
        }
    }
}
#endif

