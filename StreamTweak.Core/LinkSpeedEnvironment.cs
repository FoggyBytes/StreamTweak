using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;

namespace StreamTweak
{
    /// <summary>
    /// Everything <see cref="LinkSpeedManager"/> touches outside itself: the adapter, the clock,
    /// and the timer. It exists so the manager's decisions can be replayed against virtual time
    /// instead of a real NIC — this feature costs a multi-second network blackout to test by hand,
    /// which is exactly why the first two runtime attempts each burned an evening to find a bug
    /// that a replay would have caught in a second.
    /// </summary>
    public interface ILinkSpeedEnvironment
    {
        long GetCurrentMbps(string adapter);
        List<LinkSpeedOption> GetSupportedSpeedOptions(string adapter);
        LinkSpeedOption? GetCurrentSpeedSetting(string adapter);
        List<string> GetManageableAdapterNames();

        /// <summary>Applies the *SpeedDuplex setting. Returns false when the adapter refused it.</summary>
        bool Apply(string adapter, string registryValue);

        /// <summary>True when <paramref name="local"/> is an address of <paramref name="adapter"/>.</summary>
        bool IsOnManagedAdapter(string adapter, IPAddress local);

        DateTime UtcNow { get; }
        Task Delay(int milliseconds);

        /// <summary>Runs <paramref name="action"/> once after <paramref name="delay"/>.
        /// Disposing the handle cancels it.</summary>
        IDisposable ScheduleOnce(TimeSpan delay, Action action);
    }

    /// <summary>The production environment: the real NIC, the real clock.</summary>
    public sealed class RealLinkSpeedEnvironment : ILinkSpeedEnvironment
    {
        public long GetCurrentMbps(string adapter) => NetworkManager.GetCurrentLinkMbps(adapter);

        public List<LinkSpeedOption> GetSupportedSpeedOptions(string adapter)
            => NetworkManager.GetSupportedSpeedOptions(adapter);

        public LinkSpeedOption? GetCurrentSpeedSetting(string adapter)
            => NetworkManager.GetCurrentSpeedSetting(adapter);

        public List<string> GetManageableAdapterNames() => NetworkManager.GetManageableAdapterNames();

        public bool Apply(string adapter, string registryValue)
            => SpeedChanger.Apply(adapter, registryValue)
            || SpeedChanger.ApplyWithUac(adapter, registryValue);

        public bool IsOnManagedAdapter(string adapter, IPAddress local)
        {
            try
            {
                var ni = NetworkInterface.GetAllNetworkInterfaces()
                    .FirstOrDefault(n => n.Name.Equals(adapter, StringComparison.OrdinalIgnoreCase));
                if (ni == null) return false;

                return ni.GetIPProperties().UnicastAddresses.Any(u => u.Address.Equals(local));
            }
            catch { return false; }
        }

        public DateTime UtcNow => DateTime.UtcNow;

        public Task Delay(int milliseconds) => Task.Delay(milliseconds);

        public IDisposable ScheduleOnce(TimeSpan delay, Action action)
            => new TimerHandle(delay, action);

        private sealed class TimerHandle : IDisposable
        {
            private readonly Timer _timer;

            public TimerHandle(TimeSpan delay, Action action)
            {
                _timer = new Timer(_ => action(), null, delay, Timeout.InfiniteTimeSpan);
            }

            public void Dispose() => _timer.Dispose();
        }
    }
}
